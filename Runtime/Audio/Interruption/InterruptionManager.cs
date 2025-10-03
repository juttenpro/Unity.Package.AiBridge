using System;
using UnityEngine;
using Tsc.AIBridge.Core;
using Tsc.AIBridge.Input;

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

        private INpcProvider _npcProvider;
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

        /// <summary>
        /// Initialize with an NPC provider for accessing configurations
        /// </summary>
        public void Initialize(INpcProvider npcProvider)
        {
            _npcProvider = npcProvider;

            if (enableVerboseLogging)
            {
                Debug.Log("[InterruptionManager] Initialized with NPC provider");
            }
        }

        /// <summary>
        /// Get the currently active NPC configuration from RequestOrchestrator
        /// </summary>
        private INpcConfiguration GetActiveNpc()
        {
            // Get active NPC from RequestOrchestrator
            var orchestrator = RequestOrchestrator.Instance;
            if (orchestrator == null)
            {
                if (enableVerboseLogging)
                {
                    Debug.LogWarning("[InterruptionManager] RequestOrchestrator.Instance is null");
                }
                return null;
            }

            // Use reflection to get _activeNpcConfig (it's private)
            var activeNpcConfigField = typeof(RequestOrchestrator)
                .GetField("_activeNpcConfig", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            if (activeNpcConfigField != null)
            {
                var activeNpcConfig = activeNpcConfigField.GetValue(orchestrator) as INpcConfiguration;
                return activeNpcConfig;
            }

            if (enableVerboseLogging)
            {
                Debug.LogWarning("[InterruptionManager] Failed to get _activeNpcConfig via reflection");
            }

            return null;
        }

        private void Update()
        {
            if (speechInputHandler == null)
                return;

            var activeNpc = GetActiveNpc();
            if (activeNpc == null)
                return;

            bool pttActive = speechInputHandler.IsPushToTalkActive();
            bool userSpeaking = DetectUserSpeech();
            bool npcSpeaking = activeNpc.IsTalking;

            // Track if PTT was pressed while NPC was speaking (crucial for buffer inclusion)
            if (pttActive && npcSpeaking && !_pttPressedDuringNpcSpeech)
            {
                _pttPressedDuringNpcSpeech = true;
                if (enableVerboseLogging)
                {
                    Debug.Log("[InterruptionManager] PTT pressed during NPC speech - buffer will be included");
                }
            }

            // Track NPC response time
            if (npcSpeaking)
            {
                if (_npcResponseStartTime <= 0)
                {
                    _npcResponseStartTime = Time.time;
                    if (enableVerboseLogging)
                    {
                        Debug.Log($"[InterruptionManager] NPC started speaking at {_npcResponseStartTime:F2}");
                    }
                }
                _npcStoppedSpeakingTime = 0; // Reset stop time while speaking
            }
            else if (_npcResponseStartTime > 0)
            {
                // NPC just stopped speaking
                if (_npcStoppedSpeakingTime <= 0)
                {
                    _npcStoppedSpeakingTime = Time.time;
                    if (enableVerboseLogging)
                    {
                        float duration = _npcStoppedSpeakingTime - _npcResponseStartTime;
                        Debug.Log($"[InterruptionManager] NPC stopped after {duration:F2}s");
                    }
                }
                _npcResponseStartTime = 0;
            }

            // If PTT not active, reset tracking
            if (!pttActive)
            {
                _pttPressedDuringNpcSpeech = false;
                _overlapTimer = 0f;
                return;
            }

            // Calculate NPC speaking duration
            float npcSpeakingDuration = _npcResponseStartTime > 0 ?
                Time.time - _npcResponseStartTime : 0f;

            // SMART NEAR-END DETECTION:
            // 1. If NPC response was SHORT (<1s) and user pressed PTT during it, ALWAYS treat as near-end
            // 2. Otherwise use the configured threshold
            bool isShortResponse = npcSpeakingDuration > 0 && npcSpeakingDuration < nearEndThresholdSeconds;
            bool isNearEnd = false;

            if (_pttPressedDuringNpcSpeech)
            {
                // If it was a short response OR we've reached the threshold, it's near-end
                isNearEnd = isShortResponse || (npcSpeakingDuration >= nearEndThresholdSeconds);

                // Also check if NPC recently stopped (within 500ms) - still counts as near-end
                if (!npcSpeaking && _npcStoppedSpeakingTime > 0)
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
            if (userSpeaking)
            {
                _overlapTimer += Time.deltaTime;

                // NEAR-END: Quick trigger for buffer inclusion
                if (isNearEnd)
                {
                    // Near-end needs much shorter overlap (0.3s) to ensure buffer is included
                    if (_overlapTimer >= 0.3f && !_hasValidInterruption)
                    {
                        _hasValidInterruption = true;
                        if (enableVerboseLogging)
                        {
                            if (isShortResponse)
                            {
                                Debug.Log($"[InterruptionManager] Near-end for SHORT response ({npcSpeakingDuration:F1}s) - buffer included");
                            }
                            else
                            {
                                Debug.Log($"[InterruptionManager] Near-end after {npcSpeakingDuration:F1}s of NPC speech");
                            }
                        }
                        OnInterruptionDetected();
                    }
                }
                // NORMAL INTERRUPTION: Only if allowed and overlapping with NPC speech
                else if (npcSpeaking && activeNpc.AllowInterruption)
                {
                    if (_overlapTimer >= activeNpc.InterruptionPersistenceTime && !_hasValidInterruption)
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
            // Fire event for tests (using null for persona object to avoid dependency)
            OnInterruptionDetectedEvent?.Invoke(null, "Interruption detected");

            // Notify RequestOrchestrator or other systems
            // This could be an event or direct call
            var orchestrator = RequestOrchestrator.Instance;
            if (orchestrator != null)
            {
                // orchestrator.HandleInterruption();
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