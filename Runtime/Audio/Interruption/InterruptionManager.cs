using System;
using UnityEngine;
using Tsc.AIBridge.Core;
using Tsc.AIBridge.Input;
using Tsc.AIBridge.Audio.Playback;

namespace Tsc.AIBridge.Audio.Interruption
{
    /// <summary>
    /// Interruption detection system that works with INpcConfiguration.
    /// Lives on the PLAYER GameObject alongside SpeechInputHandler.
    /// No PersonaSO dependencies - works with any INpcConfiguration implementation.
    /// </summary>
    public class InterruptionManager : MonoBehaviour
    {
        [Header("Required Components")]
        [SerializeField] private SpeechInputHandler speechInputHandler;

        [Header("Near-End Detection")]
        [SerializeField]
        [Range(0.5f, 3.0f)]
        [Tooltip("After the NPC has been speaking for this many seconds, the user can respond with minimal overlap (0.3s). For short responses (<1s), near-end is always triggered to ensure buffered audio is included. This ensures complete STT even when user starts talking during brief NPC responses.")]
        private float nearEndThresholdSeconds = 1.0f;

        [Header("Debug")]
        [SerializeField]
        [Tooltip("Enable detailed console logging for troubleshooting VAD calibration and interruption detection.")]
        private bool enableVerboseLogging = false;

        // Events for backwards compatibility with tests
        // Using object to avoid PersonaSO dependency in Core package
        public event Action<object, string> OnInterruptionDetectedEvent;

        /// <summary>
        /// Public event fired when interruption is detected.
        /// AIBridgeRulesHandler can subscribe to this to send PlayerInterruptedNPC to RuleSystem.
        /// </summary>
        public event Action OnInterruption;

        // Cached active NPC references (set via RequestOrchestrator event)
        private NpcClientBase _activeNpcClient;
        private INpcConfiguration _activeNpcConfig;
        private StreamingAudioPlayer _activeAudioPlayer; // Cached for performance (GetComponent is expensive)

        private float _overlapTimer;
        private bool _hasValidInterruption;
        private float _npcResponseStartTime;
        private float _estimatedResponseDuration;
        private bool _pttPressedDuringNpcSpeech;
        private float _npcStoppedSpeakingTime;

        private void Awake()
        {
            if (speechInputHandler == null)
            {
                Debug.LogError("[InterruptionManager] SpeechInputHandler is required!");
            }
        }

        private void Start()
        {
            // Subscribe to RequestOrchestrator event to track active NPC
            // This replaces reflection-based lookups with direct event-driven updates
            var orchestrator = RequestOrchestrator.Instance;
            if (orchestrator != null)
            {
                orchestrator.OnActiveNpcChanged += HandleActiveNpcChanged;
                if (enableVerboseLogging)
                {
                    Debug.Log("[InterruptionManager] Subscribed to RequestOrchestrator.OnActiveNpcChanged");
                }
            }
            else
            {
                Debug.LogWarning("[InterruptionManager] RequestOrchestrator not found - interruption detection may not work");
            }
        }

        private void OnDestroy()
        {
            // Unsubscribe from RequestOrchestrator event
            // Use HasInstance to avoid errors during scene cleanup when RequestOrchestrator might already be destroyed
            if (RequestOrchestrator.HasInstance)
            {
                RequestOrchestrator.Instance.OnActiveNpcChanged -= HandleActiveNpcChanged;
            }
        }

        /// <summary>
        /// Handle active NPC change from RequestOrchestrator.
        /// Replaces expensive reflection calls with simple event-driven updates.
        /// Caches StreamingAudioPlayer for performance (GetComponent is expensive in Update loop).
        /// </summary>
        private void HandleActiveNpcChanged(NpcClientBase npcClient, INpcConfiguration npcConfig)
        {
            _activeNpcClient = npcClient;
            _activeNpcConfig = npcConfig;

            // Cache StreamingAudioPlayer for near-end detection (used in Update loop)
            // GetComponent is expensive - cache it when NPC changes instead of every frame
            if (npcClient != null)
            {
                _activeAudioPlayer = npcClient.GetComponent<StreamingAudioPlayer>();
                if (_activeAudioPlayer == null)
                {
                    _activeAudioPlayer = npcClient.GetComponentInChildren<StreamingAudioPlayer>();
                }

                if (enableVerboseLogging)
                {
                    Debug.Log($"[InterruptionManager] Active NPC changed: {npcClient.NpcName}, AudioPlayer cached: {_activeAudioPlayer != null}");
                }
            }
            else
            {
                _activeAudioPlayer = null;
                if (enableVerboseLogging)
                {
                    Debug.Log("[InterruptionManager] Active NPC cleared");
                }
            }
        }

        /// <summary>
        /// Get whether the NPC is actually producing audible speech (not just in response phase).
        /// Uses StreamingAudioPlayer's VAD to detect pauses within the response.
        /// CRITICAL: This is different from IsTalking which tracks response start/end.
        /// </summary>
        /// <param name="npcClient">NPC client to check</param>
        /// <returns>True if NPC is currently producing audible speech, false during pauses</returns>
        private bool GetNpcActualSpeech(NpcClientBase npcClient)
        {
            if (npcClient == null)
                return false;

            // Try to find StreamingAudioPlayer on the NPC client or its children
            var audioPlayer = npcClient.GetComponent<StreamingAudioPlayer>();
            if (audioPlayer == null)
            {
                audioPlayer = npcClient.GetComponentInChildren<StreamingAudioPlayer>();
            }

            if (audioPlayer != null)
            {
                // Use VAD-based speech detection for accurate pause detection
                return audioPlayer.IsNPCSpeaking;
            }

            // Fallback: if no StreamingAudioPlayer found, assume NPC is speaking if response is active
            // This maintains backwards compatibility with non-streaming audio systems
            return npcClient.IsTalking;
        }

        private void Update()
        {
            if (speechInputHandler == null)
                return;

            // OPTIMIZATION: Check PTT state FIRST before any operations
            bool pttActive = speechInputHandler.IsPushToTalkActive();

            // Early return if PTT not active - no need for interruption detection
            if (!pttActive)
            {
                _pttPressedDuringNpcSpeech = false;
                _overlapTimer = 0f;
                _hasValidInterruption = false; // CRITICAL: Reset for next PTT press
                return;
            }

            // PTT is active - check if we have an active NPC
            // _activeNpcClient is set via event subscription (no reflection!)
            if (_activeNpcClient == null)
                return;

            // Get interruption settings from cached config (no reflection!)
            bool allowInterruption = _activeNpcConfig?.AllowInterruption ?? true;
            float persistenceTime = _activeNpcConfig?.InterruptionPersistenceTime ?? 1.5f;

            // Get user speaking state from VAD (with calibration)
            bool userSpeaking = DetectUserSpeech();

            // Use IsTalking for response phase tracking
            bool npcResponding = _activeNpcClient.IsTalking;

            // CRITICAL: Use VAD-based speech detection to distinguish actual speech from pauses
            // This enables proper interruption type detection:
            // - Back-channeling: User says "ja" briefly, stops when NPC continues
            // - Real interruption: User persists talking over NPC speech
            // - Pause filling: User talks during NPC pause, stops when NPC resumes
            bool npcActuallySpeaking = GetNpcActualSpeech(_activeNpcClient);

            // Track if PTT was pressed while NPC was responding (crucial for buffer inclusion)
            // Use npcResponding (not npcActuallySpeaking) because we want to include buffer even during pauses
            if (npcResponding && !_pttPressedDuringNpcSpeech)
            {
                _pttPressedDuringNpcSpeech = true;
                if (enableVerboseLogging)
                {
                    Debug.Log("[InterruptionManager] PTT pressed during NPC response - buffer will be included");
                }
            }

            // Track NPC response time (use npcResponding for full response duration)
            if (npcResponding)
            {
                if (_npcResponseStartTime <= 0)
                {
                    _npcResponseStartTime = Time.time;
                    if (enableVerboseLogging)
                    {
                        Debug.Log($"[InterruptionManager] NPC started responding at {_npcResponseStartTime:F2}");
                    }
                }
                _npcStoppedSpeakingTime = 0; // Reset stop time while responding
            }
            else if (_npcResponseStartTime > 0)
            {
                // NPC just stopped responding
                if (_npcStoppedSpeakingTime <= 0)
                {
                    _npcStoppedSpeakingTime = Time.time;
                    if (enableVerboseLogging)
                    {
                        float duration = _npcStoppedSpeakingTime - _npcResponseStartTime;
                        Debug.Log($"[InterruptionManager] NPC stopped responding after {duration:F2}s");
                    }
                }
                _npcResponseStartTime = 0;
            }

            // Calculate NPC speaking duration (for logging only)
            float npcSpeakingDuration = _npcResponseStartTime > 0 ?
                Time.time - _npcResponseStartTime : 0f;

            // NEAR-END DETECTION:
            // Near-end = AudioStreamEnd received (no more audio coming) + buffer almost empty
            // This detects REAL near-end, not "after 1s of speaking"
            bool isNearEnd = false;

            if (_pttPressedDuringNpcSpeech)
            {
                // Use cached StreamingAudioPlayer for performance (cached in HandleActiveNpcChanged)
                if (_activeAudioPlayer != null)
                {
                    // CORRECT: Near-end = AudioStreamEnd received + BufferLevel < threshold
                    // IsReceivingResponse becomes false when AudioStreamEnd is received
                    bool streamCompleted = !_activeAudioPlayer.IsReceivingResponse;
                    float bufferRemaining = _activeAudioPlayer.BufferLevel;

                    isNearEnd = streamCompleted && bufferRemaining < nearEndThresholdSeconds;

                    if (enableVerboseLogging && streamCompleted)
                    {
                        Debug.Log($"[InterruptionManager] Stream completed, buffer remaining: {bufferRemaining:F2}s, threshold: {nearEndThresholdSeconds:F2}s, isNearEnd: {isNearEnd}");
                    }
                }

                // Also check if NPC recently stopped (within 500ms) - still counts as near-end
                if (!npcResponding && _npcStoppedSpeakingTime > 0)
                {
                    float timeSinceNpcStopped = Time.time - _npcStoppedSpeakingTime;
                    if (timeSinceNpcStopped < 0.5f) // 500ms grace period
                    {
                        isNearEnd = true;
                        if (enableVerboseLogging)
                        {
                            Debug.Log($"[InterruptionManager] Near-end: User continuing {timeSinceNpcStopped:F2}s after NPC stopped");
                        }
                    }
                }
            }

            // Process interruption detection
            // CRITICAL: Use npcActuallySpeaking for overlap detection to avoid false positives during pauses
            if (userSpeaking && npcActuallySpeaking)
            {
                _overlapTimer += Time.deltaTime;

                // NEAR-END: Use FULL persistenceTime from PersonaSO
                // Near-end still requires sustained overlap to avoid back-channeling false positives
                if (isNearEnd)
                {
                    if (_overlapTimer >= persistenceTime && !_hasValidInterruption)
                    {
                        _hasValidInterruption = true;
                        if (enableVerboseLogging)
                        {
                            Debug.Log($"[InterruptionManager] Near-end interruption detected after {_overlapTimer:F2}s persistence (NPC spoke for {npcSpeakingDuration:F1}s)");
                        }
                        OnInterruptionDetected();
                    }
                }
                // NORMAL INTERRUPTION: Only if allowed and actually overlapping with audible NPC speech
                else if (allowInterruption)
                {
                    if (_overlapTimer >= persistenceTime && !_hasValidInterruption)
                    {
                        _hasValidInterruption = true;
                        if (enableVerboseLogging)
                        {
                            Debug.Log($"[InterruptionManager] Normal interruption after {_overlapTimer:F2}s persistence");
                        }
                        OnInterruptionDetected();
                    }
                }
            }
            else
            {
                // Reset overlap timer if user not speaking OR NPC not actually producing speech (pause)
                // This ensures we don't count pauses as interruption overlap
                _overlapTimer = 0f;
            }
        }

        private bool DetectUserSpeech()
        {
            // Use SpeechInputHandler's VAD result
            return speechInputHandler?.IsUserSpeaking ?? false;
        }

        /// <summary>
        /// Check if an interruption has been detected
        /// </summary>
        public bool HasDetectedInterruption()
        {
            return _hasValidInterruption;
        }

        /// <summary>
        /// Clear the interruption flag (should be called after processing)
        /// </summary>
        public void ClearInterruptionFlag()
        {
            _hasValidInterruption = false;
            _overlapTimer = 0f;
            if (enableVerboseLogging)
            {
                Debug.Log("[InterruptionManager] Interruption flag cleared");
            }
        }

        /// <summary>
        /// Reset calibration when PTT is released
        /// </summary>
        public void ResetCalibration()
        {
            // VAD calibration is now handled by SpeechInputHandler
            if (enableVerboseLogging)
            {
                Debug.Log("[InterruptionManager] Reset requested (handled by SpeechInputHandler)");
            }
        }

        /// <summary>
        /// Called when an interruption is detected. Made public for testing.
        /// </summary>
        public void OnInterruptionDetected()
        {
            if (enableVerboseLogging)
            {
                Debug.Log("[InterruptionManager] Interruption detected - notifying active NPC to stop playback");
            }

            // Fire public event for AIBridgeRulesHandler to send PlayerInterruptedNPC to RuleSystem
            OnInterruption?.Invoke();

            // Fire event for tests (using null for persona object to avoid dependency)
            OnInterruptionDetectedEvent?.Invoke(null, "Interruption detected");

            // Use cached active NPC client (no reflection!)
            if (_activeNpcClient == null)
            {
                Debug.LogWarning("[InterruptionManager] No active NPC client to interrupt");
                return;
            }

            if (enableVerboseLogging)
            {
                Debug.Log($"[InterruptionManager] Active NPC: {_activeNpcClient.NpcName}, IsTalking: {_activeNpcClient.IsTalking}");
            }

            if (enableVerboseLogging)
            {
                Debug.Log($"[InterruptionManager] Stopping audio for {_activeNpcClient.NpcName}");
            }

            // Stop the NPC's audio playback (stops playback, clears buffer)
            _activeNpcClient.StopAudio();

            // Mark interruption in RequestOrchestrator and notify backend
            var orchestrator = RequestOrchestrator.Instance;
            if (orchestrator != null)
            {
                // Get the RequestId of the session being interrupted
                string interruptedRequestId = orchestrator.GetCurrentSessionId();

                // Notify backend: InterruptionOccurred (stop TTS, keep LLM for metadata)
                if (!string.IsNullOrEmpty(interruptedRequestId))
                {
                    orchestrator.SendInterruptionOccurredToBackend(interruptedRequestId, "User interrupted NPC");
                    if (enableVerboseLogging)
                    {
                        Debug.Log($"[InterruptionManager] Sent InterruptionOccurred to backend for session {interruptedRequestId}");
                    }
                }

                // Mark interruption flag for new session (IsInterruptionActive=true)
                orchestrator.StartInterruption();
                if (enableVerboseLogging)
                {
                    Debug.Log("[InterruptionManager] Marked interruption in RequestOrchestrator");
                }
            }
        }

        #region Test Support Methods

        /// <summary>
        /// Initialize for testing without requiring full NPC setup
        /// </summary>
        public void InitializeForTesting(bool allowInterruption = true, float persistenceTime = 1.5f)
        {
            // No local VAD needed - we use SpeechInputHandler's VAD
            if (enableVerboseLogging)
            {
                Debug.Log($"[InterruptionManager] Initialized for testing - Allow: {allowInterruption}, Persistence: {persistenceTime}");
            }
            // Test initialization doesn't require NPC provider
        }

        /// <summary>
        /// Handle PTT pressed event for testing
        /// </summary>
        public void HandlePTTPressed()
        {
            ResetCalibration();
            if (enableVerboseLogging)
            {
                Debug.Log("[InterruptionManager] PTT pressed - calibration reset");
            }
        }

        /// <summary>
        /// Check for interruption with audio frame (test compatibility)
        /// </summary>
        public bool CheckForInterruption(float[] audioFrame)
        {
            // Simple VAD check on audio frame for testing
            float rms = 0f;
            for (int i = 0; i < audioFrame.Length; i++)
            {
                rms += audioFrame[i] * audioFrame[i];
            }
            rms = Mathf.Sqrt(rms / audioFrame.Length);
            bool userSpeaking = rms > 0.01f; // Use fixed threshold for tests

            // For tests, assume NPC is talking if we're checking
            return CheckForInterruption(userSpeaking, true, Time.deltaTime);
        }

        /// <summary>
        /// Check for interruption with test parameters
        /// </summary>
        public bool CheckForInterruption(bool userSpeaking, bool npcSpeaking, float deltaTime,
            bool allowInterruption = true, float persistenceTime = 1.5f)
        {
            if (!allowInterruption)
                return false;

            if (userSpeaking && npcSpeaking)
            {
                _overlapTimer += deltaTime;

                if (_overlapTimer >= persistenceTime)
                {
                    if (!_hasValidInterruption)
                    {
                        _hasValidInterruption = true;
                        if (enableVerboseLogging)
                        {
                            Debug.Log($"[InterruptionManager] Test interruption detected after {_overlapTimer:F2}s");
                        }
                        return true;
                    }
                }
            }
            else
            {
                _overlapTimer = 0f;
            }

            return false;
        }

        #endregion
    }
}