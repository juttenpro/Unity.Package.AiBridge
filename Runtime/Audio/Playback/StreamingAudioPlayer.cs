using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using Tsc.AIBridge.Audio.VAD;

namespace Tsc.AIBridge.Audio.Playback
{
    /// <summary>
    /// Unified audio pipeline state. Single source of truth for all audio components.
    /// ShuttingDown is the key state: it blocks ALL new audio until explicitly reset via ResetPipeline().
    /// </summary>
    public enum AudioPipelineState
    {
        /// <summary>No audio active, ready for new streams or scripted clips.</summary>
        Idle,
        /// <summary>Receiving and playing streaming AI audio via WebSocket.</summary>
        Streaming,
        /// <summary>Playing a scripted Addressable AudioClip.</summary>
        PlayingScripted,
        /// <summary>Playback paused (PauseManager, VR headset off). Can resume.</summary>
        Paused,
        /// <summary>Terminal state: all audio forcibly stopped, rejects all new audio.
        /// Only transitions to Idle via explicit ResetPipeline() call.</summary>
        ShuttingDown,
        /// <summary>OnDestroy called — everything cleaned up.</summary>
        Destroyed
    }

    /// <summary>
    /// Real-time streaming audio player optimized for low-latency AI voice playback.
    /// Uses Unity's OnAudioFilterRead callback for true streaming without pre-buffering entire clips.
    /// Key features:
    /// - Plays audio chunks immediately as they arrive from TTS
    /// - Dynamic buffering with configurable minimum threshold
    /// - Automatic sample rate adaptation
    /// - Thread-safe concurrent audio queue
    /// - Underrun detection and recovery
    /// - Pause/resume support for Unity Editor
    /// Works in conjunction with AudioFilterRelay for flexible audio source placement.
    /// </summary>
    public class StreamingAudioPlayer : MonoBehaviour
    {
        // Static events for logging (prefixed to avoid naming conflicts)
        public static event Action OnPlaybackStartedStatic;

        // Static test mode flag - can be set before component creation
        private static bool _globalSuppressWarnings = false;

        [Header("Audio Configuration")]
        [SerializeField]
        [Tooltip("REQUIRED: Audio filter relay component. Must be assigned in the Inspector! Should be on the same GameObject as the AudioSource.")]
        private AudioFilterRelay audioFilterRelay;

        // Instance test mode flag - suppress initialization warnings when true
        private bool _suppressInitializationWarnings = false;

        // Volume control should be done via AudioSource.volume, not with gain multipliers!
        // Buffering is now handled by centralized AdaptiveBufferManager

        [Header("Playback Detection")]
        [SerializeField]
        [Tooltip("Safety-net timeout: complete playback if the orchestrator never sends an AudioStreamEnd message AND the buffer stays empty this long. Primary completion is driven by the explicit AudioStreamEnd signal — this is only a fallback for server crashes.")]
        [Range(1.0f, 10.0f)]
        private float playbackCompleteTimeout = 3.0f;

        [Header("Debug Settings")]
        [SerializeField]
        [Tooltip("Enable verbose logging for debugging audio streaming and buffering")]
        protected bool enableVerboseLogging;

        // Events
        [Header("Events")]
        public UnityEvent OnPlaybackStarted = new();
        public UnityEvent OnFirstAudioPlayed = new(); // First audio sample actually output (for accurate latency)
        public UnityEvent OnPlaybackComplete = new();  // Natural end of audio
        public UnityEvent OnPlaybackInterrupted = new(); // Audio stopped by interruption

        // Ring buffer for audio samples - no size limit, grows as needed
        private readonly ConcurrentQueue<float> _audioBuffer = new();

        // Unified pipeline state — single source of truth for all audio components.
        // volatile because audio thread reads in FillAudioBuffer, main thread writes.
        private volatile AudioPipelineState _pipelineState = AudioPipelineState.Idle;
        private AudioPipelineState _stateBeforePause;

        /// <summary>
        /// Current pipeline state. Used by NpcAudioPlayer, NpcClientBase, and AudioStreamProcessor
        /// to guard operations against ShuttingDown state.
        /// </summary>
        public AudioPipelineState PipelineState => _pipelineState;

        // State
        // IMPORTANT: This receives DOWNSTREAM audio from TTS at 48kHz
        // (Different from UPSTREAM microphone capture at 16kHz)
        private int _sampleRate = 48000; // Default to TTS output frequency
        private bool _isStreamActive;
        private bool _isPlaybackStarted;
        private bool _streamComplete;
        private bool _shouldStop;
        private bool _isPaused;
        // Tracks WHICH sources have paused the pipeline (e.g., External=PauseManager,
        // OsFocusLoss=Quest Home, OsApplicationPause=headset off, EditorPause=Unity Editor).
        // Audio only resumes when this set is empty. Replaces the single "_isPausedByExternalSource"
        // flag that couldn't distinguish OS pauses from training pauses and caused the
        // Quest Home-button stuck-audio regression.
        private readonly HashSet<PauseReason> _activePauseReasons = new();
        private bool _isReceivingResponse; // NEW: Track if we're still receiving response from backend
        private bool _forceStop; // CRITICAL: Force audio to stop immediately for interruptions
        private bool _isPrimingBuffer; // PRIMING BUFFER: Use larger buffer for first chunks to prevent "catching up"
        private bool _hasFirstAudioPlayed; // Track if first audio sample has been output (for accurate latency)
        private bool _hasLoggedFirstAudio; // Separate flag to prevent repeated logging (not reset by Update())
        private int _totalSamplesReceived;
        private int _totalSamplesPlayed;
        private readonly object _stateLock = new();

        // Auto-detect playback completion without backend messages
        private float _lastDataReceivedTime;

        // Set by the orchestrator's AudioStreamEnd control message (via AudioMessageHandler →
        // AudioStreamProcessor). Primary trigger for playback completion: when this flips true
        // AND the buffer is empty, finalize immediately. Cleared on every StartStream so a stale
        // signal from turn N never leaks into turn N+1.
        private volatile bool _serverStreamEnd;

        /// <summary>True once the orchestrator has signalled end-of-audio for the current turn.</summary>
        public bool IsServerStreamEnd => _serverStreamEnd;

        /// <summary>
        /// Safety-net timeout (seconds): how long to keep the stream open after the buffer empties
        /// when no AudioStreamEnd has arrived. Set deliberately high so normal inter-sentence
        /// network jitter cannot trip it; only a server crash should reach this fallback.
        /// </summary>
        public float SafetyNetCompletionTimeoutSeconds => playbackCompleteTimeout;

        /// <summary>
        /// Marks the audio stream as ended by an explicit server signal (AudioStreamEnd message).
        /// Combined with an empty buffer, this triggers immediate playback completion in the next
        /// Update tick.
        /// </summary>
        public void MarkServerStreamEnd()
        {
            _serverStreamEnd = true;
            if (enableVerboseLogging)
            {
                Debug.Log($"[{_cachedGameObjectName}] Server signalled end-of-audio-stream");
            }
        }

        /// <summary>
        /// Pure decision function for "should playback finalize now?". All inputs are passed in
        /// so the function is testable without driving the Update loop or mutating private state.
        /// Primary path: server signalled end + buffer drained → complete.
        /// Fallback path: no signal but buffer has been empty longer than the safety-net timeout
        /// → complete (handles the rare server-crash case).
        /// </summary>
        public bool EvaluateAutoComplete(bool isPlaybackStarted, bool bufferEmpty, float timeSinceLastData)
        {
            if (!isPlaybackStarted || !bufferEmpty)
            {
                return false;
            }

            if (_serverStreamEnd)
            {
                return true;
            }

            return timeSinceLastData > playbackCompleteTimeout;
        }

        // Cached values
        private string _cachedGameObjectName;
        private int _minBufferSamples;

        // PRIMING BUFFER CONFIGURATION
        // Use a larger buffer for the first audio stream to prevent "catching up" audio playback
        // This accounts for initial overhead: WebSocket arrival, RequestId header stripping, OGG/Opus decoder init
        // After first playback starts, falls back to normal adaptive buffering
        private const float PRIMING_BUFFER_DURATION = 0.25f; // 250ms vs normal 100ms

        // Statistics
        private float _lastBufferLevel;
        private int _underrunCount;
        private bool _cachedIsPlaying; // Thread-safe cache of AudioSource.isPlaying
        private int _samplesPlayedSinceStart; // Track samples played since playback started
        private const int UNDERRUN_GRACE_PERIOD_SAMPLES = 24000; // ~500ms at 48kHz - ignore underruns during buffer stabilization

        public bool IsPlaybackActive => _isStreamActive && _cachedIsPlaying;
        public bool HasBufferedAudio => _audioBuffer.Count > 0;
        public float BufferLevel => _audioBuffer.Count / (float)_sampleRate; // In seconds

        #region Pipeline State Machine

        /// <summary>
        /// Attempt a state transition. Returns false if blocked (e.g., ShuttingDown blocks all
        /// transitions except → Idle via ResetPipeline and → Destroyed).
        /// </summary>
        protected bool TryTransitionTo(AudioPipelineState newState)
        {
            if (_pipelineState == AudioPipelineState.ShuttingDown
                && newState != AudioPipelineState.Idle
                && newState != AudioPipelineState.Destroyed)
            {
                if (enableVerboseLogging)
                    Debug.Log($"[{_cachedGameObjectName}] BLOCKED transition {_pipelineState} → {newState} (pipeline is shut down)");
                return false;
            }

            if (_pipelineState == AudioPipelineState.Destroyed)
                return false;

            if (enableVerboseLogging)
                Debug.Log($"[{_cachedGameObjectName}] Pipeline: {_pipelineState} → {newState}");
            _pipelineState = newState;
            return true;
        }

        /// <summary>
        /// Forcefully shut down the audio pipeline. Sets ShuttingDown state which blocks all
        /// new audio (StartStream, AddAudioData, ProcessAudioQueue, RestoreStreamingMode).
        /// Only ResetPipeline() can bring it back to Idle.
        /// Use for: orb close, NPC deactivation (SetActive(false)).
        /// Do NOT use for interruptions — use StopPlayback() instead (transitions to Idle).
        /// </summary>
        public void RequestShutdown()
        {
            _pipelineState = AudioPipelineState.ShuttingDown; // Direct set, bypass guard
            _forceStop = true;

            lock (_stateLock)
            {
                StopPlaybackInternal(wasInterrupted: true);
            }

            // Mute AudioSource as final action — nothing can undo this while ShuttingDown
            if (audioFilterRelay != null && audioFilterRelay.AudioSource != null)
            {
                audioFilterRelay.AudioSource.mute = true;
            }
        }

        /// <summary>
        /// Re-enable the pipeline after shutdown. Transitions from ShuttingDown → Idle.
        /// Called by OnEnable() when NPC is reactivated, or when coach orb opens again.
        /// </summary>
        public void ResetPipeline()
        {
            if (_pipelineState == AudioPipelineState.ShuttingDown)
            {
                _pipelineState = AudioPipelineState.Idle;
                _forceStop = false;

                // Unmute AudioSource so it's ready for next audio
                if (audioFilterRelay != null && audioFilterRelay.AudioSource != null)
                {
                    audioFilterRelay.AudioSource.mute = false;
                }

                if (enableVerboseLogging)
                    Debug.Log($"[{_cachedGameObjectName}] Pipeline reset: ShuttingDown → Idle");
            }
        }

        #endregion

        // Get buffer settings from centralized manager (if it exists)
        public float MinBufferDuration => AdaptiveBufferManager.HasInstance
            ? (AdaptiveBufferManager.Instance?.CurrentBufferDuration ?? 0.1f)
            : 0.1f;
        public bool IsAdaptiveBufferingEnabled => !AdaptiveBufferManager.HasInstance || (AdaptiveBufferManager.Instance?.IsAdaptiveBufferingEnabled ?? true);

        /// <summary>
        /// Gets the AudioFilterRelay component (read-only access)
        /// </summary>
        public AudioFilterRelay AudioFilterRelay => audioFilterRelay;

        /// <summary>
        /// Sets the AudioFilterRelay programmatically (primarily for testing).
        /// In production, this should be set via Inspector on the prefab.
        /// </summary>
        public void SetAudioFilterRelay(AudioFilterRelay relay)
        {
            audioFilterRelay = relay;
            _suppressInitializationWarnings = true; // We've set it programmatically, no need for warnings
            if (relay != null)
            {
                relay.SetStreamingPlayer(this, enableVerboseLogging);
                UpdateBufferSizes();
                if (enableVerboseLogging)
                    Debug.Log($"[{_cachedGameObjectName}] AudioFilterRelay set programmatically");
            }
        }

        /// <summary>
        /// Suppresses initialization warnings - for test use only.
        /// This prevents warning logs when AudioFilterRelay is not assigned yet.
        /// </summary>
        public void SuppressInitializationWarnings()
        {
            _suppressInitializationWarnings = true;
        }

        /// <summary>
        /// Globally suppresses initialization warnings for all new instances - for test use only.
        /// Call this BEFORE creating StreamingAudioPlayer components in tests.
        /// </summary>
        public static void SetGlobalTestMode(bool suppressWarnings)
        {
            _globalSuppressWarnings = suppressWarnings;
        }

        /// <summary>
        /// True if we're still receiving a response from the backend (even during pauses in audio)
        /// This remains true until AudioStreamEnd is received, allowing interruption during natural pauses
        /// </summary>
        public bool IsReceivingResponse => _isReceivingResponse;

        /// <summary>
        /// True if NPC is currently producing audible speech (not just silent)
        /// Used for accurate interruption detection - only interrupt when BOTH player and NPC are talking
        /// </summary>
        public bool IsNPCSpeaking { get; private set; }

        /// <summary>
        /// Set the NPC speech state from AudioFilterRelay VAD processing
        /// Called every audio frame to update real-time speech detection
        /// </summary>
        public void SetNPCSpeechState(bool isSpeaking)
        {
            IsNPCSpeaking = isSpeaking;
        }

        /// <summary>
        /// Get the last audio frame that was played by this NPC
        /// Used by InterruptionManager for audio feedback detection
        /// </summary>
        private float[] _lastPlayedAudioFrame;
        public float[] GetLastPlayedAudioFrame() => _lastPlayedAudioFrame;

        // VAD processor for NPC speech detection (actual speech vs pauses)
        private SimpleVADProcessor _npcVadProcessor;

        protected virtual void Awake()
        {
            _cachedGameObjectName = gameObject.name;

            // Check global test mode flag
            if (_globalSuppressWarnings)
            {
                _suppressInitializationWarnings = true;
            }

            // Only start initialization coroutine if not suppressing warnings (test mode)
            // In test mode, SetAudioFilterRelay will be called manually
            if (!_suppressInitializationWarnings)
            {
                StartCoroutine(InitializeWithRetry());
            }

            #if UNITY_EDITOR
            // Subscribe to editor pause state changes
            UnityEditor.EditorApplication.pauseStateChanged += OnEditorPauseStateChanged;
            #endif
        }

        protected virtual void Start()
        {
            // Subscribe to buffer updates from centralized manager (optional component)
            // NOTE: In Editor, Main scene may load before Initializer scene (development workflow)
            // In Build, Initializer always loads first (production workflow)
            // HasInstance check handles both scenarios gracefully
            if (AdaptiveBufferManager.HasInstance)
            {
                var bufferManager = AdaptiveBufferManager.Instance;
                if (bufferManager != null)
                {
                    bufferManager.OnBufferUpdateEvent += OnBufferUpdateReceived;
                    if (enableVerboseLogging)
                        Debug.Log($"[{_cachedGameObjectName}] Subscribed to centralized buffer update events");
                }
            }
            else if (enableVerboseLogging)
            {
                Debug.Log($"[{_cachedGameObjectName}] AdaptiveBufferManager not found - using default buffer settings");
            }

            // Initialize NPC VAD processor for speech detection (pauses vs actual speech)
            // Low threshold (0.001) for clean TTS audio - no environmental noise
            _npcVadProcessor = new SimpleVADProcessor($"NPC-{_cachedGameObjectName}", volumeThreshold: 0.001f, isVerboseLogging: enableVerboseLogging);

            // Subscribe to AudioFilterRelay audio processing events
            if (audioFilterRelay != null)
            {
                audioFilterRelay.OnAudioProcessed += HandleAudioProcessed;
                if (enableVerboseLogging)
                    Debug.Log($"[{_cachedGameObjectName}] NPC VAD initialized and subscribed to AudioFilterRelay");
            }
            else if (enableVerboseLogging)
            {
                Debug.LogWarning($"[{_cachedGameObjectName}] AudioFilterRelay not available - NPC speech detection disabled");
            }
        }

        #if UNITY_EDITOR
        private void OnEditorPauseStateChanged(UnityEditor.PauseState state)
        {
            if(enableVerboseLogging)
                Debug.Log($"[{_cachedGameObjectName}] Editor pause state changed: {state}");

            if (state == UnityEditor.PauseState.Paused)
            {
                PausePlayback(PauseReason.EditorPause);
            }
            else if (state == UnityEditor.PauseState.Unpaused)
            {
                ResumePlayback(PauseReason.EditorPause);
            }
        }
        #endif

        /// <summary>
        /// Attempts to initialize the audio playback system with retry logic.
        /// Searches for AudioFilterRelay component in multiple locations (self, head, parent)
        /// and validates audio configuration. Retries up to 5 times with progressive delays.
        /// Configures the relay to use this player for audio streaming.
        /// </summary>
        /// <returns>Coroutine that completes when initialization succeeds or max attempts reached</returns>
        private System.Collections.IEnumerator InitializeWithRetry()
        {
            // Try to initialize up to 5 times with increasing delays
            for (var attempt = 0; attempt < 5; attempt++)
            {
                if (attempt > 0)
                {
                    // Wait progressively longer between attempts
                    yield return new WaitForSeconds(0.1f * attempt);
                }

                // Check if audio filter relay is assigned
                if (!audioFilterRelay)
                {
                    // Only log warnings if not in test mode (where we set it programmatically)
                    if (!_suppressInitializationWarnings)
                    {
                        if (attempt == 1)
                        {
                            Debug.LogWarning($"[{_cachedGameObjectName}] AudioFilterRelay not yet assigned, waiting for assignment...", this);
                        }
                        else if (attempt == 4)
                        {
                            // Only error on last attempt
                            Debug.LogError($"[{_cachedGameObjectName}] AudioFilterRelay is not assigned! Please assign it in the Inspector or use SetAudioFilterRelay().", this);
                        }
                    }
                    continue; // Continue retry loop in case it gets assigned dynamically
                }

                if (audioFilterRelay)
                {
                    // Success! Connect this streaming player to the relay
                    audioFilterRelay?.SetStreamingPlayer(this, enableVerboseLogging);

                    UpdateBufferSizes();

                    if(enableVerboseLogging)
                        Debug.Log($"[{_cachedGameObjectName}] StreamingAudioPlayer initialized with relay on attempt {attempt + 1}");
                    yield break; // Exit coroutine
                }
                if (enableVerboseLogging && attempt < 4)
                {
                    Debug.LogWarning($"[{_cachedGameObjectName}] AudioFilterRelay not found on attempt {attempt + 1}, will retry...");
                }
            }

            // Failed after all attempts - this is a configuration error
            var errorMsg = $"[{_cachedGameObjectName}] CONFIGURATION ERROR: AudioFilterRelay is not assigned! " +
                          "This MUST be assigned in the Inspector. " +
                          "StreamingAudioPlayer cannot function without AudioFilterRelay.";
            Debug.LogError(errorMsg, this);
            enabled = false;
            throw new MissingComponentException(errorMsg);
        }

        /// <summary>
        /// Update buffer size calculations from centralized manager
        /// </summary>
        private void UpdateBufferSizes()
        {
            // Get buffer from centralized AdaptiveBufferManager (if it exists)
            float bufferDuration = 0.1f; // Default fallback
            if (AdaptiveBufferManager.HasInstance)
            {
                bufferDuration = AdaptiveBufferManager.Instance?.CurrentBufferDuration ?? 0.1f;
            }
            _minBufferSamples = Mathf.RoundToInt(bufferDuration * _sampleRate);
            // No max buffer limit anymore - buffer grows dynamically
        }

        /// <summary>
        /// Legacy method - network quality is now handled by AdaptiveBufferManager.
        /// This method is kept for backward compatibility but does nothing.
        /// </summary>
        /// <param name="recommendedBufferDuration">Not used - handled by AdaptiveBufferManager</param>
        [Obsolete("Use AdaptiveBufferManager.Instance.ProcessBufferHint() instead")]
        public void UpdateNetworkQuality(float recommendedBufferDuration)
        {
            // Network quality updates are now handled by the centralized AdaptiveBufferManager
            // This method is kept for backward compatibility only
            if (enableVerboseLogging)
            {
                Debug.Log($"[{_cachedGameObjectName}] UpdateNetworkQuality called but ignored - using AdaptiveBufferManager instead");
            }
        }

        /// <summary>
        /// Handle buffer update from AdaptiveBufferManager event
        /// </summary>
        /// <param name="newBufferDuration">New recommended buffer duration in seconds</param>
        private void OnBufferUpdateReceived(float newBufferDuration)
        {
            // Buffer updates are now handled centrally
            // Just update our cached buffer sizes
            lock (_stateLock)
            {
                UpdateBufferSizes();

                if (enableVerboseLogging)
                {
                    Debug.Log($"[{_cachedGameObjectName}] Buffer update notification received: {newBufferDuration:F3}s from AdaptiveBufferManager");
                }
            }
        }

        /// <summary>
        /// Handle audio processing from AudioFilterRelay for NPC speech detection.
        /// Processes audio through VAD to detect actual speech vs pauses.
        /// CRITICAL: This enables InterruptionManager to distinguish:
        /// - Real interruption (user talks over NPC speech)
        /// - Back-channeling (user says "ja" then stops)
        /// - Pause filling (user talks during NPC pause)
        /// </summary>
        private void HandleAudioProcessed(float[] audioData, int channels)
        {
            if (_npcVadProcessor == null || audioData == null || audioData.Length == 0)
                return;

            // Convert multichannel to mono for VAD processing
            float[] monoAudio;
            if (channels == 1)
            {
                monoAudio = audioData;
            }
            else
            {
                // Average channels to mono
                int sampleCount = audioData.Length / channels;
                monoAudio = new float[sampleCount];
                for (int i = 0; i < sampleCount; i++)
                {
                    float sum = 0f;
                    for (int ch = 0; ch < channels; ch++)
                    {
                        sum += audioData[i * channels + ch];
                    }
                    monoAudio[i] = sum / channels;
                }
            }

            // Process through VAD - deltaTime estimated at ~20ms (50Hz audio callbacks)
            bool isSpeaking = _npcVadProcessor.ProcessAudioFrame(monoAudio, deltaTime: 0.02f);

            // Update IsNPCSpeaking state for InterruptionManager
            SetNPCSpeechState(isSpeaking);
        }

        /// <summary>
        /// Start streaming audio at the specified sample rate
        /// </summary>
        public void StartStream(int sampleRate)
        {
            // Guard: block new streams if pipeline is shut down
            if (!TryTransitionTo(AudioPipelineState.Streaming))
                return;

            lock (_stateLock)
            {
                // IMPORTANT: Always stop and clear any previous stream first
                // This ensures no audio bleeding between sessions, especially after interruptions
                if (_isStreamActive)
                {
                    // Only warn if there's actually audio left in the buffer
                    if (_audioBuffer.Count > 0)
                    {
                        Debug.LogWarning($"[{_cachedGameObjectName}] StartStream clearing {_audioBuffer.Count} samples from previous stream (likely interruption)");
                    }
                    // Force stop the previous stream to ensure clean state
                    StopPlaybackInternal(wasInterrupted: true);
                }

                // Reset force stop and pause flags when starting new stream
                // CRITICAL: Clear any stale pause state to prevent stuck audio
                // (e.g., VR headset power off/on cycle leaving a pause reason set without a matching resume)
                _forceStop = false;
                _isPaused = false;
                _activePauseReasons.Clear();

                _sampleRate = sampleRate;
                _isStreamActive = true;
                _isPlaybackStarted = false; // CRITICAL: Reset for each stream to trigger OnPlaybackStarted event
                _hasFirstAudioPlayed = false; // CRITICAL: Reset for each stream to trigger OnFirstAudioPlayed event
                _hasLoggedFirstAudio = false; // Reset log guard for new stream
                _streamComplete = false;
                _isReceivingResponse = true; // NEW: Mark that we're receiving a response
                _isPrimingBuffer = true; // PRIMING BUFFER: Enable larger initial buffer for first chunks
                _totalSamplesReceived = 0;
                _totalSamplesPlayed = 0;
                _underrunCount = 0;
                _lastDataReceivedTime = Time.realtimeSinceStartup; // Initialize timestamp for auto-detection
                _serverStreamEnd = false; // Clear stale signal — turn N's flag must not finalize turn N+1

                // Extra safety: Ensure buffer is completely empty
                // (Should already be cleared by StopPlaybackInternal but this is a safety check)
                while (_audioBuffer.TryDequeue(out _)) { }

                // Get buffer recommendation from centralized AdaptiveBufferManager (if it exists)
                float currentBuffer = 0.1f; // Default fallback
                AdaptiveBufferManager bufferManager = null;

                if (AdaptiveBufferManager.HasInstance)
                {
                    bufferManager = AdaptiveBufferManager.Instance;
                    currentBuffer = bufferManager?.CurrentBufferDuration ?? 0.1f;
                }

                // Log the buffer being used for this stream (verbose only to reduce spam)
                if (enableVerboseLogging)
                {
                    if (bufferManager != null)
                    {
                        Debug.Log($"[{_cachedGameObjectName}] Starting stream with centralized buffer: {currentBuffer:F3}s from AdaptiveBufferManager");
                    }
                    else
                    {
                        Debug.LogWarning($"[{_cachedGameObjectName}] AdaptiveBufferManager not found, using default buffer: {currentBuffer:F3}s");
                    }
                }

                // Update buffer sizes for new sample rate
                UpdateBufferSizes();

                if(enableVerboseLogging)
                    Debug.Log($"[{_cachedGameObjectName}] Stream started at {sampleRate}Hz, buffer: {currentBuffer:F3}s (dynamic max)");
            }
        }

        /// <summary>
        /// Add audio samples to the streaming buffer
        /// </summary>
        public void AddAudioData(float[] samples)
        {
            if (samples == null || samples.Length == 0)
                return;

            lock (_stateLock)
            {
                // PAUSE BEHAVIOR: Continue buffering audio during pause
                // FillAudioBuffer() handles pause by outputting silence without dequeuing
                // This allows seamless resume - buffer continues to grow during pause
                // WARNING: Long pauses (>1 minute) may cause large buffers (acceptable for training scenarios)
                if (_isPaused && enableVerboseLogging)
                {
                    Debug.Log($"[{_cachedGameObjectName}] Buffering {samples.Length} samples during pause (buffer will continue to grow)");
                }

                // SIMPLICITY: Process audio data regardless of _isStreamActive state
                // State flag is for UI/IsTalking indication, not for blocking audio processing
                // Audio pipeline: data arrives → buffer → auto-start playback when buffer full
                if (!_isStreamActive && enableVerboseLogging)
                {
                    Debug.Log($"[{_cachedGameObjectName}] Received audio data before StartStream() - processing anyway (state flag is informational only)");
                }

                // Track when we last received data - used for auto-detecting playback completion
                _lastDataReceivedTime = Time.realtimeSinceStartup;

                // Add samples to buffer - no limit, memory will grow as needed
                // Use AudioSource.volume for volume control, not gain multipliers!
                foreach (var sample in samples)
                {
                    _audioBuffer.Enqueue(sample);
                }

                // Optional: Log warning if buffer is getting very large
                var currentBufferSeconds = _audioBuffer.Count / (float)_sampleRate;
                if (currentBufferSeconds > 60.0f && _totalSamplesReceived % (_sampleRate * 10) < samples.Length)
                {
                    Debug.LogWarning($"[{_cachedGameObjectName}] Large buffer detected: {currentBufferSeconds:F1}s of audio buffered. This may indicate slow playback or network issues.");
                }

                _totalSamplesReceived += samples.Length;

                // PRIMING BUFFER: Use larger buffer for first stream to prevent "catching up"
                // Calculate buffer threshold: priming buffer (250ms) for first chunks, then normal adaptive buffer (100ms)
                int bufferThreshold;
                if (_isPrimingBuffer)
                {
                    // Use priming buffer for first chunks - accounts for WebSocket/decoder overhead
                    bufferThreshold = Mathf.RoundToInt(PRIMING_BUFFER_DURATION * _sampleRate);
                }
                else
                {
                    // Use normal adaptive buffer from AdaptiveBufferManager
                    bufferThreshold = _minBufferSamples;
                }

                // Start playback if we have enough buffered
                if (!_isPlaybackStarted && _audioBuffer.Count >= bufferThreshold)
                {
                    StartPlayback();
                    _isPrimingBuffer = false; // Disable priming buffer after first playback starts

                    if (enableVerboseLogging)
                        Debug.Log($"[{_cachedGameObjectName}] ✅ Playback started with buffer: {bufferThreshold / (float)_sampleRate:F3}s ({bufferThreshold} samples), Priming: {_isPrimingBuffer}");
                }
                else if (enableVerboseLogging && _totalSamplesReceived % (_sampleRate) < samples.Length)
                {
                    Debug.Log($"[{_cachedGameObjectName}] ⏳ Waiting for playback start - Buffer: {_audioBuffer.Count}/{bufferThreshold} samples, AlreadyStarted: {_isPlaybackStarted}");
                }

                // Log buffer status periodically
                if (enableVerboseLogging && _totalSamplesReceived % (_sampleRate * 2) < samples.Length) // Every 2 seconds
                {
                    Debug.Log($"[{_cachedGameObjectName}] Buffer: {BufferLevel:F2}s, Received: {_totalSamplesReceived}, Played: {_totalSamplesPlayed}");
                }
            }
        }

        /// <summary>
        /// Signal that the stream is complete (no more data will be added)
        /// </summary>
        public void EndStream()
        {
            lock (_stateLock)
            {
                _streamComplete = true;
                _isReceivingResponse = false; // NEW: Mark that response is complete
                if (enableVerboseLogging)
                    Debug.Log($"[{_cachedGameObjectName}] Stream ended - Total received: {_totalSamplesReceived} samples ({_totalSamplesReceived / (float)_sampleRate:F2}s)");
            }
        }

        /// <summary>
        /// Stop playback and clear buffers
        /// </summary>
        /// <param name="wasInterrupted">True if stopped by interruption, false if natural end</param>
        public void StopPlayback(bool wasInterrupted = true)
        {
            if (enableVerboseLogging && wasInterrupted)
            {
                Debug.LogWarning($"[{_cachedGameObjectName}] StopPlayback called for INTERRUPTION! Stack trace:\n{Environment.StackTrace}");
            }
            else if (enableVerboseLogging)
            {
                Debug.Log($"[{_cachedGameObjectName}] StopPlayback called for natural end");
            }

            // CRITICAL: Set force stop flag immediately to stop audio callbacks
            _forceStop = true;

            lock (_stateLock)
            {
                StopPlaybackInternal(wasInterrupted: wasInterrupted);
            }
        }

        /// <summary>
        /// Internal stop method that assumes we already have the lock
        /// </summary>
        private void StopPlaybackInternal(bool wasInterrupted = false)
        {
            // Transition to Idle unless we're in ShuttingDown (which must stay ShuttingDown)
            if (_pipelineState != AudioPipelineState.ShuttingDown)
            {
                _pipelineState = AudioPipelineState.Idle;
            }

            _isStreamActive = false;
            _isPlaybackStarted = false; // CRITICAL: Reset to allow StartPlayback() for next turn
            _streamComplete = true;
            _isReceivingResponse = false; // NEW: Mark that response is stopped
            _forceStop = true; // Ensure force stop is set
            _samplesPlayedSinceStart = 0; // Reset grace period counter
            _cachedIsPlaying = false; // Update cached state

            // CRITICAL: Reset NPC speech state for InterruptionManager
            // Without this, old VAD state causes false overlap detection
            IsNPCSpeaking = false;
            if (enableVerboseLogging)
                Debug.Log($"[{_cachedGameObjectName}] 🤐 NPC speech state reset (playback stopped)");

            // CRITICAL: Set cleanup flag to prevent VoiceLinePlayer from loading during DestroyImmediate
            // This prevents Unity AssetDatabase corruption and .meta file corruption
            bool cleanupFlagWasSet = false;
            try
            {
                // Access via reflection since we can't add direct reference to Training assembly
                var audioLockType = System.Type.GetType("Tsc.Training.Audio.AudioLoadLockManager, Training");
                if (audioLockType != null)
                {
                    var flagProperty = audioLockType.GetProperty("IsStreamingAudioCleanupInProgress");
                    if (flagProperty != null)
                    {
                        flagProperty.SetValue(null, true);
                        cleanupFlagWasSet = true;
                        if (enableVerboseLogging)
                            Debug.Log($"[{_cachedGameObjectName}] [{Time.time:F3}] ✓ Set IsStreamingAudioCleanupInProgress = true");
                    }
                    else if (enableVerboseLogging)
                    {
                        Debug.LogWarning($"[{_cachedGameObjectName}] [{Time.time:F3}] ❌ AudioLoadLockManager found but IsStreamingAudioCleanupInProgress property not found");
                    }
                }
                else if (enableVerboseLogging)
                {
                    Debug.LogWarning($"[{_cachedGameObjectName}] [{Time.time:F3}] ❌ AudioLoadLockManager type not found via reflection");
                }

                // Stop relay if it exists (may be null in tests)
                // This now also recreates AudioClip and resets AudioSource.time for complete buffer clear
                audioFilterRelay?.StopPlayback();

                if (enableVerboseLogging)
                    Debug.Log($"[{_cachedGameObjectName}] [{Time.time:F3}] Audio relay stopped (cleanup flag was set: {cleanupFlagWasSet})");
            }
            finally
            {
                // Clear cleanup flag (only if we set it successfully)
                if (cleanupFlagWasSet)
                {
                    var audioLockType = System.Type.GetType("Tsc.Training.Audio.AudioLoadLockManager, Training");
                    if (audioLockType != null)
                    {
                        var flagProperty = audioLockType.GetProperty("IsStreamingAudioCleanupInProgress");
                        if (flagProperty != null)
                        {
                            flagProperty.SetValue(null, false);
                            if (enableVerboseLogging)
                                Debug.Log($"[{_cachedGameObjectName}] [{Time.time:F3}] ✓ Set IsStreamingAudioCleanupInProgress = false");
                        }
                    }
                }
            }

            // Clear buffer - CRITICAL for preventing audio bleeding between sessions
            // Count samples being discarded for logging
            int discardedSamples = 0;
            while (_audioBuffer.TryDequeue(out _))
            {
                discardedSamples++;
            }

            // Extra safety: Double-check buffer is truly empty
            // ConcurrentQueue can have race conditions, so verify
            if (_audioBuffer.Count > 0)
            {
                Debug.LogWarning($"[{_cachedGameObjectName}] ⚠️ Buffer not empty after clear! Retrying... ({_audioBuffer.Count} samples remaining)");
                while (_audioBuffer.TryDequeue(out _)) { }
            }

            if(enableVerboseLogging)
            {
                if (discardedSamples > 0)
                {
                    Debug.Log($"[{_cachedGameObjectName}] Playback stopped - Played: {_totalSamplesPlayed}/{_totalSamplesReceived} samples, Discarded: {discardedSamples}, Underruns: {_underrunCount}");
                }
                else
                {
                    Debug.Log($"[{_cachedGameObjectName}] Playback stopped - Played: {_totalSamplesPlayed}/{_totalSamplesReceived} samples, Underruns: {_underrunCount}");
                }
            }

            // Fire appropriate event based on stop reason
            if (wasInterrupted)
            {
                OnPlaybackInterrupted?.Invoke();
            }
            else
            {
                OnPlaybackComplete?.Invoke();
            }
        }

        /// <summary>
        /// Get playback progress as a percentage (0-1)
        /// </summary>
        public float GetPlaybackProgress()
        {
            lock (_stateLock)
            {
                if (_totalSamplesReceived <= 0)
                    return 0f;

                // Calculate progress based on samples played vs received
                var progress = (float)_totalSamplesPlayed / _totalSamplesReceived;

                // Clamp between 0 and 1
                return Mathf.Clamp01(progress);
            }
        }

        /// <summary>
        /// Pause audio playback (keeps buffer intact). Multiple pause sources can stack —
        /// audio only actually resumes when every source has released its pause.
        /// </summary>
        /// <param name="reason">Which subsystem is requesting the pause. Defaults to External
        /// (training-level pause) so existing callers without a parameter keep their semantics.</param>
        public virtual void PausePlayback(PauseReason reason = PauseReason.External)
        {
            // Don't pause if already shut down
            if (_pipelineState == AudioPipelineState.ShuttingDown || _pipelineState == AudioPipelineState.Destroyed)
                return;

            lock (_stateLock)
            {
                bool alreadyPaused = _activePauseReasons.Count > 0;

                if (!_activePauseReasons.Add(reason) && enableVerboseLogging)
                {
                    Debug.Log($"[{_cachedGameObjectName}] PausePlayback({reason}) ignored — already paused by same reason");
                }

                if (alreadyPaused)
                {
                    // Different source asking to pause an already-paused player: record the
                    // reason (done above) but the AudioSource is already paused, so stop here.
                    return;
                }

                _stateBeforePause = _pipelineState;
                _pipelineState = AudioPipelineState.Paused;
                _isPaused = true;

                // Pause the AudioSource via relay
                if (audioFilterRelay != null && audioFilterRelay.AudioSource != null && audioFilterRelay.AudioSource.isPlaying)
                {
                    audioFilterRelay.AudioSource.Pause();
                    if(enableVerboseLogging)
                    {
                        Debug.Log($"[{_cachedGameObjectName}] Audio playback paused ({reason}) - Stack trace:\n{System.Environment.StackTrace}");
                    }
                }
                else if (enableVerboseLogging)
                {
                    Debug.Log($"[{_cachedGameObjectName}] PausePlayback({reason}) called but AudioSource not playing (relay:{audioFilterRelay != null}, source:{audioFilterRelay?.AudioSource != null}, playing:{audioFilterRelay?.AudioSource?.isPlaying})");
                }
            }
        }

        /// <summary>
        /// Resume audio playback for the specified pause source. If other pause sources are
        /// still active, the AudioSource stays paused but this source is removed from the
        /// active set.
        /// </summary>
        /// <param name="reason">Which subsystem is requesting the resume. Must match the reason
        /// that paused this source (External is the default for legacy callers).</param>
        public virtual void ResumePlayback(PauseReason reason = PauseReason.External)
        {
            // Don't resume if shut down
            if (_pipelineState == AudioPipelineState.ShuttingDown || _pipelineState == AudioPipelineState.Destroyed)
                return;

            lock (_stateLock)
            {
                if (!_activePauseReasons.Remove(reason))
                {
                    if (enableVerboseLogging)
                        Debug.Log($"[{_cachedGameObjectName}] ResumePlayback({reason}) ignored — not paused by this reason");
                    return;
                }

                if (_activePauseReasons.Count > 0)
                {
                    // Other sources still have us paused — clear this one but stay paused.
                    if (enableVerboseLogging)
                        Debug.Log($"[{_cachedGameObjectName}] ResumePlayback({reason}) removed — still paused by {_activePauseReasons.Count} other source(s)");
                    return;
                }

                _pipelineState = _stateBeforePause;
                _isPaused = false;

                // Resume the AudioSource via relay
                if (_isPlaybackStarted)
                {
                    audioFilterRelay?.AudioSource?.UnPause();
                    if(enableVerboseLogging)
                    {
                        Debug.Log($"[{_cachedGameObjectName}] Audio playback resumed ({reason}) - Stack trace:\n{System.Environment.StackTrace}");
                    }
                }
            }
        }

        /// <summary>
        /// True if the player is currently paused by the given reason. Useful for tests and
        /// for derived classes that need to branch on pause ownership.
        /// </summary>
        public bool IsPausedForReason(PauseReason reason)
        {
            lock (_stateLock)
            {
                return _activePauseReasons.Contains(reason);
            }
        }

        /// <summary>
        /// Start audio playback
        /// </summary>
        private void StartPlayback()
        {
            _isPlaybackStarted = true;
            _cachedIsPlaying = true; // Update cached state
            _samplesPlayedSinceStart = 0; // Reset grace period counter

            // Start playback through relay if available
            if (audioFilterRelay != null)
            {
                audioFilterRelay.StartPlayback();
            }
            else if (enableVerboseLogging)
            {
                Debug.LogWarning($"[{_cachedGameObjectName}] AudioFilterRelay not available - playback may not work correctly");
            }

            if(enableVerboseLogging)
                Debug.Log($"[{_cachedGameObjectName}] Playback started with {BufferLevel:F2}s buffered");

            OnPlaybackStarted?.Invoke();
            OnPlaybackStartedStatic?.Invoke();
        }

        /// <summary>
        /// Fill the audio buffer - called by AudioFilterRelay's OnAudioFilterRead
        /// </summary>
        public void FillAudioBuffer(float[] data, int channels)
        {
            // CRITICAL: Check force stop and shutdown state FIRST (audio thread safety)
            if (_forceStop || _pipelineState == AudioPipelineState.ShuttingDown || _pipelineState == AudioPipelineState.Destroyed)
            {
                Array.Clear(data, 0, data.Length);
                _lastPlayedAudioFrame = null; // Clear when stopped
                return;
            }

            // Log first callback to confirm it's working
            //if (enableVerboseLogging && _totalSamplesPlayed == 0 && data.Length > 0)
            //{
            //    Debug.Log($"[{_cachedGameObjectName}] OnAudioFilterRead first callback - data.Length: {data.Length}, channels: {channels}");
            //}

            // Check if we're paused - output silence but DON'T consume buffer
            if (_isPaused)
            {
                Array.Clear(data, 0, data.Length);
                _lastPlayedAudioFrame = null; // No audio playing when paused
                // Don't dequeue samples - keep them for when we resume
                return;
            }

            if (!_isStreamActive || !_isPlaybackStarted)
            {
                // Output silence when not active
                Array.Clear(data, 0, data.Length);
                _lastPlayedAudioFrame = null; // No audio playing when inactive
                return;
            }

            var samplesNeeded = data.Length / channels;
            var samplesProvided = 0;

            // Log buffer state periodically
            if (enableVerboseLogging && _totalSamplesPlayed % (_sampleRate * 2) < samplesNeeded)
            {
                Debug.Log($"[{_cachedGameObjectName}] OnAudioFilterRead - Buffer has {_audioBuffer.Count} samples, need {samplesNeeded}");
            }



            // Fill the buffer with available samples
            for (var i = 0; i < samplesNeeded; i++)
            {
                if (_audioBuffer.TryDequeue(out var sample))
                {
                    if (channels == 1)
                    {
                        // Mono output
                        data[i] = sample;
                    }
                    else
                    {
                        // Stereo or multi-channel - copy mono to all channels
                        for (var ch = 0; ch < channels; ch++)
                        {
                            data[i * channels + ch] = sample;
                        }
                    }
                    samplesProvided++;
                }
                else
                {
                    // Buffer underrun - no more samples available
                    break;
                }
            }

            // Fill remaining with silence if needed
            if (samplesProvided < samplesNeeded)
            {
                for (var i = samplesProvided; i < samplesNeeded; i++)
                {
                    for (var ch = 0; ch < channels; ch++)
                    {
                        data[i * channels + ch] = 0f;
                    }
                }

                // Only count as underrun if we're still expecting more data
                // AND we're past the grace period (allows buffer to stabilize after StartPlayback)
                if (!_streamComplete && _samplesPlayedSinceStart > UNDERRUN_GRACE_PERIOD_SAMPLES)
                {
                    _underrunCount++;
                    if (enableVerboseLogging && _underrunCount % 50 == 1) // Log every 50th underrun when verbose
                    {
                        Debug.LogWarning($"[{_cachedGameObjectName}] Buffer underrun #{_underrunCount} - Only {samplesProvided}/{samplesNeeded} samples available");
                    }
                }
            }

            _totalSamplesPlayed += samplesProvided;
            _samplesPlayedSinceStart += samplesProvided; // Track samples played since StartPlayback()

            // Mark when first audio sample is actually output
            // This is the TRUE perceived latency moment - when user actually hears audio
            // Set flag here (audio thread) but fire event in Update() (main thread)
            // NOTE: _hasFirstAudioPlayed is reset by Update() after firing the event,
            // so we use _hasLoggedFirstAudio (never reset mid-stream) to prevent repeated logging
            if (!_hasFirstAudioPlayed && samplesProvided > 0)
            {
                _hasFirstAudioPlayed = true;
                if (enableVerboseLogging && !_hasLoggedFirstAudio)
                {
                    _hasLoggedFirstAudio = true;
                    UnityEngine.Debug.Log($"[{_cachedGameObjectName}] 🔊 First audio sample output at frame");
                }
            }

            // Store the last played audio frame for feedback detection
            // Only store if we actually played audio
            if (samplesProvided > 0)
            {
                // Convert multichannel to mono for feedback detection
                if (_lastPlayedAudioFrame == null || _lastPlayedAudioFrame.Length != samplesNeeded)
                {
                    _lastPlayedAudioFrame = new float[samplesNeeded];
                }

                for (var i = 0; i < samplesNeeded; i++)
                {
                    if (channels == 1)
                    {
                        _lastPlayedAudioFrame[i] = data[i];
                    }
                    else
                    {
                        // Average all channels to mono
                        var sum = 0f;
                        for (var ch = 0; ch < channels; ch++)
                        {
                            sum += data[i * channels + ch];
                        }
                        _lastPlayedAudioFrame[i] = sum / channels;
                    }
                }
            }
            else
            {
                // No audio played - clear the frame
                _lastPlayedAudioFrame = null;
            }

            // Check if playback is complete
            if (_streamComplete && _audioBuffer.Count == 0 && samplesProvided == 0)
            {
                // Mark for stopping - will be handled in Update()
                _shouldStop = true;
            }
        }

        private void Update()
        {
            // OPTIMIZATION: Only process when streaming is active
            if (!_isStreamActive)
                return;

            // Fire OnFirstAudioPlayed event on main thread when first audio sample is output
            // Flag is set in FillAudioBuffer() (audio thread), event fired here (main thread)
            // IMPORTANT: Check if flag is true AND we haven't fired yet this stream
            if (_hasFirstAudioPlayed && _isPlaybackStarted)
            {
                // Double-checked locking pattern to ensure we only fire once per stream
                lock (_stateLock)
                {
                    // Check again inside lock - prevents race conditions
                    if (_hasFirstAudioPlayed)
                    {
                        // Set to false BEFORE firing to prevent duplicate events in subsequent frames
                        _hasFirstAudioPlayed = false;

                        // Now fire the event - only happens once per stream
                        OnFirstAudioPlayed?.Invoke();
                    }
                }
            }

            // OPTIMIZATION: Cache AudioSource.isPlaying check (expensive Unity API call)
            // Check once per second instead of every frame at 60fps
            if (Time.frameCount % 60 == 0)
            {
                if (audioFilterRelay != null && audioFilterRelay.AudioSource != null)
                {
                    _cachedIsPlaying = audioFilterRelay.AudioSource.isPlaying;
                }
                else
                {
                    _cachedIsPlaying = false;
                }
            }

            // CRITICAL: Editor pause detection
            // Unity's OnAudioFilterRead continues running even when Editor is paused!
            // Without this check, audio buffer would continue to fill during Editor pause.
            // The Editor pause is tracked as its own PauseReason so it stacks correctly with
            // any External/OS pauses that may already be active.
#if UNITY_EDITOR
            {
                bool editorIsPaused = UnityEditor.EditorApplication.isPaused;
                bool weThinkEditorIsPaused = IsPausedForReason(PauseReason.EditorPause);

                if (editorIsPaused && !weThinkEditorIsPaused)
                {
                    PausePlayback(PauseReason.EditorPause);
                }
                else if (!editorIsPaused && weThinkEditorIsPaused)
                {
                    ResumePlayback(PauseReason.EditorPause);
                }
            }
#endif

            // Finalize playback when the orchestrator's AudioStreamEnd signal has arrived AND
            // the buffer has drained. The safety-net timeout (3s default) only kicks in if the
            // server signal never arrives — covers the rare server-crash scenario without
            // firing during normal multi-sentence streaming under network jitter (the original
            // OpusHead-parse bug was caused by a 0.15s timeout firing mid-response).
            if (!_shouldStop)
            {
                float timeSinceLastData = Time.realtimeSinceStartup - _lastDataReceivedTime;
                if (EvaluateAutoComplete(_isPlaybackStarted, _audioBuffer.Count == 0, timeSinceLastData))
                {
                    if (enableVerboseLogging)
                    {
                        var trigger = _serverStreamEnd
                            ? "server signal + empty buffer"
                            : $"safety-net timeout ({timeSinceLastData:F2}s with no data, no server signal)";
                        Debug.Log($"[{_cachedGameObjectName}] Auto-detected playback complete — {trigger}");
                    }
                    _shouldStop = true;
                }
            }

            // Handle stop request on main thread
            if (_shouldStop)
            {
                _shouldStop = false;
                // Natural end of audio stream (not interruption)
                StopPlayback(wasInterrupted: false);
                return;
            }

            // Monitor buffer level (only when verbose logging is enabled)
            if (enableVerboseLogging && _isPlaybackStarted)
            {
                var currentBufferLevel = BufferLevel;

                // Log significant buffer changes (every 0.5s change)
                if (Mathf.Abs(currentBufferLevel - _lastBufferLevel) > 0.5f)
                {
                    Debug.Log($"[{_cachedGameObjectName}] Buffer level: {currentBufferLevel:F2}s");
                    _lastBufferLevel = currentBufferLevel;
                }
            }
        }

        protected virtual void OnDestroy()
        {
            _pipelineState = AudioPipelineState.Destroyed;
            StopPlayback();

            // Unsubscribe from buffer updates (only if instance exists, don't create during cleanup)
            if (AdaptiveBufferManager.HasInstance)
            {
                var bufferManager = AdaptiveBufferManager.Instance;
                if (bufferManager != null)
                {
                    bufferManager.OnBufferUpdateEvent -= OnBufferUpdateReceived;
                }
            }

            // Unsubscribe from audio processing events
            if (audioFilterRelay != null)
            {
                audioFilterRelay.OnAudioProcessed -= HandleAudioProcessed;
            }

            #if UNITY_EDITOR
            // Unsubscribe from editor pause events
            UnityEditor.EditorApplication.pauseStateChanged -= OnEditorPauseStateChanged;
            #endif
        }

        private void OnApplicationPause(bool pauseStatus)
        {
            // Application pause fires for Quest Home long-press, VR headset power off, Android
            // backgrounding. Tracked as OsApplicationPause so it doesn't conflict with a
            // training-level External pause that may also be active.
            if (pauseStatus)
            {
                PausePlayback(PauseReason.OsApplicationPause);
            }
            else
            {
                ResumePlayback(PauseReason.OsApplicationPause);
            }
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            // Focus loss fires for Quest Home short-press (Navigator overlay), Alt-Tab in a
            // windowed build. The pause is tracked as OsFocusLoss. It stacks safely with an
            // External (PauseManager) pause — resume here removes only this source, so the
            // audio stays paused until PauseManager also resumes.
            // DISABLED in Unity Editor so developers can click other editor windows without
            // cutting off their debugging audio.
            #if !UNITY_EDITOR
            if (!hasFocus && _isStreamActive)
            {
                PausePlayback(PauseReason.OsFocusLoss);
            }
            else if (hasFocus)
            {
                ResumePlayback(PauseReason.OsFocusLoss);
            }
            #endif
        }

        // REMOVED: ResetBufferAfterFirstResponse coroutine - no longer needed
        // The pitch shift was caused by AudioStreamStart arriving before audio data,
        // not by buffer size. Fixed in backend by sending them together.
    }
}