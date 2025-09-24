using System;
using System.Collections.Concurrent;
using UnityEngine;
using UnityEngine.Events;

namespace SimulationCrew.AIBridge.Audio.Playback
{
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

        [Header("Audio Configuration")]
        [SerializeField]
        [Tooltip("REQUIRED: Audio filter relay component. Must be assigned in the Inspector! Should be on the same GameObject as the AudioSource.")]
        private AudioFilterRelay audioFilterRelay;

        [SerializeField]
        [Tooltip("Audio gain/volume multiplier")]
        [Range(0.01f, 2f)]
        private float audioGain = 1.0f;

        [Header("Buffering")]
        [SerializeField]
        [Tooltip("Minimum buffer size in seconds before starting playback")]
        [Range(0.05f, 2f)]
        private float minBufferDuration = 0.1f; // Reduced from 0.3f for lower latency

        [SerializeField]
        [Tooltip("Enable adaptive buffering via centralized AdaptiveBufferManager")]
        private bool enableAdaptiveBuffering = true;

        [Header("Debug Settings")]
        [SerializeField]
        [Tooltip("Enable verbose logging for debugging audio streaming and buffering")]
        private bool enableVerboseLogging;

        // Events
        [Header("Events")]
        public UnityEvent OnPlaybackStarted = new();
        public UnityEvent OnPlaybackComplete = new();  // Natural end of audio
        public UnityEvent OnPlaybackInterrupted = new(); // Audio stopped by interruption

        // Ring buffer for audio samples - no size limit, grows as needed
        private readonly ConcurrentQueue<float> _audioBuffer = new();

        // State
        // IMPORTANT: This receives DOWNSTREAM audio from TTS at 48kHz
        // (Different from UPSTREAM microphone capture at 16kHz)
        private int _sampleRate = 48000; // Default to TTS output frequency
        private bool _isStreamActive;
        private bool _isPlaybackStarted;
        private bool _streamComplete;
        private bool _shouldStop;
        private bool _isPaused;
        private bool _isReceivingResponse; // NEW: Track if we're still receiving response from backend
        private bool _forceStop; // CRITICAL: Force audio to stop immediately for interruptions
        private int _totalSamplesReceived;
        private int _totalSamplesPlayed;
        private readonly object _stateLock = new();

        // Cached values
        private string _cachedGameObjectName;
        private int _minBufferSamples;

        // Statistics
        private float _lastBufferLevel;
        private int _underrunCount;
        private bool _cachedIsPlaying; // Thread-safe cache of AudioSource.isPlaying

        public bool IsPlaybackActive => _isStreamActive && _cachedIsPlaying;
        public bool HasBufferedAudio => _audioBuffer.Count > 0;
        public float BufferLevel => _audioBuffer.Count / (float)_sampleRate; // In seconds
        public float MinBufferDuration => minBufferDuration; // Expose buffer threshold
        public bool IsAdaptiveBufferingEnabled => enableAdaptiveBuffering;

        /// <summary>
        /// Gets the AudioFilterRelay component (read-only access)
        /// </summary>
        public AudioFilterRelay AudioFilterRelay => audioFilterRelay;

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

        protected virtual void Awake()
        {
            _cachedGameObjectName = gameObject.name;

            // Start initialization coroutine to ensure proper component discovery
            StartCoroutine(InitializeWithRetry());

            // Subscribe to buffer updates if enabled
            if (enableAdaptiveBuffering)
            {
                var bufferManager = AdaptiveBufferManager.Instance;
                if (bufferManager != null)
                {
                    bufferManager.OnBufferUpdateEvent += OnBufferUpdateReceived;
                    Debug.Log($"[{_cachedGameObjectName}] Subscribed to buffer update events");
                }
            }

            #if UNITY_EDITOR
            // Subscribe to editor pause state changes
            UnityEditor.EditorApplication.pauseStateChanged += OnEditorPauseStateChanged;
            #endif
        }

        #if UNITY_EDITOR
        private void OnEditorPauseStateChanged(UnityEditor.PauseState state)
        {
            if(enableVerboseLogging)
                Debug.Log($"[{_cachedGameObjectName}] Editor pause state changed: {state}");

            if (state == UnityEditor.PauseState.Paused)
            {
                PausePlayback();
            }
            else if (state == UnityEditor.PauseState.Unpaused)
            {
                ResumePlayback();
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
                    if (attempt == 0)
                    {
                        Debug.LogError($"[{_cachedGameObjectName}] AudioFilterRelay is not assigned in the Inspector! Please assign it to enable audio playback.", this);
                    }
                    continue; // Continue retry loop in case it gets assigned dynamically
                }

                if (audioFilterRelay)
                {
                    // Success! Connect this streaming player to the relay
                    audioFilterRelay?.SetStreamingPlayer(this);

                    UpdateBufferSizes();

                    if(enableVerboseLogging)
                        Debug.Log($"[{_cachedGameObjectName}] StreamingAudioPlayer initialized with relay on attempt {attempt + 1} - Gain: {audioGain}");
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
        /// Update buffer size calculations
        /// </summary>
        private void UpdateBufferSizes()
        {
            _minBufferSamples = Mathf.RoundToInt(minBufferDuration * _sampleRate);
            // No max buffer limit anymore - buffer grows dynamically
        }

        /// <summary>
        /// Updates the minimum buffer duration based on measured network quality.
        /// Called proactively during warmup/pre-connection to optimize first turn latency.
        /// </summary>
        /// <param name="recommendedBufferDuration">Recommended buffer duration in seconds based on network RTT</param>
        public void UpdateNetworkQuality(float recommendedBufferDuration)
        {
            if (!enableAdaptiveBuffering)
            {
                return; // Silently ignore if disabled
            }

            lock (_stateLock)
            {
                var previousBuffer = minBufferDuration;

                // Only update if there's a meaningful change (> 5ms difference)
                if (Mathf.Abs(recommendedBufferDuration - minBufferDuration) < 0.005f)
                {
                    return; // No significant change, skip update
                }

                // Apply the recommended buffer directly for first turn optimization
                minBufferDuration = recommendedBufferDuration;

                // Update buffer sizes
                UpdateBufferSizes();

                // Only log significant changes (> 10ms)
                if (enableVerboseLogging && Mathf.Abs(recommendedBufferDuration - previousBuffer) > 0.01f)
                {
                    Debug.Log($"[{_cachedGameObjectName}] Buffer adjusted: {previousBuffer:F3}s → {minBufferDuration:F3}s (network optimization)");
                }
            }
        }

        /// <summary>
        /// Handle buffer update from AdaptiveBufferManager event
        /// </summary>
        /// <param name="newBufferDuration">New recommended buffer duration in seconds</param>
        private void OnBufferUpdateReceived(float newBufferDuration)
        {
            if (!enableAdaptiveBuffering)
                return;

            lock (_stateLock)
            {
                var previousBuffer = minBufferDuration;

                // Only update if there's a meaningful change (> 5ms difference)
                if (Mathf.Abs(newBufferDuration - minBufferDuration) < 0.005f)
                    return;

                minBufferDuration = newBufferDuration;
                UpdateBufferSizes();

                // Only log significant changes (> 10ms)
                if (enableVerboseLogging && Mathf.Abs(newBufferDuration - previousBuffer) > 0.01f)
                {
                    Debug.Log($"[{_cachedGameObjectName}] Buffer updated via event: {previousBuffer:F3}s → {minBufferDuration:F3}s");
                }
            }
        }

        /// <summary>
        /// Start streaming audio at the specified sample rate
        /// </summary>
        public void StartStream(int sampleRate)
        {
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

                // Reset force stop flag when starting new stream
                _forceStop = false;

                _sampleRate = sampleRate;
                _isStreamActive = true;
                _isPlaybackStarted = false;
                _streamComplete = false;
                _isReceivingResponse = true; // NEW: Mark that we're receiving a response
                _totalSamplesReceived = 0;
                _totalSamplesPlayed = 0;
                _underrunCount = 0;

                // Extra safety: Ensure buffer is completely empty
                // (Should already be cleared by StopPlaybackInternal but this is a safety check)
                while (_audioBuffer.TryDequeue(out _)) { }

                // Get buffer recommendation from centralized AdaptiveBufferManager
                if (enableAdaptiveBuffering)
                {
                    var bufferManager = AdaptiveBufferManager.Instance;
                    if (bufferManager && bufferManager.IsAdaptiveBufferingEnabled)
                    {
                        // Use the centralized buffer recommendation
                        var recommendedBuffer = bufferManager.CurrentBufferDuration;
                        if (Mathf.Abs(recommendedBuffer - minBufferDuration) > 0.01f)
                        {
                            minBufferDuration = recommendedBuffer;
                            if (enableVerboseLogging)
                                Debug.Log($"[{_cachedGameObjectName}] Using centralized buffer: {minBufferDuration:F3}s from AdaptiveBufferManager");
                        }
                    }
                }

                // Log the buffer being used for this stream (verbose only to reduce spam)
                if (enableVerboseLogging)
                    Debug.Log($"[{_cachedGameObjectName}] Starting stream with buffer: {minBufferDuration:F3}s");

                // Update buffer sizes for new sample rate
                UpdateBufferSizes();

                if(enableVerboseLogging)
                    Debug.Log($"[{_cachedGameObjectName}] Stream started at {sampleRate}Hz, min buffer: {minBufferDuration}s (dynamic max)");
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
                if (!_isStreamActive)
                {
                    Debug.LogWarning($"[{_cachedGameObjectName}] Received audio data but stream is not active");
                    return;
                }


                // Add samples to buffer - no limit, memory will grow as needed
                foreach (var sample in samples)
                {
                    _audioBuffer.Enqueue(sample * audioGain);
                }

                // Optional: Log warning if buffer is getting very large
                var currentBufferSeconds = _audioBuffer.Count / (float)_sampleRate;
                if (currentBufferSeconds > 60.0f && _totalSamplesReceived % (_sampleRate * 10) < samples.Length)
                {
                    Debug.LogWarning($"[{_cachedGameObjectName}] Large buffer detected: {currentBufferSeconds:F1}s of audio buffered. This may indicate slow playback or network issues.");
                }

                _totalSamplesReceived += samples.Length;

                // Start playback if we have enough buffered
                if (!_isPlaybackStarted && _audioBuffer.Count >= _minBufferSamples)
                {
                    StartPlayback();
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
            _isStreamActive = false;
            _streamComplete = true;
            _isReceivingResponse = false; // NEW: Mark that response is stopped
            _forceStop = true; // Ensure force stop is set
            _cachedIsPlaying = false; // Update cached state

            // Stop relay if it exists (may be null in tests)
            audioFilterRelay?.StopPlayback();


            // Clear buffer - CRITICAL for preventing audio bleeding between sessions
            while (_audioBuffer.TryDequeue(out _)) { }

            if(enableVerboseLogging)
                Debug.Log($"[{_cachedGameObjectName}] Playback stopped - Played: {_totalSamplesPlayed}/{_totalSamplesReceived} samples, Underruns: {_underrunCount}");

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
        /// Pause audio playback (keeps buffer intact)
        /// </summary>
        public void PausePlayback()
        {
            lock (_stateLock)
            {
                if (_isPaused) return;

                _isPaused = true;

                // Pause the AudioSource via relay
                if (audioFilterRelay != null && audioFilterRelay.AudioSource != null && audioFilterRelay.AudioSource.isPlaying)
                {
                    audioFilterRelay.AudioSource.Pause();
                    if(enableVerboseLogging)
                        Debug.Log($"[{_cachedGameObjectName}] Audio playback paused");
                }
            }
        }

        /// <summary>
        /// Resume audio playback
        /// </summary>
        public void ResumePlayback()
        {
            lock (_stateLock)
            {
                if (!_isPaused) return;

                _isPaused = false;

                // Resume the AudioSource via relay
                if (_isPlaybackStarted)
                {
                    audioFilterRelay?.AudioSource?.UnPause();
                    if(enableVerboseLogging)
                        Debug.Log($"[{_cachedGameObjectName}] Audio playback resumed");
                }
            }
        }

        /// <summary>
        /// Start audio playback
        /// </summary>
        private void StartPlayback()
        {
            _isPlaybackStarted = true;
            _cachedIsPlaying = true; // Update cached state

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
            // CRITICAL: Check force stop flag FIRST
            if (_forceStop)
            {
                Array.Clear(data, 0, data.Length);
                _lastPlayedAudioFrame = null; // Clear when stopped
                return;
            }

            // Log first callback to confirm it's working
            if (enableVerboseLogging && _totalSamplesPlayed == 0 && data.Length > 0)
            {
                Debug.Log($"[{_cachedGameObjectName}] OnAudioFilterRead first callback - data.Length: {data.Length}, channels: {channels}");
            }

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
                if (!_streamComplete)
                {
                    _underrunCount++;
                    if (_underrunCount % 50 == 1) // Log every 50th underrun
                    {
                        Debug.LogWarning($"[{_cachedGameObjectName}] Buffer underrun #{_underrunCount} - Only {samplesProvided}/{samplesNeeded} samples available");
                    }
                }
            }

            _totalSamplesPlayed += samplesProvided;

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
            // Update cached isPlaying state on main thread (thread-safe access)
            if (audioFilterRelay != null && audioFilterRelay.AudioSource != null)
            {
                _cachedIsPlaying = audioFilterRelay.AudioSource.isPlaying;
            }
            else
            {
                _cachedIsPlaying = false;
            }

            // Check if Unity is paused
            var shouldBePaused = false;

            #if UNITY_EDITOR
            // In Unity Editor, check multiple pause conditions
            shouldBePaused = UnityEditor.EditorApplication.isPaused ||
                           !UnityEditor.EditorApplication.isPlaying ||
                           Time.timeScale == 0f ||
                           AudioListener.pause;

            // Remove debug logging - no longer needed
            #else
            // In builds, check Time.timeScale and AudioListener pause
            shouldBePaused = Time.timeScale == 0f || AudioListener.pause;
            #endif

            // Also check if AudioSource itself is paused externally
            if (audioFilterRelay)
            {
                var audioSourcePaused = audioFilterRelay != null && audioFilterRelay.AudioSource != null &&
                                       !_cachedIsPlaying && _isStreamActive;
                if (audioSourcePaused && !_isPaused)
                {
                    // AudioSource was paused externally, sync our state
                    lock (_stateLock)
                    {
                        _isPaused = true;
                    }
                }
            }

            lock (_stateLock)
            {
                if (shouldBePaused != _isPaused)
                {
                    // Remove debug log - it's too spammy
                    if (shouldBePaused)
                    {
                        PausePlayback();
                    }
                    else
                    {
                        ResumePlayback();
                    }
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

            // Monitor buffer level
            if (_isStreamActive && _isPlaybackStarted)
            {
                var currentBufferLevel = BufferLevel;

                // Log significant buffer changes
                if (enableVerboseLogging && Mathf.Abs(currentBufferLevel - _lastBufferLevel) > 0.5f)
                {
                    Debug.Log($"[{_cachedGameObjectName}] Buffer level: {currentBufferLevel:F2}s");
                    _lastBufferLevel = currentBufferLevel;
                }
            }
        }

        protected virtual void OnDestroy()
        {
            StopPlayback();

            // Unsubscribe from buffer updates
            var bufferManager = AdaptiveBufferManager.Instance;
            if (bufferManager != null)
            {
                bufferManager.OnBufferUpdateEvent -= OnBufferUpdateReceived;
            }

            #if UNITY_EDITOR
            // Unsubscribe from editor pause events
            UnityEditor.EditorApplication.pauseStateChanged -= OnEditorPauseStateChanged;
            #endif
        }

        private void OnApplicationPause(bool pauseStatus)
        {
            // Handle application pause (mainly for mobile platforms)
            if (pauseStatus)
            {
                PausePlayback();
            }
            else if (_isStreamActive)
            {
                ResumePlayback();
            }
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            // Handle application focus loss (optional - can be disabled if not desired)
            #if UNITY_EDITOR || UNITY_STANDALONE
            // In editor and standalone builds, pause audio when window loses focus
            if (!hasFocus && _isStreamActive)
            {
                PausePlayback();
            }
            else if (hasFocus && _isStreamActive)
            {
                // Only resume if not already paused by other means
                if (!Time.timeScale.Equals(0f))
                {
                    #if UNITY_EDITOR
                    if (Application.isPlaying)
                    {
                        ResumePlayback();
                    }
                    #else
                    ResumePlayback();
                    #endif
                }
            }
            #endif
        }

        // REMOVED: ResetBufferAfterFirstResponse coroutine - no longer needed
        // The pitch shift was caused by AudioStreamStart arriving before audio data,
        // not by buffer size. Fixed in backend by sending them together.
    }
}