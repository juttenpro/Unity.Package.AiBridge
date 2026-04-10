using System;
using System.Collections;
using UnityEngine;
using Tsc.AIBridge.Core;
using Tsc.AIBridge.Input;
using Tsc.AIBridge.Audio.Playback;

namespace Tsc.AIBridge.Audio.Interruption
{
    /// <summary>
    /// Event-driven interruption detection system that works with INpcConfiguration.
    /// Lives on the PLAYER GameObject alongside SpeechInputHandler.
    /// Uses events + coroutines instead of Update() for better performance and testability.
    /// No PersonaSO dependencies - works with any INpcConfiguration implementation.
    /// </summary>
    public class InterruptionManager : MonoBehaviour
    {
        /// <summary>
        /// Fallback persistence time used when no active NPC configuration is available.
        /// Matches the PersonaSO.persistenceTime default of 0.4s. Before v1.6.16 the fallback
        /// was 1.5s which silently made interruption 3.75x harder in edge cases.
        /// </summary>
        public const float DefaultPersistenceTimeFallback = 0.4f;

        [Header("Required Components")]
        [SerializeField] private SpeechInputHandler speechInputHandler;

        [Header("Near-End Detection")]
        [SerializeField]
        [Range(0.5f, 3.0f)]
        [Tooltip("Buffer threshold for near-end detection. When stream is complete and buffer < this value, near-end is triggered.")]
        private float nearEndThresholdSeconds = 1.0f;

        [SerializeField]
        [Range(0.1f, 1.0f)]
        [Tooltip("Persistence multiplier applied to persistenceTime during near-end. " +
                 "0.25 means near-end interrupts at 25% of normal persistence, e.g. 100ms " +
                 "instead of 400ms. Rationale: if the NPC is almost finished, holding the " +
                 "user back adds friction without preventing real conflicts.")]
        private float nearEndPersistenceMultiplier = 0.25f;

        [Header("NPC Pause Tolerance")]
        [SerializeField]
        [Range(0.0f, 1.0f)]
        [Tooltip("How long an NPC can be silent within an active response before the overlap " +
                 "timer resets. Without this tolerance, natural NPC pauses (breaths, commas) " +
                 "reset the timer and make interruption extremely difficult.")]
        private float npcPauseTolerance = 0.3f;

        [Header("Debug")]
        [SerializeField]
        [Tooltip("Enable detailed console logging for troubleshooting VAD calibration and interruption detection.")]
        private bool enableVerboseLogging = false;

        [SerializeField]
        [Tooltip("Always log a one-line production diagnostic when an interruption fires or " +
                 "when overlap accumulates but never reaches persistence. Safe for production.")]
        private bool enableProductionDiagnostics = true;

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

        // State tracking
        private bool _hasValidInterruption;
        private float _npcResponseStartTime;
        private bool _userInputStartedDuringNpcResponse;
        private float _npcStoppedSpeakingTime;

        // Coroutine tracking
        private Coroutine _overlapMonitorCoroutine;

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
            // Use HasInstance to avoid FindFirstObjectByType during scene transitions
            if (RequestOrchestrator.HasInstance)
            {
                RequestOrchestrator.Instance.OnActiveNpcChanged += HandleActiveNpcChanged;
                if (enableVerboseLogging)
                {
                    Debug.Log("[InterruptionManager] Subscribed to RequestOrchestrator.OnActiveNpcChanged");
                }
            }
            else
            {
                Debug.LogWarning("[InterruptionManager] RequestOrchestrator not found - interruption detection may not work");
            }

            // Subscribe to SpeechInputHandler events for user input tracking
            if (speechInputHandler != null)
            {
                speechInputHandler.OnRecordingStarted += OnUserInputStarted;
                speechInputHandler.OnRecordingStopped += OnUserInputStopped;
                if (enableVerboseLogging)
                {
                    Debug.Log("[InterruptionManager] Subscribed to SpeechInputHandler events");
                }
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

            // Unsubscribe from SpeechInputHandler events
            if (speechInputHandler != null)
            {
                speechInputHandler.OnRecordingStarted -= OnUserInputStarted;
                speechInputHandler.OnRecordingStopped -= OnUserInputStopped;
            }

            // Stop any running coroutines
            if (_overlapMonitorCoroutine != null)
            {
                StopCoroutine(_overlapMonitorCoroutine);
                _overlapMonitorCoroutine = null;
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

        /// <summary>
        /// Event handler: User started recording/input (PTT or voice activation)
        /// </summary>
        private void OnUserInputStarted()
        {
            if (enableVerboseLogging)
            {
                Debug.Log("[InterruptionManager] User input started");
            }

            // Check if NPC is currently responding
            bool npcResponding = _activeNpcClient?.IsTalking ?? false;

            if (npcResponding)
            {
                // User started input during NPC response - track for buffer inclusion
                _userInputStartedDuringNpcResponse = true;

                // Track NPC response start time if not already set
                if (_npcResponseStartTime <= 0)
                {
                    _npcResponseStartTime = Time.time;
                }

                if (enableVerboseLogging)
                {
                    Debug.Log("[InterruptionManager] User input started during NPC response - starting overlap monitoring");
                }

                // Start monitoring for interruption
                StartOverlapMonitoring();
            }
            else
            {
                _userInputStartedDuringNpcResponse = false;
                if (enableVerboseLogging)
                {
                    Debug.Log("[InterruptionManager] User input started - NPC not responding, no monitoring needed");
                }
            }
        }

        /// <summary>
        /// Event handler: User stopped recording/input
        /// </summary>
        private void OnUserInputStopped()
        {
            if (enableVerboseLogging)
            {
                Debug.Log("[InterruptionManager] User input stopped - stopping overlap monitoring");
            }

            // Stop overlap monitoring
            StopOverlapMonitoring();

            // Reset state for next input session
            _userInputStartedDuringNpcResponse = false;
            _hasValidInterruption = false;
        }

        /// <summary>
        /// Start the overlap monitoring coroutine
        /// </summary>
        private void StartOverlapMonitoring()
        {
            // Stop any existing monitoring
            StopOverlapMonitoring();

            // Start new monitoring coroutine
            _overlapMonitorCoroutine = StartCoroutine(MonitorOverlapCoroutine());
        }

        /// <summary>
        /// Stop the overlap monitoring coroutine
        /// </summary>
        private void StopOverlapMonitoring()
        {
            if (_overlapMonitorCoroutine != null)
            {
                StopCoroutine(_overlapMonitorCoroutine);
                _overlapMonitorCoroutine = null;
            }
        }

        /// <summary>
        /// Pure helper: decide whether overlap is long enough to trigger interruption.
        /// Extracted in v1.6.16 so the decision logic is unit-testable without spinning up
        /// a coroutine, Unity scene, NPC client, or WebSocket connection. This is where the
        /// bug-2 fix lives: near-end now uses a reduced persistence time instead of copying
        /// the normal path.
        /// </summary>
        /// <param name="overlapTimer">Accumulated overlap duration in seconds.</param>
        /// <param name="persistenceTime">Normal persistence threshold from PersonaSO.</param>
        /// <param name="isNearEnd">True when NPC stream is complete and buffer is near-drained.</param>
        /// <param name="allowInterruption">Whether the active persona allows normal interruption.</param>
        /// <param name="nearEndPersistenceMultiplier">Fraction of persistenceTime to require when near-end.</param>
        /// <returns>True if an interruption should fire.</returns>
        public static bool ShouldInterrupt(
            float overlapTimer,
            float persistenceTime,
            bool isNearEnd,
            bool allowInterruption,
            float nearEndPersistenceMultiplier)
        {
            if (isNearEnd)
            {
                // Near-end overrides allowInterruption: if the NPC is nearly done anyway,
                // blocking the user adds friction without preventing real conflicts.
                var reducedPersistence = persistenceTime * nearEndPersistenceMultiplier;
                return overlapTimer >= reducedPersistence;
            }

            if (!allowInterruption)
                return false;

            return overlapTimer >= persistenceTime;
        }

        /// <summary>
        /// Result of one <see cref="UpdateOverlapTimer"/> tick: the new overlap timer value
        /// and the new NPC pause accumulator.
        /// </summary>
        public readonly struct OverlapTickResult
        {
            public readonly float OverlapTimer;
            public readonly float NpcPauseAccumulator;

            public OverlapTickResult(float overlapTimer, float npcPauseAccumulator)
            {
                OverlapTimer = overlapTimer;
                NpcPauseAccumulator = npcPauseAccumulator;
            }
        }

        /// <summary>
        /// Pure helper: compute the next overlap-timer state given the current state and
        /// this frame's speaking states. Extracted in v1.6.16 to fix bug 3 (NPC micro-pauses
        /// resetting the timer) and to make the logic unit-testable.
        ///
        /// Rules:
        /// - If user AND NPC are both producing speech: accumulate overlap, reset pause accumulator.
        /// - If user is still speaking and the NPC response is still active but the NPC is
        ///   briefly silent (breathing, commas): keep accumulating overlap until the pause
        ///   accumulator exceeds <paramref name="npcPauseTolerance"/>. This allows users to
        ///   interrupt through natural NPC pauses.
        /// - If the pause exceeds tolerance, user stops speaking, or NPC response ends:
        ///   reset both overlap and pause accumulator.
        /// </summary>
        public static OverlapTickResult UpdateOverlapTimer(
            float currentOverlap,
            float currentNpcPauseAccumulator,
            bool userSpeaking,
            bool npcActuallySpeaking,
            bool npcResponding,
            float deltaTime,
            float npcPauseTolerance)
        {
            if (userSpeaking && npcActuallySpeaking)
            {
                return new OverlapTickResult(currentOverlap + deltaTime, 0f);
            }

            if (userSpeaking && npcResponding && !npcActuallySpeaking)
            {
                // User still talking, NPC in a momentary pause within an active response.
                var newPauseAccum = currentNpcPauseAccumulator + deltaTime;
                if (newPauseAccum < npcPauseTolerance)
                {
                    return new OverlapTickResult(currentOverlap + deltaTime, newPauseAccum);
                }
                // Pause exceeded tolerance — treat this as real silence and reset.
                return new OverlapTickResult(0f, 0f);
            }

            // User stopped speaking OR NPC response ended — reset both.
            return new OverlapTickResult(0f, 0f);
        }

        /// <summary>
        /// Coroutine: Monitor for interruption while user and NPC are both potentially talking
        /// This replaces the Update() loop with event-driven + coroutine approach
        /// </summary>
        private IEnumerator MonitorOverlapCoroutine()
        {
            float overlapTimer = 0f;
            float npcPauseAccumulator = 0f;
            float maxOverlapReached = 0f; // Tracked for production diagnostics on failure.

            // Get interruption settings from cached config.
            // Before v1.6.16 the fallback was 1.5f, which silently made interruption 3.75x
            // harder when config was unavailable. Now it matches the PersonaSO default and
            // logs a warning so the fallback is visible instead of silent.
            bool allowInterruption;
            float persistenceTime;
            if (_activeNpcConfig != null)
            {
                allowInterruption = _activeNpcConfig.AllowInterruption;
                persistenceTime = _activeNpcConfig.InterruptionPersistenceTime;
            }
            else
            {
                allowInterruption = true;
                persistenceTime = DefaultPersistenceTimeFallback;
                Debug.LogWarning(
                    $"[InterruptionManager] No active NPC configuration — using fallback " +
                    $"persistence {DefaultPersistenceTimeFallback:F2}s. " +
                    $"This usually indicates a timing issue during scene load.");
            }

            if (enableVerboseLogging)
            {
                Debug.Log($"[InterruptionManager] Overlap monitoring started - " +
                          $"allowInterruption: {allowInterruption}, persistence: {persistenceTime:F2}s, " +
                          $"nearEndMultiplier: {nearEndPersistenceMultiplier:F2}, " +
                          $"npcPauseTolerance: {npcPauseTolerance:F2}s");
            }

            while (_activeNpcClient != null && speechInputHandler != null && speechInputHandler.IsUserInputActive)
            {
                // Get user speaking state from VAD
                bool userSpeaking = DetectUserSpeech();

                // Get NPC responding state
                bool npcResponding = _activeNpcClient.IsTalking;

                // CRITICAL: Use VAD-based speech detection to distinguish actual speech from pauses
                bool npcActuallySpeaking = GetNpcActualSpeech(_activeNpcClient);

                // Track NPC response time
                if (npcResponding)
                {
                    if (_npcResponseStartTime <= 0)
                    {
                        _npcResponseStartTime = Time.time;
                    }
                    _npcStoppedSpeakingTime = 0;
                }
                else if (_npcResponseStartTime > 0)
                {
                    // NPC stopped responding
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

                // Check for near-end condition
                bool isNearEnd = CheckNearEndCondition(npcResponding);

                // Update the overlap timer state via the pure helper (testable logic).
                var tickResult = UpdateOverlapTimer(
                    currentOverlap: overlapTimer,
                    currentNpcPauseAccumulator: npcPauseAccumulator,
                    userSpeaking: userSpeaking,
                    npcActuallySpeaking: npcActuallySpeaking,
                    npcResponding: npcResponding,
                    deltaTime: Time.deltaTime,
                    npcPauseTolerance: npcPauseTolerance);

                overlapTimer = tickResult.OverlapTimer;
                npcPauseAccumulator = tickResult.NpcPauseAccumulator;

                if (overlapTimer > maxOverlapReached)
                    maxOverlapReached = overlapTimer;

                if (enableVerboseLogging && overlapTimer > 0f)
                {
                    Debug.Log($"[InterruptionManager] Overlap: {overlapTimer:F2}s / {persistenceTime:F2}s " +
                              $"(pauseAccum: {npcPauseAccumulator:F2}s, near-end: {isNearEnd})");
                }

                if (ShouldInterrupt(overlapTimer, persistenceTime, isNearEnd, allowInterruption,
                        nearEndPersistenceMultiplier) && !_hasValidInterruption)
                {
                    _hasValidInterruption = true;
                    if (enableProductionDiagnostics)
                    {
                        var vadInfo = speechInputHandler?.VadManager?.GetDiagnosticInfo() ?? "n/a";
                        Debug.Log($"[InterruptionManager] INTERRUPT fired after {overlapTimer:F2}s overlap " +
                                  $"(persistence {persistenceTime:F2}s, near-end {isNearEnd}, VAD {vadInfo})");
                    }
                    OnInterruptionDetected();
                    yield break; // Stop monitoring after interruption detected
                }

                yield return null; // Check every frame
            }

            if (enableProductionDiagnostics && maxOverlapReached > 0f && !_hasValidInterruption)
            {
                var vadInfo = speechInputHandler?.VadManager?.GetDiagnosticInfo() ?? "n/a";
                Debug.Log($"[InterruptionManager] Overlap session ended WITHOUT interrupt. " +
                          $"Max overlap: {maxOverlapReached:F2}s / required {persistenceTime:F2}s. VAD {vadInfo}");
            }

            if (enableVerboseLogging)
            {
                Debug.Log("[InterruptionManager] Overlap monitoring ended");
            }
        }

        /// <summary>
        /// Check if near-end condition is met
        /// </summary>
        private bool CheckNearEndCondition(bool npcResponding)
        {
            if (!_userInputStartedDuringNpcResponse)
                return false;

            // Use cached StreamingAudioPlayer for performance
            if (_activeAudioPlayer != null)
            {
                // Near-end = AudioStreamEnd received + BufferLevel < threshold
                bool streamCompleted = !_activeAudioPlayer.IsReceivingResponse;
                float bufferRemaining = _activeAudioPlayer.BufferLevel;

                bool isNearEnd = streamCompleted && bufferRemaining < nearEndThresholdSeconds;

                if (enableVerboseLogging && streamCompleted)
                {
                    Debug.Log($"[InterruptionManager] Stream completed, buffer: {bufferRemaining:F2}s, threshold: {nearEndThresholdSeconds:F2}s, near-end: {isNearEnd}");
                }

                if (isNearEnd)
                    return true;
            }

            // Also check if NPC recently stopped (within 500ms grace period)
            if (!npcResponding && _npcStoppedSpeakingTime > 0)
            {
                float timeSinceNpcStopped = Time.time - _npcStoppedSpeakingTime;
                if (timeSinceNpcStopped < 0.5f)
                {
                    if (enableVerboseLogging)
                    {
                        Debug.Log($"[InterruptionManager] Near-end: {timeSinceNpcStopped:F2}s after NPC stopped");
                    }
                    return true;
                }
            }

            return false;
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
            if (enableVerboseLogging)
            {
                Debug.Log("[InterruptionManager] Interruption flag cleared");
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
            if (RequestOrchestrator.HasInstance)
            {
                var orchestrator = RequestOrchestrator.Instance;
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
    }
}