using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Tsc.AIBridge.Audio.Capture;
using Tsc.AIBridge.Observability;
using UnityEngine;
using Tsc.AIBridge.Messages;
using Tsc.AIBridge.WebSocket;
using Tsc.AIBridge.Audio.Interruption;
using Tsc.AIBridge.Input;
using NativeWebSocket;

namespace Tsc.AIBridge.Core
{
    /// <summary>
    /// Central orchestrator for all API requests.
    /// Coordinates between input sources (player, NPC, system) and backend communication.
    ///
    /// USAGE:
    /// - Pass INpcConfiguration directly to StartAudioRequest() or StartTextRequest()
    /// - Or use StartConversationRequest() with a complete ConversationRequest
    /// - Optionally set an INpcProvider for dynamic NPC lookup by ID
    ///
    /// RESPONSIBILITIES:
    /// ✅ DO: Coordinate all request types (audio, text, analysis)
    /// ✅ DO: Manage request flow and buffering decisions
    /// ✅ DO: Delegate to appropriate services (WebSocket, Interruption, etc.)
    /// ✅ DO: Trigger NPC animations via INpcConfiguration events
    /// ✅ DO: Handle session lifecycle (start, end, cancel)
    ///
    /// ❌ DON'T: Handle audio recording (SpeechInputHandler's job)
    /// ❌ DON'T: Manage WebSocket connection (WebSocketClient's job)
    /// ❌ DON'T: Play audio (NpcClient's job)
    /// ❌ DON'T: Store chat history (NpcClient's job)
    /// ❌ DON'T: Make interruption decisions (InterruptionManager's job)
    ///
    /// PRINCIPLE: Orchestrate, don't implement!
    /// </summary>
    public class RequestOrchestrator : MonoBehaviour
    {
        #region Singleton

        private static RequestOrchestrator _instance;
        private static bool _isQuitting;

        /// <summary>
        /// Check if an instance exists without creating one or throwing errors.
        /// Safe to use during OnDestroy() for cleanup.
        /// </summary>
        public static bool HasInstance => _instance != null && !_isQuitting;

        public static RequestOrchestrator Instance
        {
            get
            {
                if (_instance == null)
                {
                    // Don't try to find instance during application quit
                    // This prevents errors during destruction sequence
                    if (!_isQuitting)
                    {
                        _instance = FindFirstObjectByType<RequestOrchestrator>();
                        if (_instance == null && Application.isPlaying)
                        {
                            Debug.LogWarning("[RequestOrchestrator] No instance found in scene. " +
                                             "This is expected after leaving a lesson scene while WebSocket is still connected.");
                        }
                    }
                }
                return _instance;
            }
        }

        #endregion

        #region Required Components

        [Header("Required Components")]
        [SerializeField] public SpeechInputHandler speechInputHandler;

        [Header("Optional Components")]
        [SerializeField] private InterruptionManager interruptionManager; // Optional interruption support

        [Header("NPC Provider (Optional)")]
        [Tooltip("Optional provider for dynamic NPC lookup by ID. If not set, pass INpcConfiguration directly to request methods.")]
        // NOTE: Using MonoBehaviour for Inspector compatibility. External packages (e.g., RuleSystem)
        // provide components that implement both MonoBehaviour and INpcProvider.
        // The cast warning is a false positive - the component WILL implement INpcProvider at runtime.
        [SerializeField] private MonoBehaviour npcProviderComponent; // Will be cast to INpcProvider
        private INpcProvider _npcProvider;

        [Header("Performance Monitoring")]
        [Tooltip("Enable latency metrics tracking for all conversations")]
        [SerializeField] private bool enableMetrics = true;

        [Header("Debug Settings")]
        [SerializeField]
        [Tooltip("Enable verbose logging for debugging")]
        private bool enableVerboseLogging;

        [Header("Turn Watchdog")]
        [SerializeField]
        [Tooltip("Fails a turn when the backend shows no first sign of life (transcript, audio, completion) within this many seconds after the turn could first have produced one — EndOfSpeech for a player turn, the TextInput send for an NPC-initiated one. Covers half-open connections and backend error paths that skip conversationComplete. 0 disables the watchdog.")]
        private float turnFirstSignalTimeoutSeconds = 120f;

        #endregion

        #region Events

        /// <summary>
        /// Fired when STT transcription is received
        /// </summary>
        /// <summary>(transcript, requestId) — the id names the turn this transcript belongs to.</summary>
        public event Action<string, string> OnTranscriptionReceived;

        /// <summary>
        /// Fired when STT fails (timeout, error, etc.)
        /// Allows distinguishing between silent PTT (no audio) and STT processing failures
        /// </summary>
        public event Action<AIBridge.Messages.NoTranscriptMessage> OnSttFailed;

        #endregion

        #region Private Fields

        private readonly Queue<AudioRequest> _audioRequestQueue = new();
        private readonly Queue<TextRequest> _textRequestQueue = new();

        private WebSocketClient _webSocketClient;
        private ConversationSession _micSession;

        // Every turn that has been started and not yet ended, keyed on its RequestId. Introduced
        // alongside _micSession on purpose: this step changes no meaning and no behaviour, it only
        // makes the set of live turns addressable and — the actual work — forces the inventory of exits
        // to be complete before the watchdog and the mic/turn split start depending on it.
        // See Docs/Architecture/Concurrent-Turns-Plan.md, steps 6, 8, 11 and 12.
        // Per-turn routing teardown bookkeeping — see NotifyTurnAudioFinished.
        private readonly Dictionary<string, TurnRoutingState> _routingTeardown =
            new Dictionary<string, TurnRoutingState>();

        private readonly Dictionary<string, ConversationSession> _liveSessions =
            new Dictionary<string, ConversationSession>();
        private INpcConfiguration _activeNpcConfig;
        private NpcClientBase _activeNpcClient; // Cache to avoid FindObjectsByType
        private bool _isProcessingRequest; // Queue management - prevents concurrent request STARTS
        private bool _isRequestActive; // Request lifecycle - true from StartAudioRequest until EndAudioRequest/Cancel
        private Coroutine _processQueueCoroutine;

        // Metadata-handler we are currently subscribed to for OnConversationComplete cleanup.
        // Tracked so the same handler is not subscribed twice (same-NPC retries) and so we can
        // unsubscribe symmetrically on NPC switch / cancel / destroy.
        // One entry per NpcClient we are subscribed to for conversationComplete. This was a single
        // slot, and registering a turn actively unsubscribed the previous NPC — so with turns live on
        // two NPCs only the last one's completion ever reached the orchestrator, and the other turn
        // could never be released by anything except the watchdog, which after the value-based rework
        // means failing a turn that had actually succeeded. A dictionary keyed by RequestId cannot
        // repair a notification that was never delivered, which is why this comes first.
        //
        // Keyed on the concrete NpcClientBase deliberately: Unity's destroyed-object "fake null" only
        // works through a concrete UnityEngine.Object reference, not through an interface.
        private readonly Dictionary<NpcClientBase, ConversationMetadataHandler> _completionSubscriptions =
            new Dictionary<NpcClientBase, ConversationMetadataHandler>();

        // Whether a pause is active — paused time must not count toward any turn's watchdog budget,
        // because PauseManager pauses backend streaming too. App-wide on purpose: the pause is.
        // The per-turn "has the backend shown a sign of life" flag lives on ConversationSession, where
        // it can be true for one turn and false for another.
        private bool _isPauseActive;

        /// <summary>
        /// Event fired when the active NPC changes.
        /// Used by InterruptionManager to track which NPC is currently active without reflection.
        /// Parameters: (activeNpcClient, activeNpcConfig)
        /// </summary>
        public event Action<NpcClientBase, INpcConfiguration> OnActiveNpcChanged;

        // Connection state tracking (prevent warning spam)
        private bool _wasConnectedLastFrame = true;
        private float _lastConnectionWarningTime;
        private const float CONNECTION_WARNING_COOLDOWN = 2.0f; // Only log warning every 2 seconds

        // Audio buffering during reconnection
        private readonly Queue<byte[]> _reconnectionAudioBuffer = new();
        private const int MAX_RECONNECTION_BUFFER_SIZE = 500; // ~10 seconds @ 16kHz with 20ms frames
        private int _reconnectionBufferOverflows;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Debug.LogWarning("[RequestOrchestrator] Multiple instances detected! Destroying duplicate.");
                Destroy(gameObject);
                return;
            }

            _instance = this;

            // Try to get INpcProvider from component
            if (npcProviderComponent != null)
            {
                if (npcProviderComponent is INpcProvider provider)
                {
                    _npcProvider = provider;
                }
                else
                {
                    Debug.LogError($"[RequestOrchestrator] Component {npcProviderComponent.name} does not implement INpcProvider!");
                }
            }

        }

        private void Start()
        {
            ValidateRequiredComponents();

            // ValidateRequiredComponents ensures speechInputHandler is not null
            // If we reach here, all required components are present

            // Subscribe to SpeechInputHandler's AudioStreamProcessor for encoded audio
            if (speechInputHandler.AudioStreamProcessor == null)
            {
                Debug.LogError("[RequestOrchestrator] AudioStreamProcessor is null! Audio encoding will not work!", this);
                enabled = false;
                return;
            }

            speechInputHandler.AudioStreamProcessor.OnOpusAudioEncoded += ProcessAudioChunk;
            if (enableVerboseLogging)
                Debug.Log("[RequestOrchestrator] Subscribed to AudioStreamProcessor.OnOpusAudioEncoded");

            // Subscribe to recording stopped event to send EndOfSpeech
            speechInputHandler.OnRecordingStopped += HandleRecordingStopped;
            if (enableVerboseLogging)
                Debug.Log("[RequestOrchestrator] Subscribed to SpeechInputHandler.OnRecordingStopped");

            _processQueueCoroutine = StartCoroutine(ProcessRequestQueues());

            // Subscribe to WebSocket disconnect to clear stale queued requests
            if (_webSocketClient != null)
            {
                _webSocketClient.OnDisconnected += HandleWebSocketDisconnected;
            }
        }

        /// <summary>
        /// Clears stale audio/text requests from the queue on WebSocket disconnect.
        /// Queued requests only contain config references (not audio data), so clearing is safe.
        /// Active audio chunks are preserved in the reconnection buffer.
        /// </summary>
        private void HandleWebSocketDisconnected(WebSocketCloseCode code)
        {
            var audioCount = _audioRequestQueue.Count;
            var textCount = _textRequestQueue.Count;

            if (audioCount > 0 || textCount > 0)
            {
                Debug.Log($"[RequestOrchestrator] WebSocket disconnected ({code}) — clearing {audioCount} audio and {textCount} text queued requests to prevent session mismatches");
                _audioRequestQueue.Clear();
                _textRequestQueue.Clear();
            }

            AbortAllLiveTurns($"WebSocket disconnected ({code})");
        }

        /// <summary>
        /// Abandons the in-flight turn and clears turn state so the session keeps working.
        /// </summary>
        /// <remarks>
        /// Shared by every path that discovers the turn cannot complete: an observed socket close
        /// (<see cref="HandleWebSocketDisconnected"/>) and a SessionStart that never reached a live
        /// socket (ProcessAudioRequest). Both used to be handled separately, and the
        /// faulted-SessionStart path did not reset anything at all — the turn stayed armed, so the
        /// failure only surfaced at push-to-talk release with "Cannot send end messages", after the
        /// user had already spoken a full sentence into a dead socket.
        ///
        /// Aborting fires OnSttFailed so the RuleSystem resets IsReactionBusy. Without that, no STT
        /// result ever arrives and the NPC stays unresponsive for the rest of the lesson.
        ///
        /// The <c>_isRequestActive</c> guard makes this idempotent: both paths can fire for the same
        /// turn, and the RuleSystem must not evaluate the sttFailed rule twice for one utterance.
        ///
        /// Audio still sitting in AudioStreamProcessor's encoder queue is deliberately not drained
        /// here — StartEncoding() clears it at the start of the next turn.
        /// </remarks>
        /// <param name="context">Short reason for the abort, used in the log line.</param>
        private void AbortActiveTurn(string context)
        {
            // The _isRequestActive guard keeps this idempotent: several paths can discover the same dead
            // turn, and the RuleSystem must not evaluate the sttFailed rule twice for one utterance.
            // Deliberately NOT conditional on the session pointer still being set: an armed recording
            // whose session was already cleared elsewhere still has to be reported, or no STT result ever
            // arrives and the NPC stays unresponsive for the rest of the lesson.
            if (_isRequestActive)
            {
                Debug.LogWarning($"[RequestOrchestrator] Active turn aborted — {context}");
                _isRequestActive = false;

                RaiseSttFailed(new AIBridge.Messages.NoTranscriptMessage
                {
                    RequestId = _micSession?.RequestId,
                    Reason = "ConnectionLost",
                    AudioDuration = 0,
                    SttProvider = "none"
                });
            }

            // Clear stale session state so the next PTT on the SAME NPC starts clean. Without this the
            // mic pointer references the aborted session and the same-NPC start path silently overwrites
            // it without resetting downstream state — symptom: the user tries again with the same NPC and
            // nothing happens. _isProcessingRequest is cleared defensively in case a ProcessXxxRequest
            // coroutine did not reach its finally.
            // An aborted turn never got an answer, so nothing more will arrive for it: routing can go
            // at the same time as the bookkeeping.
            var abortedRequestId = _micSession?.RequestId;
            ReleaseLiveSession(abortedRequestId);
            ForceReleaseTurnRouting(abortedRequestId);
            _micSession = null;
            _isProcessingRequest = false;
        }

        /// <summary>
        /// Fails ONE turn. The microphone's own turn goes through <see cref="AbortActiveTurn"/>, which
        /// also disarms the microphone and notifies the RuleSystem. Any other turn must do neither: the
        /// player may be mid-sentence into a turn of their own, and reporting "no transcript" for a
        /// bystander would cut that live utterance short.
        /// </summary>
        private void FailTurn(string requestId, string context)
        {
            if (string.IsNullOrEmpty(requestId))
                return;

            if (_micSession != null && _micSession.RequestId == requestId)
            {
                AbortActiveTurn(context);
                return;
            }

            if (_liveSessions.ContainsKey(requestId))
            {
                Debug.LogWarning($"[RequestOrchestrator] Turn {requestId} failed — {context}. " +
                                 "The microphone's own turn is untouched.");
                ReleaseLiveSession(requestId);
                ForceReleaseTurnRouting(requestId);
            }
        }

        /// <summary>
        /// Fails every live turn. A dead socket kills all of them, and each needs its own notification
        /// with its own id — one shared flag cannot express which of several turns just died.
        /// </summary>
        private void AbortAllLiveTurns(string context)
        {
            foreach (var requestId in new List<string>(_liveSessions.Keys))
                FailTurn(requestId, context);

            // The microphone's turn last and unconditionally: it may not be in the live set if something
            // other than the normal start path installed it. A no-op when it was already failed above.
            AbortActiveTurn(context);

            _isProcessingRequest = false;
        }

        private void OnDestroy()
        {
            // Unsubscribe from AudioStreamProcessor
            if (speechInputHandler != null && speechInputHandler.AudioStreamProcessor != null)
            {
                speechInputHandler.AudioStreamProcessor.OnOpusAudioEncoded -= ProcessAudioChunk;
            }

            // Unsubscribe from recording stopped event
            if (speechInputHandler != null)
            {
                speechInputHandler.OnRecordingStopped -= HandleRecordingStopped;
            }

            // Unsubscribe from WebSocket disconnect
            if (_webSocketClient != null)
            {
                _webSocketClient.OnDisconnected -= HandleWebSocketDisconnected;
            }

            // Unsubscribe from any lingering ConversationMetadataHandler subscription.
            UnregisterConversationCompletionHandler();

            if (_processQueueCoroutine != null)
            {
                StopCoroutine(_processQueueCoroutine);
                _processQueueCoroutine = null;
            }

            if (_instance == this)
            {
                _instance = null;
            }
        }

        private void OnApplicationQuit()
        {
            _isQuitting = true;
        }

        private void OnApplicationPause(bool pauseStatus)
        {
            if (UseExternalPauseSystem)
                return;

            HandlePauseStateChange(pauseStatus, "ApplicationPause");
        }

        #endregion

        #region Public API

        /// <summary>
        /// When true, OnApplicationPause is not handled internally.
        /// Set this when an external pause system (like PauseManager) manages pause state
        /// to prevent double-pause state corruption.
        /// </summary>
        public bool UseExternalPauseSystem { get; set; }

        /// <summary>
        /// Handle pause state change (called by OnApplicationPause or external pause systems).
        /// This is a public API to allow external pause systems (like PauseManager) to integrate
        /// with RequestOrchestrator without creating a dependency.
        /// </summary>
        /// <param name="isPaused">True if pausing, false if resuming</param>
        /// <param name="source">Source of the pause (for logging): "ApplicationPause", "TrainingPause", etc.</param>
        public void HandlePauseStateChange(bool isPaused, string source = "Unknown")
        {
            // Tracked for the turn watchdog: backend streaming is paused along with the client,
            // so silence while paused is legitimate and must not count toward the turn timeout.
            _isPauseActive = isPaused;

            if (isPaused)
            {
                // Pause - stop any active recording to prevent orphaned state
                // CRITICAL: Without this, recording state persists during pause while
                // the active request session may complete, causing "no active request" errors
                if (_isRequestActive && speechInputHandler != null && speechInputHandler.IsRecording)
                {
                    if (enableVerboseLogging)
                        Debug.Log($"[RequestOrchestrator] {source} paused during recording - stopping recording to prevent state desync");

                    // Stop recording cleanly
                    speechInputHandler.StopRecording();

                    // Reset request state
                    _isRequestActive = false;

                    // Clear reconnection buffer to prevent stale audio
                    _reconnectionAudioBuffer.Clear();
                }

                // CRITICAL: Pause ALL NPC audio playback to prevent NPCs talking during pause
                // Find all NPC clients in scene and pause their audio
                var npcClients = FindObjectsByType<NpcClientBase>(FindObjectsSortMode.None);
                foreach (var npc in npcClients)
                {
                    if (npc.AudioPlayer != null)
                    {
                        npc.AudioPlayer.PausePlayback();
                        if (enableVerboseLogging)
                            Debug.Log($"[RequestOrchestrator] {source} - Paused audio playback for NPC: {npc.NpcName}");
                    }
                }
            }
            else
            {
                // Resume - resume audio playback and reset recording state
                // User will need to press PTT again to start new recording
                // This is the safest approach - prevents resuming half-recorded audio

                // CRITICAL: Resume ALL NPC audio playback
                var npcClients = FindObjectsByType<NpcClientBase>(FindObjectsSortMode.None);
                foreach (var npc in npcClients)
                {
                    if (npc.AudioPlayer != null)
                    {
                        npc.AudioPlayer.ResumePlayback();
                        if (enableVerboseLogging)
                            Debug.Log($"[RequestOrchestrator] {source} - Resumed audio playback for NPC: {npc.NpcName}");
                    }
                }

                if (enableVerboseLogging)
                    Debug.Log($"[RequestOrchestrator] {source} resumed - ready for new recording");
            }
        }

        /// <summary>
        /// Set the NPC provider at runtime (for RuleSystem integration)
        /// </summary>
        public void SetNpcProvider(INpcProvider provider)
        {
            _npcProvider = provider;
            if (enableVerboseLogging)
                Debug.Log($"[RequestOrchestrator] NPC provider set: {provider?.GetType().Name ?? "null"}");
        }

        /// <summary>
        /// Start a conversation request (player-initiated with audio or NPC-initiated without audio)
        /// This is called when RuleSystem determines all conversation parameters
        /// </summary>
        public void StartConversationRequest(ConversationRequest request)
        {
            if (request == null)
            {
                Debug.LogError("[RequestOrchestrator] Cannot start conversation with null request!");
                return;
            }

            if (enableVerboseLogging)
            {
                Debug.Log($"[RequestOrchestrator] Starting conversation request - NPC: {request.NpcId}, " +
                         $"Type: {(request.IsNpcInitiated ? "NPC-initiated" : "Player-initiated")}, " +
                         $"STT: {request.SttProvider}, LLM: {request.LlmModel}");

                // DEBUG: Log message count and content in request
                Debug.Log($"[RequestOrchestrator] ConversationRequest has {request.Messages?.Count ?? 0} messages before adapter");
                if (request.Messages != null)
                {
                    foreach (var msg in request.Messages)
                    {
                        // Log full content for system prompts, truncate others at 2000 chars
                        var maxLength = msg.Role?.ToLower() == "system" ? 5000 : 2000;
                        var preview = msg.Content?.Length > maxLength
                            ? msg.Content.Substring(0, maxLength) + $"... [TRUNCATED, total {msg.Content.Length} chars]"
                            : msg.Content;
                        Debug.Log($"  - [{msg.Role}] {preview}");
                    }
                }
            }

            // Create a temporary configuration wrapper for the request
            var config = new ConversationRequestAdapter(request);

            // Route to appropriate flow based on IsNpcInitiated flag
            if (request.IsNpcInitiated)
            {
                // NPC-initiated: Skip STT, go directly to text input flow (with empty text)
                if (enableVerboseLogging)
                    Debug.Log("[RequestOrchestrator] Using text input flow for NPC-initiated conversation");
                StartTextRequest(config, "", request.RequestId, request); // Empty text = NPC initiates based on system prompt/history
            }
            else
            {
                // Player-initiated: Use normal audio/STT flow
                if (enableVerboseLogging)
                    Debug.Log("[RequestOrchestrator] Using audio request flow for player-initiated conversation");
                StartAudioRequest(config, request.RequestId, request);
            }
        }

        /// <summary>
        /// Start an audio request with NPC configuration
        /// </summary>
        public void StartAudioRequest(INpcConfiguration npcConfig, string requestId,
            ConversationRequest conversationRequest)
        {
            if (npcConfig == null)
            {
                Debug.LogError("[RequestOrchestrator] Cannot start audio request with null NPC configuration!");
                return;
            }

            if (enableVerboseLogging)
                Debug.Log($"[RequestOrchestrator] Starting audio request for NPC: {npcConfig.Name}");

            // Addressing a different NPC than the microphone's current turn. Compare against the turn's
            // own NpcId: _activeNpcConfig is written by the text path too, so it named whichever NPC
            // started ANY turn last — a bystander's spontaneous line made the next press read as a switch.
            if (_micSession != null && _micSession.NpcId != npcConfig.Id)
            {
                // Two separate things used to happen here under one name. Closing the previous RECORDING
                // is not a choice: one microphone turn may be live, whatever else happens. What becomes of
                // the ABANDONED NPC's answer IS a choice, and content makes it per turn — falling silent,
                // or finishing while the player already talks to someone else.
                var cancelAbandonedAnswer = _micSession.OnPlayerTurnsAway == PlayerTurnsAwayPolicy.CancelAnswer;

                if (enableVerboseLogging)
                    Debug.Log($"[RequestOrchestrator] Player turns from {_micSession.NpcName} to {npcConfig.Name}; " +
                              $"its answer is {(cancelAbandonedAnswer ? "cancelled" : "left to finish")}.");

                CancelCurrentSession("Switching to different NPC", cancelAbandonedAnswer);
            }

            if (conversationRequest == null)
            {
                Debug.LogError($"[RequestOrchestrator] Cannot start a turn for '{npcConfig.Id}' without a " +
                               "ConversationRequest — it carries this turn's messages, voice settings and " +
                               "context-cache name. Start turns through StartConversationRequest.");
                return;
            }

            // Get NPC client from provider - MUST be configured!
            if (_npcProvider == null)
            {
                Debug.LogError("[RequestOrchestrator] No NPC provider configured! Set 'npcProviderComponent' in Inspector to a component implementing INpcProvider (e.g., AIBridgeRulesHandler).");
                return;
            }

            // Into a LOCAL, published below only once this turn is really going ahead. The shared fields
            // used to be written before this lookup, so a provider miss left _activeNpcConfig pointing at
            // an NPC that had no client behind it.
            var npcClient = _npcProvider.GetNpcClient(npcConfig.Id);

            if (npcClient == null)
            {
                Debug.LogError($"[RequestOrchestrator] NPC provider returned null for NPC ID: '{npcConfig.Id}'. Check that NPC client exists in scene and matches this ID.");
                return;
            }

            if (enableVerboseLogging)
                Debug.Log($"[RequestOrchestrator] Found NPC client: {npcClient.NpcName} for ID: {npcConfig.Id}");

            // Snapshot the history for THIS turn, while the NPC it belongs to is still unambiguous.
            var messages = GetChatHistory(npcConfig, npcClient);

            _activeNpcConfig = npcConfig;
            _activeNpcClient = npcClient;

            // Subscribe to per-turn completion so the next user-turn starts with a fresh session.
            // Without this, _micSession lingers after the backend cleans up its session,
            // and the next recording-stopped event sends EndOfSpeech for the stale RequestId.
            RegisterConversationCompletionHandler(npcClient);

            // Notify listeners (e.g., InterruptionManager) about active NPC change
            OnActiveNpcChanged?.Invoke(npcClient, npcConfig);

            // NOTE: Buffering is now handled automatically by AudioStreamProcessor.StartEncoding()
            // Audio is ALWAYS buffered by default until FlushBuffer() is called after SessionStarted
            // This handles all scenarios: normal requests, interruptions, RuleSystem delays, reconnects

            // Generate request ID and create session IMMEDIATELY (before queueing)
            // CRITICAL FIX: This prevents race condition where HandleRecordingStopped() is called
            // before ProcessAudioRequest() has created the session. Without this, EndOfSpeech/EndOfAudio
            // messages are never sent, causing backend to never process the audio.
            // This issue is especially likely after WebSocket reconnects when queue processing may be delayed.
            requestId ??= Guid.NewGuid().ToString();
            var npcName = npcClient.NpcName;
            SetMicSession(new ConversationSession(npcName, requestId, npcConfig.Id,
                conversationRequest.OnPlayerTurnsAway));

            if (enableVerboseLogging)
                Debug.Log($"[RequestOrchestrator] Created session immediately: {requestId} for {npcName}");

            // Mark request as active - audio chunks can now be accepted (they will be buffered)
            _isRequestActive = true;

            // Start PTT duration tracking in latency tracker (after we have the client)
            var tracker = GetLatencyTracker(npcClient);
            if (tracker != null)
            {
                tracker.MarkRecordingStart();
                if (enableVerboseLogging)
                    Debug.Log($"[RequestOrchestrator] MarkRecordingStart() called for {npcClient.NpcName}");
            }
            else
            {
                if (enableVerboseLogging)
                    Debug.LogWarning($"[RequestOrchestrator] Could not call MarkRecordingStart() - LatencyTracker is null");
            }

            // Note: Animation events should be triggered via the NPC client, not directly

            // Queue the request with the same RequestId
            var request = new AudioRequest
            {
                NpcConfig = npcConfig,
                RequestId = requestId,  // Use same RequestId as session
                Request = conversationRequest,
                NpcClient = npcClient,
                Messages = messages,
            };

            _audioRequestQueue.Enqueue(request);
            if (enableVerboseLogging)
                Debug.Log($"[RequestOrchestrator] Audio request queued. Queue size: {_audioRequestQueue.Count}");
        }

        /// <summary>
        /// Start a text-based request (NPC-initiated or system)
        /// </summary>
        public void StartTextRequest(INpcConfiguration npcConfig, string text, string requestId,
            ConversationRequest conversationRequest)
        {
            // Note: text can be empty string for NPC-initiated conversations (NPC speaks first without player input)
            if (npcConfig == null || text == null)
            {
                Debug.LogError("[RequestOrchestrator] Invalid text request parameters!");
                return;
            }

            var isNpcInitiated = string.IsNullOrEmpty(text);
            if (enableVerboseLogging)
                Debug.Log($"[RequestOrchestrator] Starting text request for {npcConfig.Name}" +
                         (isNpcInitiated ? " (NPC-initiated, no player input)" : $": {text}"));

            if (conversationRequest == null)
            {
                Debug.LogError($"[RequestOrchestrator] Cannot start a turn for '{npcConfig.Id}' without a " +
                               "ConversationRequest — it carries this turn's messages, voice settings and " +
                               "context-cache name. Start turns through StartConversationRequest.");
                return;
            }

            // Get NPC client from provider - MUST be configured!
            if (_npcProvider == null)
            {
                Debug.LogError("[RequestOrchestrator] No NPC provider configured! Set 'npcProviderComponent' in Inspector to a component implementing INpcProvider (e.g., AIBridgeRulesHandler).");
                return;
            }

            // Into a LOCAL, published below only once this turn is really going ahead. The shared fields
            // used to be written before this lookup, so a provider miss left _activeNpcConfig pointing at
            // an NPC that had no client behind it.
            var npcClient = _npcProvider.GetNpcClient(npcConfig.Id);

            if (npcClient == null)
            {
                Debug.LogError($"[RequestOrchestrator] NPC provider returned null for NPC ID: '{npcConfig.Id}'. Check that NPC client exists in scene and matches this ID.");
                return;
            }

            if (enableVerboseLogging)
                Debug.Log($"[RequestOrchestrator] Found NPC client: {npcClient.NpcName} for ID: {npcConfig.Id}");

            // Snapshot the history for THIS turn, while the NPC it belongs to is still unambiguous.
            var messages = GetChatHistory(npcConfig, npcClient);

            _activeNpcConfig = npcConfig;
            _activeNpcClient = npcClient;

            // Subscribe to per-turn completion so the next user-turn starts with a fresh session.
            // Without this, _micSession lingers after the backend cleans up its session,
            // and the next recording-stopped event sends EndOfSpeech for the stale RequestId.
            RegisterConversationCompletionHandler(npcClient);

            // Notify listeners (e.g., InterruptionManager) about active NPC change
            OnActiveNpcChanged?.Invoke(npcClient, npcConfig);

            var request = new TextRequest
            {
                NpcConfig = npcConfig,
                Text = text,
                RequestId = requestId ?? Guid.NewGuid().ToString(),
                Request = conversationRequest,
                NpcClient = npcClient,
                Messages = messages,
            };

            _textRequestQueue.Enqueue(request);
            if (enableVerboseLogging)
                Debug.Log($"[RequestOrchestrator] Text request queued. Queue size: {_textRequestQueue.Count}");
        }

        /// <summary>
        /// Called when PTT is released or voice activation ends
        /// </summary>
        public void EndAudioRequest()
        {
            // Note: Animation events should be triggered via the NPC client, not directly

            speechInputHandler?.StopRecording();

            // Request is no longer accepting new audio chunks
            _isRequestActive = false;

            if (enableVerboseLogging)
                Debug.Log("[RequestOrchestrator] Audio request ended (PTT released)");
        }

        /// <summary>
        /// Cancel the current session
        /// </summary>
        public void CancelCurrentSession(string reason = "User cancelled", bool cancelBackendAnswer = true)
        {
            if (_micSession != null)
            {
                if (enableVerboseLogging)
                    Debug.Log($"[RequestOrchestrator] Cancelling session {_micSession.RequestId}: {reason}");

                // Discard any buffered audio (RuleSystem rejection or interruption)
                if (speechInputHandler?.AudioStreamProcessor != null)
                {
                    speechInputHandler.AudioStreamProcessor.DiscardBuffer();
                    if (enableVerboseLogging)
                        Debug.Log("[RequestOrchestrator] Discarded buffered audio due to session cancellation");
                }

                // Stop any ongoing recording
                speechInputHandler?.StopRecording();

                var requestIdToCancel = _micSession.RequestId;

                // Cancel WebSocket session - notify backend to stop LLM/TTS generation.
                // Skipped when content wants the abandoned NPC to finish: then the turn is only released
                // locally and the backend keeps generating, so the answer still arrives and plays.
                if (cancelBackendAnswer)
                {
                    _ = CancelSessionOnBackendAsync(requestIdToCancel, reason);
                }
                else if (enableVerboseLogging)
                {
                    Debug.Log($"[RequestOrchestrator] Leaving turn {requestIdToCancel} running on the backend — " +
                              "content asked for the abandoned answer to finish.");
                }

                // Deliberately NOT unsubscribing here any more. Cancelling the microphone's turn says
                // nothing about the other NPCs whose turns may still be running, and dropping their
                // subscriptions is how their completions went unheard and their turns unreleased.
                // Subscriptions end when the NpcClient is destroyed.

                // Clear session. Routing only stops when the backend was told to stop: with
                // LetAnswerFinish the answer is still coming and still has to be routed and played.
                ReleaseLiveSession(requestIdToCancel);
                if (cancelBackendAnswer)
                    ForceReleaseTurnRouting(requestIdToCancel);
                _micSession = null;
                _activeNpcConfig = null;
                _activeNpcClient = null;
                _isProcessingRequest = false;

                // Notify listeners that there's no active NPC anymore
                OnActiveNpcChanged?.Invoke(null, null);
                _isRequestActive = false;
            }
        }

        /// <summary>
        /// Internal async method to send SessionCancel message to backend
        /// </summary>
        private async System.Threading.Tasks.Task CancelSessionOnBackendAsync(string requestId, string reason)
        {
            if (_webSocketClient == null)
            {
                Debug.LogWarning("[RequestOrchestrator] Cannot send SessionCancel - WebSocketClient is null");
                return;
            }

            try
            {
                var cancelMessage = new SessionCancelMessage
                {
                    RequestId = requestId,
                    Reason = reason
                };

                await _webSocketClient.SendSessionCancelAsync(cancelMessage);
                if (enableVerboseLogging)
                    Debug.Log($"[RequestOrchestrator] Sent SessionCancel to backend for session {requestId} (reason: {reason})");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[RequestOrchestrator] Failed to send SessionCancel: {ex.Message}");
            }
        }

        /// <summary>
        /// Send InterruptionOccurred message to backend (stop TTS, keep LLM for metadata)
        /// Does NOT clear session state - backend will send conversationComplete with wasInterrupted=true
        /// </summary>
        public void SendInterruptionOccurredToBackend(string requestId, string reason)
        {
            if (string.IsNullOrEmpty(requestId))
            {
                Debug.LogWarning("[RequestOrchestrator] Cannot send InterruptionOccurred - requestId is null or empty");
                return;
            }

            _ = InterruptionOccurredOnBackendAsync(requestId, reason);
        }

        /// <summary>
        /// Internal async method to send InterruptionOccurred message to backend
        /// </summary>
        private async System.Threading.Tasks.Task InterruptionOccurredOnBackendAsync(string requestId, string reason)
        {
            if (_webSocketClient == null)
            {
                Debug.LogWarning("[RequestOrchestrator] Cannot send InterruptionOccurred - WebSocketClient is null");
                return;
            }

            try
            {
                var interruptionMessage = new InterruptionOccurredMessage
                {
                    RequestId = requestId,
                    Reason = reason
                };

                await _webSocketClient.SendInterruptionOccurredAsync(interruptionMessage);
                if (enableVerboseLogging)
                    Debug.Log($"[RequestOrchestrator] Sent InterruptionOccurred to backend for session {requestId} (reason: {reason})");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[RequestOrchestrator] Failed to send InterruptionOccurred: {ex.Message}");
            }
        }

        /// <summary>
        /// Whether this turn has been started and not yet completed, cancelled, failed or displaced.
        /// </summary>
        public bool IsTurnLive(string requestId)
            => !string.IsNullOrEmpty(requestId) && _liveSessions.ContainsKey(requestId);

        /// <summary>
        /// Makes <paramref name="session"/> the turn the MICROPHONE is feeding, and adds it to the live
        /// set. There is exactly one of these: the player has one mouth, and upstream audio carries no
        /// request id, so the backend can only attribute speech to the most recently opened session.
        ///
        /// A session it displaces is released here, because nothing else will: a re-press on the same NPC
        /// replaces it deliberately and sends no backend cancel — ten rapid presses are one continuous
        /// session by design (RequestOrchestratorAudioTests.RapidPTTPresses_HandlesGracefully).
        /// </summary>
        private void SetMicSession(ConversationSession session)
        {
            if (_micSession != null && _micSession.RequestId != session.RequestId)
            {
                if (enableVerboseLogging)
                    Debug.Log($"[RequestOrchestrator] Microphone turn {_micSession.RequestId} displaced by " +
                              $"{session.RequestId} (no backend cancel — same-NPC re-press) — releasing the displaced turn.");

                ReleaseLiveSession(_micSession.RequestId);
            }

            // The player pressing to talk at an NPC who is already answering is barge-in, and the
            // player's own action always wins: the older turn is released rather than the press refused.
            // The mirror case in RegisterLiveSession goes the other way for exactly the same reason —
            // there it is a background rule that loses, not the person holding the button.
            var displaced = FindOtherLiveTurnForNpc(session.NpcId, session.RequestId);
            if (displaced != null)
            {
                Debug.LogWarning($"[RequestOrchestrator] '{session.NpcId}' still had a live turn ({displaced}) " +
                                 $"when the player pressed to talk ({session.RequestId}). Releasing the older one — " +
                                 "one NPC cannot decode two turns at once, and the player's press wins.");
                ReleaseLiveSession(displaced);
            }

            _micSession = session;
            _liveSessions[session.RequestId] = session;
        }

        /// <summary>
        /// Adds a turn to the live set WITHOUT making it the microphone's turn. This is the
        /// character-speaks-first path: the player is not talking into it, so it must not touch the
        /// microphone's bookkeeping — that is the whole point of the mic/turn split.
        ///
        /// Refuses a second live turn for an NPC that already has one, and returns false; the caller
        /// must then not send anything. One live turn per NPC is a structural ceiling, not a policy
        /// choice: AudioMessageHandler.OnNewRequest calls Reset() the moment the requestId changes, and
        /// there is one decoding AudioStreamProcessor, one StreamingAudioPlayer and one
        /// ConversationMetadataHandler.LastRequestId per NpcClient. A second turn cannot be heard.
        ///
        /// The NEW turn loses, not the running one. This used to release the older turn, which cut off
        /// an answer already being spoken in favour of one the same NPC could not play either — and the
        /// caller had no way to know, so it sent the request anyway. Both mistakes are gone.
        ///
        /// Deliberately NOT raising SttFailed for the refused turn, though the plan said to: OnSttFailed
        /// is handled against _personaAddressed, so it would fire the "sorry, I didn't understand you"
        /// rule at whoever the player is actually talking to and wipe their last recognised text. The
        /// refusal is caught one level up, in AIBridgeRulesHandler.StartConversation, before the turn
        /// exists at all — this is the backstop for anything that reaches the orchestrator directly.
        /// </summary>
        /// <returns>False when the turn was refused and must not be sent.</returns>
        private bool RegisterLiveSession(ConversationSession session)
        {
            var conflicting = FindOtherLiveTurnForNpc(session.NpcId, session.RequestId);
            if (conflicting != null)
            {
                Debug.LogWarning($"[RequestOrchestrator] Refusing turn {session.RequestId}: '{session.NpcId}' " +
                                 $"already has a live turn ({conflicting}) and cannot decode two at once. " +
                                 "The running turn keeps the NPC; this one is dropped before anything is sent.");
                return false;
            }

            _liveSessions[session.RequestId] = session;
            return true;
        }

        /// <summary>
        /// The RequestId of another live turn belonging to <paramref name="npcId"/>, or null. Null or
        /// empty npcId means "cannot tell", which must never look like a conflict.
        /// </summary>
        private string FindOtherLiveTurnForNpc(string npcId, string exceptRequestId)
        {
            if (string.IsNullOrEmpty(npcId))
                return null;

            foreach (var live in _liveSessions)
            {
                if (live.Value.NpcId == npcId && live.Key != exceptRequestId)
                    return live.Key;
            }

            return null;
        }

        /// <summary>
        /// Whether <paramref name="npcId"/> has a turn in flight right now. Asked by the RuleSystem side
        /// BEFORE it mints a turn id, so a second character-speaks-first line for the same NPC is
        /// dropped while nothing has been registered, adopted or flagged as awaiting a response yet.
        /// </summary>
        public bool HasLiveTurnForNpc(string npcId) => FindOtherLiveTurnForNpc(npcId, null) != null;

        /// <summary>
        /// Drops a turn from the live set. Nothing else: a turn's BOOKKEEPING ends at
        /// `conversationComplete`, and its message ROUTING must outlive that. Tearing routing down here
        /// is a mistake this method has now made twice.
        ///
        /// The backend sends `conversationComplete` about 200 ms after the FIRST audio chunk of a turn,
        /// not after the last. So at this moment the NPC is usually still speaking, and two things are
        /// still needed:
        ///
        /// * **`WebSocketClient._npcHandlers`** — the rest of the audio and the `audioStreamEnd` route
        ///   through it. v5.6.0 dropped it here and every second turn died: the stream never closed, the
        ///   NPC stayed "talking" with an empty buffer, and the player's next press was classified as an
        ///   interruption attempt and silently discarded. Unroutable binary audio also logs a
        ///   `Debug.LogError`, which ends the lesson.
        /// * **`NpcMessageRouter`** — it answers "which turn is this NPC playing", which is exactly what
        ///   `InterruptionManager.ResolveInterruptedTurnId` asks in order to tell the backend to stop the
        ///   TTS of an interrupted answer. Clearing it at completion meant that lookup returned null for
        ///   any interruption more than ~200 ms into the NPC's speech, i.e. all of them: since v5.2.1
        ///   every approved interruption logged "could not resolve its turn id" and the backend was never
        ///   told, so the TTS ran to completion unheard and at cost. It is also what
        ///   `NpcAudioPlayer.SendPauseStream` / `SendResumeStream` consult.
        ///
        /// Both are released by <see cref="ReleaseTurnRouting"/>, at the moment the turn's audio is
        /// genuinely finished — or immediately on the paths where no audio can still arrive.
        ///
        /// And with <see cref="PlayerTurnsAwayPolicy.LetAnswerFinish"/> the whole point is that an
        /// answer keeps arriving after its turn was released locally.
        /// </summary>
        private void ReleaseLiveSession(string requestId)
        {
            if (string.IsNullOrEmpty(requestId))
                return;

            _liveSessions.Remove(requestId);
        }

        /// <summary>
        /// Records that this turn's audio has finished playing, and stops routing it if the backend is
        /// also done with it.
        ///
        /// **Playback ending is NOT on its own proof that no more audio can arrive**, and assuming it was
        /// ended a lesson. A same-NPC re-press displaces the previous turn WITHOUT cancelling it on the
        /// backend — ten rapid presses are one continuous session by design — and the new request resets
        /// that NPC's decoder, so the displaced turn's playback "finishes" immediately while the backend
        /// is still streaming it. Releasing the routing there left the rest of that stream unroutable,
        /// and unroutable audio used to be a fatal error (session log 2026-09-08 09:14: routing released
        /// at 09:14:36.936, hundreds of dropped chunks from 09:14:37.155, lesson over).
        ///
        /// So both sides have to agree: the backend has finished with the turn (conversationComplete) and
        /// the audio has played. Whichever arrives second releases the routing. The paths that know no
        /// audio can possibly arrive — an explicit backend cancel, a timeout, a dead socket — call
        /// <see cref="ForceReleaseTurnRouting"/> instead.
        /// </summary>
        public void NotifyTurnAudioFinished(string requestId)
        {
            if (string.IsNullOrEmpty(requestId))
                return;

            var state = RoutingStateFor(requestId);
            state.PlaybackFinished = true;
            ReleaseTurnRoutingIfSettled(requestId, state);
        }

        /// <summary>
        /// Stops routing this turn now, for the paths where no audio can still arrive: the backend was
        /// explicitly cancelled, the turn timed out, or the socket died. Idempotent.
        /// </summary>
        public void ForceReleaseTurnRouting(string requestId)
        {
            if (string.IsNullOrEmpty(requestId))
                return;

            _routingTeardown.Remove(requestId);
            StopRoutingTurn(requestId, "no further audio is possible");
        }

        /// <summary>Called from the completion hook: the backend is finished with this turn.</summary>
        private void MarkTurnFinishedByBackend(string requestId)
        {
            if (string.IsNullOrEmpty(requestId))
                return;

            var state = RoutingStateFor(requestId);
            state.FinishedByBackend = true;
            ReleaseTurnRoutingIfSettled(requestId, state);
        }

        private TurnRoutingState RoutingStateFor(string requestId)
        {
            if (!_routingTeardown.TryGetValue(requestId, out var state))
            {
                state = new TurnRoutingState();
                _routingTeardown[requestId] = state;
            }

            return state;
        }

        private void ReleaseTurnRoutingIfSettled(string requestId, TurnRoutingState state)
        {
            if (!state.FinishedByBackend || !state.PlaybackFinished)
            {
                if (enableVerboseLogging)
                    Debug.Log($"[RequestOrchestrator] Keeping routing for {requestId} — backend done: " +
                              $"{state.FinishedByBackend}, audio played: {state.PlaybackFinished}.");
                return;
            }

            _routingTeardown.Remove(requestId);
            StopRoutingTurn(requestId, "the backend is done and the audio has played");
        }

        private void StopRoutingTurn(string requestId, string because)
        {
            _webSocketClient?.UnregisterNpc(requestId);

            if (NpcMessageRouter.HasInstance)
                NpcMessageRouter.Instance.ClearRequest(requestId);

            if (enableVerboseLogging)
                Debug.Log($"[RequestOrchestrator] Stopped routing turn {requestId} — {because}.");
        }

        /// <summary>
        /// What still has to happen before a turn's message routing can come down. Kept outside
        /// _liveSessions on purpose: a turn leaves the live set at conversationComplete, which is the
        /// earliest of the two signals, not the latest.
        /// </summary>
        private sealed class TurnRoutingState
        {
            public bool FinishedByBackend;
            public bool PlaybackFinished;
        }

        /// <summary>
        /// Audio streams received for <paramref name="requestId"/>, or 0 when no such turn is live.
        /// Answers for ANY live turn: the count decides whether a completion has to clean the turn up
        /// itself, and that question is just as real for a character-speaks-first turn as for the
        /// player's own.
        /// </summary>
        public int GetStreamsReceived(string requestId)
        {
            if (string.IsNullOrEmpty(requestId) || !_liveSessions.TryGetValue(requestId, out var session))
                return 0;

            return session.StreamsReceived;
        }

        /// <summary>
        /// Mark that audio stream has been received for the current session.
        /// Should be called when the first binary audio chunk is received.
        /// This prevents premature session completion when conversationComplete arrives.
        /// </summary>
        public void MarkAudioStreamReceived(string requestId)
        {
            // Audio is proof of backend life for THE TURN IT BELONGS TO. This used to stamp whatever the
            // microphone's session happened to be, so any NPC's first audio chunk silenced another turn's
            // watchdog and flipped its StreamsReceived — including a pre-recorded scripted clip, since the
            // hook is wired to AudioPlayer.OnPlaybackStarted. StreamsReceived is the sole input to the
            // "was there audio?" branch that decides whether a completion has to clean the turn up
            // itself, so crediting it to the wrong turn left a real turn uncleaned.
            if (string.IsNullOrEmpty(requestId))
            {
                Debug.LogWarning("[RequestOrchestrator] Audio started for an unnamed turn — cannot credit any " +
                                 "turn with proof of backend life or with having produced audio.");
                return;
            }

            if (!_liveSessions.TryGetValue(requestId, out var session))
            {
                if (enableVerboseLogging)
                    Debug.Log($"[RequestOrchestrator] Audio started for {requestId}, which is no longer a live turn.");
                return;
            }

            session.FirstSignalSeen = true;

            if (session.StreamsReceived == 0)
            {
                session.StreamsReceived = 1;
                if (enableVerboseLogging)
                    Debug.Log($"[RequestOrchestrator] Audio stream marked as received for session {requestId}");
            }
        }

        /// <summary>
        /// Get the current session RequestId (null if no active session)
        /// </summary>
        public string GetMicrophoneSessionId()
        {
            return _micSession?.RequestId;
        }

        /// <summary>
        /// Completes <paramref name="requestId"/> (used when no audio was received, so nothing else will
        /// clean the turn up). Addressed by id: a completion for a turn that is no longer live must not
        /// release a different one, and only the microphone's own turn clears the microphone pointer.
        /// </summary>
        public void CompleteSession(string requestId)
        {
            if (string.IsNullOrEmpty(requestId) || !_liveSessions.ContainsKey(requestId))
            {
                if (enableVerboseLogging)
                    Debug.Log($"[RequestOrchestrator] Not completing '{requestId ?? "(none)"}' — no such live turn.");
                return;
            }

            if (enableVerboseLogging)
                Debug.Log($"[RequestOrchestrator] Session {requestId} completed");

            var wasMicTurn = _micSession != null && _micSession.RequestId == requestId;
            ReleaseLiveSession(requestId);

            // This is the other completion path — a turn that produced no audio, so the client closes it
            // here instead. Either way the backend is finished with it.
            MarkTurnFinishedByBackend(requestId);

            if (wasMicTurn)
                _micSession = null;
        }

        #region Turn Watchdog

        /// <summary>
        /// Outcome of one turn-watchdog evaluation tick. See <see cref="EvaluateTurnWatchdog"/>.
        /// </summary>
        internal enum TurnWatchdogVerdict
        {
            KeepWaiting,
            KeepWaitingPaused,
            StopWatching,
            FailTurn
        }

        /// <summary>
        /// Decision logic for the per-turn first-signal watchdog, kept pure so the timing edges
        /// are unit-testable without PlayMode.
        ///
        /// Phase-1-only by design: the watchdog only covers the window between the turn becoming able
        /// to produce a signal and the FIRST one arriving (transcript, audio playback start, completion
        /// — completion releases the turn, which lands in the "not live" branch). Once any signal proves
        /// the chain is alive it stops for good, so it can never cut off a long Full-mode monologue
        /// mid-stream. Paused time does not consume budget: PauseManager pauses backend streaming too,
        /// so silence while paused is legitimate (2026-06-12 audit, client H8).
        ///
        /// Both turn-specific inputs are now values read off THAT turn, not ids compared against shared
        /// slots. The old signature took "the current request id" and "the id a signal was seen for",
        /// which only described one turn at a time: liveness came from the microphone's pointer, so an
        /// NPC-initiated turn was never live by that test and stopped being watched on its first tick.
        /// </summary>
        internal static TurnWatchdogVerdict EvaluateTurnWatchdog(
            bool isTurnStillLive,
            bool firstSignalSeen,
            bool isPaused,
            float elapsedSinceArmedSeconds,
            float timeoutSeconds)
        {
            if (timeoutSeconds <= 0f)
                return TurnWatchdogVerdict.StopWatching; // feature disabled via Inspector

            if (!isTurnStillLive)
                return TurnWatchdogVerdict.StopWatching; // turn completed, cancelled, failed or displaced

            if (firstSignalSeen)
                return TurnWatchdogVerdict.StopWatching; // backend proved alive — phase 1 over

            if (isPaused)
                return TurnWatchdogVerdict.KeepWaitingPaused;

            return elapsedSinceArmedSeconds >= timeoutSeconds
                ? TurnWatchdogVerdict.FailTurn
                : TurnWatchdogVerdict.KeepWaiting;
        }

        /// <summary>
        /// Reads the watchdog's inputs for one turn out of live state and evaluates them. Separate from
        /// the coroutine so a test can pin what the coroutine actually asks about: an NPC-initiated turn
        /// used to be judged by the MICROPHONE's pointer and was therefore abandoned immediately.
        /// </summary>
        internal TurnWatchdogVerdict EvaluateTurnWatchdogFor(string requestId, float elapsedSinceArmedSeconds)
        {
            _liveSessions.TryGetValue(requestId ?? string.Empty, out var session);

            return EvaluateTurnWatchdog(
                session != null,
                session != null && session.FirstSignalSeen,
                _isPauseActive,
                elapsedSinceArmedSeconds,
                turnFirstSignalTimeoutSeconds);
        }

        /// <summary>
        /// Watches one turn for its first backend response signal and fails it when none arrives.
        /// Without this, a half-open connection (WiFi drop without RST — no app-level keepalive exists
        /// in either direction), a server hang, or a backend error path that skips conversationComplete
        /// left the turn armed forever: the NPC stayed silent until the TCP layer happened to notice or
        /// the player switched NPCs.
        ///
        /// Armed when the turn could first produce a signal, which is NOT when its request was sent:
        /// * player turn — at EndOfSpeech. The backend cannot transcribe speech that is still being
        ///   spoken, so a push-to-talk hold longer than the timeout used to fail a perfectly healthy
        ///   turn from inside the watchdog.
        /// * NPC-initiated turn — right after the TextInput send, which is also its first opportunity.
        ///   This one is unchanged, and also covers a silently failed send.
        /// </summary>
        private IEnumerator TurnFirstSignalWatchdog(string requestId)
        {
            const float tickSeconds = 1f;
            var elapsed = 0f;

            while (true)
            {
                // Realtime: an unresponsive backend should be detected even at timeScale 0; the
                // explicit pause branch (not timeScale) decides whether the budget is consumed.
                yield return new WaitForSecondsRealtime(tickSeconds);

                var verdict = EvaluateTurnWatchdogFor(requestId, elapsed);

                switch (verdict)
                {
                    case TurnWatchdogVerdict.StopWatching:
                        yield break;
                    case TurnWatchdogVerdict.FailTurn:
                        FailUnresponsiveTurn(requestId);
                        yield break;
                    case TurnWatchdogVerdict.KeepWaiting:
                        elapsed += tickSeconds;
                        break;
                    case TurnWatchdogVerdict.KeepWaitingPaused:
                        break; // freeze the clock while paused
                }
            }
        }

        /// <summary>
        /// Fails a turn whose backend never responded, via the same recovery contract as
        /// <see cref="HandleWebSocketDisconnected"/>: RaiseSttFailed lets the RuleSystem reset
        /// IsReactionBusy, and the session slot is released so the player can simply try again.
        /// </summary>
        private void FailUnresponsiveTurn(string requestId)
        {
            Debug.LogWarning(
                $"[RequestOrchestrator] No backend response for turn {requestId} within " +
                $"{turnFirstSignalTimeoutSeconds}s — failing the turn (half-open connection, server " +
                "hang, or error path that skipped conversationComplete). The next PTT starts clean.");

            var isMicTurn = _micSession != null && _micSession.RequestId == requestId;

            if (isMicTurn && _isRequestActive)
            {
                _isRequestActive = false;
                RaiseSttFailed(new AIBridge.Messages.NoTranscriptMessage
                {
                    RequestId = requestId,
                    Reason = "TurnResponseTimeout",
                    AudioDuration = 0,
                    SttProvider = "none"
                });
            }

            // No answer is coming for a turn the backend never responded to, so stop routing it too.
            ReleaseLiveSession(requestId);
            ForceReleaseTurnRouting(requestId);

            // An NPC turn's timeout must not disarm the microphone or tell the RuleSystem the player
            // said nothing — the player may be mid-sentence into a turn of their own.
            if (isMicTurn)
            {
                _micSession = null;
                _isProcessingRequest = false;
            }
        }

        #endregion

        /// <summary>
        /// Raise the OnTranscriptionReceived event
        /// Called by NpcClientBase when transcription is received from WebSocket
        /// </summary>
        public void RaiseTranscriptionReceived(string transcript, string requestId)
        {
            // A transcript is proof of backend life for THE TURN IT BELONGS TO — it ends that turn's
            // first-signal window and no other. This used to credit _micSession, so a transcript for
            // turn A silenced turn B's watchdog whenever B had become the current session, and B's dead
            // backend went unnoticed for the rest of the lesson.
            if (string.IsNullOrEmpty(requestId))
            {
                Debug.LogWarning("[RequestOrchestrator] Transcript arrived without a RequestId — cannot " +
                                 "credit any turn with proof of backend life. Not stamping the watchdog.");
            }
            else if (_liveSessions.TryGetValue(requestId, out var transcribedTurn))
            {
                transcribedTurn.FirstSignalSeen = true;
            }
            else if (enableVerboseLogging)
            {
                Debug.Log($"[RequestOrchestrator] Transcript for {requestId}, which is no longer a live turn.");
            }

            OnTranscriptionReceived?.Invoke(transcript, requestId);
        }

        /// <summary>
        /// Raise the OnSttFailed event
        /// Called by NpcClientBase when STT fails (timeout, error, etc.)
        /// </summary>
        public void RaiseSttFailed(AIBridge.Messages.NoTranscriptMessage message)
        {
            OnSttFailed?.Invoke(message);
        }

        #endregion

        #region Audio Processing

        /// <summary>
        /// Process encoded audio chunk from SpeechInputHandler.
        /// This is called by AudioStreamProcessor.OnOpusAudioEncoded event.
        /// Audio is either buffered (if RuleSystem hasn't approved yet) or sent directly to WebSocket.
        /// </summary>
        private void ProcessAudioChunk(byte[] encodedAudio)
        {
            if (encodedAudio == null || encodedAudio.Length == 0)
            {
                Debug.LogWarning("[RequestOrchestrator] ProcessAudioChunk received null or empty data!");
                return;
            }

            // Simple check: Is there an active request accepting audio?
            // This covers both buffering phase (before session) and active session phase
            if (!_isRequestActive)
            {
                Debug.LogWarning($"[RequestOrchestrator] Received {encodedAudio.Length} bytes but no active request! Audio dropped.");
                return;
            }

            // Audio is sent directly to WebSocket
            // Buffering is handled by AudioStreamProcessor itself (StartBuffering/FlushBuffer)
            bool isConnected = _webSocketClient != null && _webSocketClient.IsConnected;

            if (isConnected)
            {
                // CRITICAL: First flush any buffered audio from reconnection
                FlushReconnectionBuffer();

                // Then send current chunk
                _ = _webSocketClient.SendBinaryAsync(encodedAudio);

                _wasConnectedLastFrame = true;
            }
            else
            {
                // CRITICAL FIX: Buffer audio during disconnection instead of dropping it
                // This ensures no audio is lost during WebSocket reconnection

                if (_reconnectionAudioBuffer.Count < MAX_RECONNECTION_BUFFER_SIZE)
                {
                    // Buffer the audio chunk
                    _reconnectionAudioBuffer.Enqueue(encodedAudio);

                    if (enableVerboseLogging && _reconnectionAudioBuffer.Count % 50 == 1)
                        Debug.Log($"[RequestOrchestrator] Buffering audio during reconnection: {_reconnectionAudioBuffer.Count} chunks buffered");
                }
                else
                {
                    // Buffer full - drop oldest chunk to make room (FIFO)
                    _reconnectionAudioBuffer.Dequeue();
                    _reconnectionAudioBuffer.Enqueue(encodedAudio);
                    _reconnectionBufferOverflows++;

                    if (_reconnectionBufferOverflows % 50 == 1)
                        Debug.LogWarning($"[RequestOrchestrator] Reconnection buffer overflow - dropping oldest chunks ({_reconnectionBufferOverflows} overflows total)");
                }

                // THROTTLED WARNING: Only log once when connection drops, then every N seconds
                bool shouldLogWarning = false;

                if (_wasConnectedLastFrame)
                {
                    // Connection just dropped - always log this transition
                    shouldLogWarning = true;
                    _wasConnectedLastFrame = false;
                    _lastConnectionWarningTime = Time.time;
                }
                else if (Time.time - _lastConnectionWarningTime >= CONNECTION_WARNING_COOLDOWN)
                {
                    // Still disconnected after cooldown period - log update
                    shouldLogWarning = true;
                    _lastConnectionWarningTime = Time.time;
                }

                if (shouldLogWarning)
                {
                    Debug.LogWarning($"[RequestOrchestrator] WebSocket not connected - buffering audio for reconnection ({_reconnectionAudioBuffer.Count}/{MAX_RECONNECTION_BUFFER_SIZE} chunks). WebSocket may be reconnecting...");
                }
            }
        }

        /// <summary>
        /// Handle recording stopped event from SpeechInputHandler.
        /// Sends EndOfSpeech and EndOfAudio messages to backend to trigger transcription.
        ///
        /// ROBUSTNESS FEATURES:
        /// - Detects and waits for reconnection in progress (up to 3s)
        /// - Flushes buffered audio before sending end messages
        /// - Validates GameObject lifetime during async operations
        /// - Uses WebSocket state instead of heuristics for reconnection detection
        /// </summary>
        private async void HandleRecordingStopped()
        {
            if (enableVerboseLogging)
                Debug.Log("[RequestOrchestrator] Recording stopped - sending EndOfSpeech and EndOfAudio");

            // Only send messages if there's an active request
            if (!_isRequestActive || _micSession == null)
            {
                if (enableVerboseLogging)
                    Debug.LogWarning("[RequestOrchestrator] Recording stopped but no active request - messages not sent");
                return;
            }

            // CRITICAL: Handle reconnection scenario gracefully with robust state detection
            if (_webSocketClient == null || !_webSocketClient.IsConnected)
            {
                // Detect if reconnection is in progress using explicit state check
                // Note: Use State property directly to avoid namespace conflict between Core.ConnectionState and WebSocket.ConnectionState
                bool isReconnecting = _webSocketClient != null &&
                                     !_webSocketClient.IsConnected &&
                                     _webSocketClient.State.ToString() == "Connecting";

                // Also check if we have buffered audio (secondary indicator)
                bool hasBufferedAudio = _reconnectionAudioBuffer.Count > 0;

                if (isReconnecting || hasBufferedAudio)
                {
                    Debug.LogWarning($"[RequestOrchestrator] WebSocket disconnected during recording stop - " +
                                   $"reconnection in progress (State: {_webSocketClient?.State}, Buffered: {_reconnectionAudioBuffer.Count} chunks). " +
                                   $"Waiting for reconnect...");

                    // Wait up to 3 seconds for reconnection with proper lifetime checks
                    const int maxWaitMs = 3000;
                    const int checkIntervalMs = 100;
                    int elapsedMs = 0;

                    while (elapsedMs < maxWaitMs)
                    {
                        // ROBUSTNESS: Check if GameObject/Component still exists before continuing
                        if (this == null || !gameObject || !gameObject.activeInHierarchy)
                        {
                            Debug.LogWarning("[RequestOrchestrator] Component destroyed/deactivated during reconnect wait - aborting");
                            return;
                        }

                        // Check if connection restored
                        if (_webSocketClient != null && _webSocketClient.IsConnected)
                        {
                            Debug.Log($"[RequestOrchestrator] Reconnection successful after {elapsedMs}ms");
                            break;
                        }

                        await System.Threading.Tasks.Task.Delay(checkIntervalMs);
                        elapsedMs += checkIntervalMs;
                    }

                    // ROBUSTNESS: Validate component still exists after async wait
                    if (this == null || !gameObject || !gameObject.activeInHierarchy)
                    {
                        Debug.LogWarning("[RequestOrchestrator] Component destroyed/deactivated after reconnect wait - aborting");
                        return;
                    }

                    // Check final connection state
                    if (_webSocketClient != null && _webSocketClient.IsConnected)
                    {
                        Debug.Log($"[RequestOrchestrator] Connection restored - flushing {_reconnectionAudioBuffer.Count} buffered chunks before sending end messages");

                        // CRITICAL: Ensure buffered audio is flushed before sending end messages
                        FlushReconnectionBuffer();

                        // Continue to send end messages below
                    }
                    else
                    {
                        Debug.LogWarning($"[RequestOrchestrator] Reconnection timeout after {elapsedMs}ms - cannot send end messages. Request may be incomplete.");

                        // Log summary of what was buffered
                        if (_reconnectionAudioBuffer.Count > 0)
                        {
                            Debug.LogWarning($"[RequestOrchestrator] {_reconnectionAudioBuffer.Count} audio chunks were buffered but connection did not recover in time");
                        }

                        // Notify RuleSystem so IsReactionBusy resets (prevents permanent NPC freeze)
                        _isRequestActive = false;
                        RaiseSttFailed(new AIBridge.Messages.NoTranscriptMessage
                        {
                            RequestId = _micSession?.RequestId,
                            Reason = "ConnectionLost"
                        });
                        return;
                    }
                }
                else
                {
                    // The socket is down and no handshake is in flight. Recoverable: the next
                    // push-to-talk goes through EnsureConnectionAsync again (fresh JWT + rebuilt
                    // socket). Reaching this point means SessionStart never made it onto a live
                    // socket for this turn — the reconnection buffer can only fill after a
                    // successful SessionStart — so the recorded speech was never transmitted.
                    UserErrorLogger.LogRecoverableError(
                        "Connection lost. Please try again.",
                        "[RequestOrchestrator] Cannot send end messages - WebSocket not connected and no reconnection in progress");

                    // Notify RuleSystem so IsReactionBusy resets (prevents permanent NPC freeze)
                    AbortActiveTurn("no connection at push-to-talk release");

                    return;
                }
            }

            // Start latency measurement - from PTT release to audio playback start
            var tracker = GetLatencyTracker(_activeNpcClient);
            if (tracker != null)
            {
                tracker.StartMeasurement();
                if (enableVerboseLogging)
                    Debug.Log($"[RequestOrchestrator] StartMeasurement() called for {_activeNpcClient?.NpcName} at PTT release");
            }
            else
            {
                if (enableVerboseLogging)
                    Debug.LogWarning($"[RequestOrchestrator] Could not call StartMeasurement() - LatencyTracker is null");
            }

            try
            {
                // Send EndOfSpeech - indicates user stopped speaking
                await _webSocketClient.SendEndOfSpeechAsync(_micSession.RequestId);
                if (enableVerboseLogging)
                    Debug.Log($"[RequestOrchestrator] EndOfSpeech sent for session: {_micSession.RequestId}");

                // Send EndOfAudio - indicates all audio data has been transmitted
                await _webSocketClient.SendEndOfAudioAsync(_micSession.RequestId);
                if (enableVerboseLogging)
                    Debug.Log($"[RequestOrchestrator] EndOfAudio sent for session: {_micSession.RequestId}");

                // NOW the turn can produce a signal, so now its budget starts. Read the id out of the
                // session before starting the coroutine: an await may have let the turn be displaced,
                // and watching an id nobody holds any more just stops on the first tick.
                var speechEndedForRequestId = _micSession?.RequestId;
                if (!string.IsNullOrEmpty(speechEndedForRequestId))
                    StartCoroutine(TurnFirstSignalWatchdog(speechEndedForRequestId));
            }
            catch (Exception ex)
            {
                Debug.LogError($"[RequestOrchestrator] Failed to send end messages: {ex.Message}");
            }
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Subscribe to this NPC client's ConversationMetadataHandler so the orchestrator can clear
        /// per-turn state when the backend signals conversationComplete.
        ///
        /// Without a subscription, the session lingers after a successful turn — every subsequent
        /// recording-stopped event then sends EndOfSpeech for the stale RequestId, the backend answers
        /// "Session not found", and the NPC goes silent while animations keep running (incident
        /// 2026-05-11).
        ///
        /// Subscriptions are per NPC and are NOT removed when the player turns to someone else. Each
        /// NpcClient owns its own metadata handler, so unsubscribing the previous NPC on every turn
        /// start meant that with turns live on two NPCs, only the last one's completion was ever heard.
        /// They are removed when that NpcClient is destroyed, and all of them when this component is.
        ///
        /// Safe to call repeatedly: a client that is already subscribed is a no-op, so same-NPC retries
        /// do not accumulate duplicate subscriptions.
        /// </summary>
        private void RegisterConversationCompletionHandler(NpcClientBase npcClient)
        {
            if (npcClient == null)
                return;

            PruneDestroyedCompletionSubscriptions();

            if (_completionSubscriptions.ContainsKey(npcClient))
                return;

            var handler = npcClient.MetadataHandler;
            if (handler == null)
            {
                Debug.LogWarning($"[RequestOrchestrator] '{npcClient.NpcName}' has no metadata handler yet, so its " +
                                 "turn completions cannot be observed. Its session will only be released by the watchdog.");
                return;
            }

            handler.OnConversationComplete += HandleConversationCompleted;
            _completionSubscriptions[npcClient] = handler;

            if (enableVerboseLogging)
                Debug.Log($"[RequestOrchestrator] Subscribed to completions for '{npcClient.NpcName}' " +
                          $"({_completionSubscriptions.Count} NPC(s) subscribed).");
        }

        /// <summary>
        /// Drops subscriptions whose NpcClient has been destroyed — a dynamically spawned NPC that is
        /// gone would otherwise be pinned alive by this dictionary for the rest of the scene.
        /// </summary>
        private void PruneDestroyedCompletionSubscriptions()
        {
            List<NpcClientBase> destroyed = null;

            foreach (var subscription in _completionSubscriptions)
            {
                if (subscription.Key == null)
                    (destroyed ??= new List<NpcClientBase>()).Add(subscription.Key);
            }

            if (destroyed == null)
                return;

            foreach (var client in destroyed)
            {
                if (_completionSubscriptions.TryGetValue(client, out var handler) && handler != null)
                    handler.OnConversationComplete -= HandleConversationCompleted;

                _completionSubscriptions.Remove(client);
            }
        }

        /// <summary>
        /// Removes every completion subscription. Called when this component is destroyed, so no
        /// delegate of ours outlives it.
        /// </summary>
        private void UnregisterConversationCompletionHandler()
        {
            foreach (var subscription in _completionSubscriptions)
            {
                if (subscription.Value != null)
                    subscription.Value.OnConversationComplete -= HandleConversationCompleted;
            }

            _completionSubscriptions.Clear();
        }

        /// <summary>
        /// Per-turn cleanup hook fired by the backend's conversationComplete message.
        /// Clears _micSession, _isRequestActive and _isProcessingRequest so the next
        /// user-turn starts with a fresh RequestId. Releasing the turn also drops the
        /// NpcMessageRouter and WebSocketClient routing entries keyed on its RequestId.
        /// </summary>
        private void HandleConversationCompleted(string requestId, bool audioReceived)
        {
            // The completing turn now names itself instead of being inferred from shared state. Behaviour
            // is unchanged in this step: the caller still only raises this for the tracked turn, so the
            // clears below are the same clears. Removing that gate is a later step, and it needs this
            // parameter to exist first.
            var completedRequestId = requestId;
            var completedNpcName = _activeNpcClient?.NpcName;
            var wasMicTurn = _micSession != null && _micSession.RequestId == completedRequestId;

            ReleaseLiveSession(completedRequestId);

            // The backend is done with this turn. Routing still waits for the audio to finish playing.
            MarkTurnFinishedByBackend(completedRequestId);

            // Only the microphone's own turn may disarm the microphone. A character-speaks-first turn
            // completing while the player is recording used to clear all of this, and the push-to-talk
            // release then found no active request: no EndOfSpeech, no transcript, no sttFailed, and the
            // NPC stayed mute until an NPC switch (2026-06-12 audit, client critical C4).
            if (wasMicTurn)
            {
                _micSession = null;
                _isRequestActive = false;
                _isProcessingRequest = false;
            }

            if (enableVerboseLogging)
            {
                Debug.Log($"[RequestOrchestrator] Conversation completed (audioReceived={audioReceived}) — " +
                          $"session state cleared for {completedNpcName ?? "(no active NPC)"} " +
                          $"(RequestId: {completedRequestId ?? "(none)"})");
            }
        }

        /// <summary>
        /// Flush buffered audio chunks after WebSocket reconnection
        /// </summary>
        private void FlushReconnectionBuffer()
        {
            if (_reconnectionAudioBuffer.Count == 0)
                return;

            var bufferSize = _reconnectionAudioBuffer.Count;
            var overflows = _reconnectionBufferOverflows;

            Debug.Log($"[RequestOrchestrator] 🔄 Connection restored! Flushing {bufferSize} buffered audio chunks" +
                     (overflows > 0 ? $" (⚠️ {overflows} chunks were dropped due to buffer overflow)" : ""));

            // Send all buffered chunks
            var sentCount = 0;
            while (_reconnectionAudioBuffer.Count > 0)
            {
                var chunk = _reconnectionAudioBuffer.Dequeue();
                _ = _webSocketClient.SendBinaryAsync(chunk);
                sentCount++;
            }

            // Reset counters
            _reconnectionBufferOverflows = 0;

            Debug.Log($"[RequestOrchestrator] ✅ Successfully sent {sentCount} buffered chunks to backend");
        }

        private void ValidateRequiredComponents()
        {
            if (_webSocketClient == null)
                _webSocketClient = FindFirstObjectByType<WebSocketClient>();

            if (speechInputHandler == null)
                speechInputHandler = FindFirstObjectByType<SpeechInputHandler>();

            // InterruptionManager is optional
            // Will be null if not using interruption features

            // CRITICAL: Validate required components
            var missingComponents = new List<string>();

            if (_webSocketClient == null)
                missingComponents.Add("WebSocketClient");

            if (speechInputHandler == null)
                missingComponents.Add("SpeechInputHandler");

            if (missingComponents.Count > 0)
            {
                var errorMsg = $"❌❌❌ CRITICAL ERROR ❌❌❌\n\n" +
                              $"RequestOrchestrator is missing REQUIRED components:\n" +
                              $"  • {string.Join("\n  • ", missingComponents)}\n\n" +
                              $"➡️ AI Bridge will NOT work without these components!\n" +
                              $"➡️ Add missing GameObjects to the scene immediately!\n\n" +
                              $"GameObject: {gameObject.name}";

                Debug.LogError(errorMsg, this);

                // Also log individual errors for each missing component
                if (_webSocketClient == null)
                    Debug.LogError("❌ WebSocketClient not found! API communication will NOT work.", this);

                if (speechInputHandler == null)
                    Debug.LogError("❌ SpeechInputHandler not found! Audio requests will NOT work.", this);

                // CRITICAL FIX: Disable component to prevent silent failures in builds
                // This ensures the component doesn't run with missing dependencies
                enabled = false;
                Debug.LogError($"[RequestOrchestrator] Component DISABLED due to missing dependencies. Fix configuration!", this);

#if UNITY_EDITOR
                // Pause the editor to force attention
                Debug.Break();
#endif

                return; // Stop initialization
            }

            // Optional component - just info
            if (interruptionManager == null && enableVerboseLogging)
                Debug.Log("[RequestOrchestrator] InterruptionManager not set. Interruption detection disabled.");
        }

        private IEnumerator ProcessRequestQueues()
        {
            while (this && gameObject && gameObject.activeInHierarchy)
            {
                // Process text requests first (higher priority for NPC-initiated conversations)
                if (_textRequestQueue.Count > 0 && !_isProcessingRequest)
                {
                    var request = _textRequestQueue.Dequeue();
                    yield return ProcessTextRequest(request);
                }

                // Process audio requests
                if (_audioRequestQueue.Count > 0 && !_isProcessingRequest)
                {
                    var request = _audioRequestQueue.Dequeue();
                    yield return ProcessAudioRequest(request);
                }

                yield return null; // Wait one frame before checking again
            }
        }

        private IEnumerator ProcessAudioRequest(AudioRequest request)
        {
            _isProcessingRequest = true;

            try
            {
                if (enableVerboseLogging)
                    Debug.Log($"[RequestOrchestrator] Processing audio request for {request.NpcConfig.Name}");

                // CRITICAL: Recording is ALREADY started by SpeechInputHandler at PTT press
                // Audio is being encoded by AudioStreamProcessor
                // Buffering was already started in StartAudioRequest() to prevent early audio chunks
                // from being sent before SessionStarted confirmation

                // CRITICAL FIX: Session is already created in StartAudioRequest()
                // Verify it exists and matches the request ID
                if (!_liveSessions.ContainsKey(request.RequestId))
                {
                    // This turn stopped being live between being queued and being sent: it was cancelled
                    // by an NPC switch, displaced by another press, or already failed. It is obsolete, not
                    // broken — whichever turn replaced it owns the outcome now.
                    //
                    // Deliberately NOT a Debug.LogError (that ends the session in the host app) and
                    // deliberately NOT a RaiseSttFailed: for a displaced turn the player is still
                    // speaking, into the turn that displaced this one, so reporting "no speech" would cut
                    // a live utterance short.
                    Debug.LogWarning($"[RequestOrchestrator] Turn {request.RequestId} is no longer live — " +
                                     "cancelled, displaced or already failed before its SessionStart was sent. " +
                                     "Dropping this turn only.");
                    yield break;
                }

                if (enableVerboseLogging)
                    Debug.Log($"[RequestOrchestrator] Using existing session: {_micSession.RequestId}");

                // Create session parameters
                var parameters = BuildSessionParameters(request.NpcConfig);

                // Register this request with the NPC router so messages are routed correctly
                var npcName = request.NpcClient.NpcName;
                NpcMessageRouter.Instance.SetActiveRequest(request.RequestId, npcName);

                // CRITICAL: Register the NPC handler with WebSocketClient to receive responses
                if (request.NpcClient is INpcMessageHandler handler)
                {
                    _webSocketClient.RegisterNpc(request.RequestId, handler);
                    if (enableVerboseLogging)
                        Debug.Log($"[RequestOrchestrator] Registered NPC handler for RequestId: {request.RequestId}");
                }
                else
                {
                    Debug.LogError($"[RequestOrchestrator] Cannot register NPC handler for '{npcName}' — its client does not implement INpcMessageHandler. This turn will receive no responses.");
                }

                // Snapshotted when the turn was started, so a turn that began in the meantime cannot
                // have replaced it with another persona's history.
                var messages = request.Messages;

                // The vocal baseline is LESSON-scoped. Enforce that here, where the lesson is known, so it
                // holds for every entry path (menu start, next case, retry) without a caller having to
                // remember to reset. A lesson change drops the state; an empty lessonId (AI coach) does not.
                ProsodyBaselineStore.EnsureLessonScope(AIBridgeObservability.TryGetContext()?.LessonId);

                // Build SessionStartMessage with all parameters including custom vocabulary
                var sessionStartMessage = new SessionStartMessage
                {
                    RequestId = request.RequestId,
                    Messages = messages,
                    // Fills persona_id on the backend's telemetry; it was empty on every turn,
                    // so a coach turn could not be told from an NPC turn.
                    PersonaId = npcName,
                    // Core audio settings
                    AudioFormat = parameters.AudioFormat,
                    SampleRate = parameters.SampleRate,
                    OpusBitrate = parameters.Bitrate,
                    // TTS settings
                    TtsProvider = parameters.TtsProvider,
                    VoiceId = parameters.VoiceId,
                    TtsModel = parameters.Model,
                    TtsOutputFormat = parameters.AudioFormat,
                    TtsStreamingMode = parameters.TtsStreamingMode,
                    // Speech opt-out for this turn. False = STT -> LLM only (no TTS pipeline, no
                    // audio, no TTS cost) — a PromptComposer scoring turn. Absent request keeps
                    // the spoken answer every existing scenario expects.
                    EnableTts = request.Request.EnableTts,
                    // Optional "json_object" for a machine-readable reply. Null = free text.
                    ResponseFormat = request.Request.ResponseFormat,
                    // ElevenLabs voice settings (from ConversationRequest, set by RuleSystem)
                    VoiceStability = request.Request.TtsStability,
                    VoiceSimilarityBoost = request.Request.TtsSimilarityBoost,
                    VoiceStyle = request.Request.TtsStyle,
                    VoiceUseSpeakerBoost = request.Request.TtsSpeakerBoost,
                    VoiceSpeed = request.Request.TtsSpeed,
                    TtsLanguageCode = request.Request.TtsLanguageCode, // Force TTS language (e.g., "nl" to prevent Flemish)
                    // Cartesia base emotion (ignored by ElevenLabs/Voxtral backend-side)
                    BaseEmotion = request.Request.BaseEmotion,
                    // LLM settings
                    LlmProvider = parameters.LlmProvider,
                    LlmModel = parameters.LlmModel,
                    Temperature = parameters.Temperature,
                    MaxTokens = parameters.MaxTokens,
                    // Gemini 2.5+ reasoning-token budget from the AI API Template.
                    // Null when the template doesn't opt into thinking control, in which
                    // case the field is omitted from the SessionStart wire payload and
                    // the backend keeps its provider default (existing pre-thinking
                    // behaviour). Sessions inherit this once at SessionStart; subsequent
                    // dialogue turns within the session reuse the same budget.
                    ThinkingBudget = request.Request.ThinkingBudget,
                    // Gemini 3.x reasoning-depth selector (minimal|low|medium|high). Mutually
                    // exclusive with ThinkingBudget; same per-session inheritance as the budget.
                    ThinkingLevel = request.Request.ThinkingLevel,
                    // Player vocal-delivery (prosody) analysis from the AI API Template. Null/empty
                    // provider = off (omitted from the wire → backend runs no analysis). Audio path only;
                    // EnableProsodyAnalysis is derived so the backend's per-session gate matches.
                    ProsodyProvider = request.Request.ProsodyProvider,
                    EnableProsodyAnalysis = !string.IsNullOrEmpty(request.Request.ProsodyProvider),
                    // Player-owned vocal baseline (measurement v2), sent each turn; the server's updated copy
                    // comes back via NpcClient. Read from the lesson-scoped store, not from a scene
                    // component: scene loads (every attempt) destroy the handler, which used to restart the
                    // baseline before it was statistically usable.
                    ProsodyBaseline = ProsodyBaselineStore.State,
                    // Optional per-template dialogue-LLM fallback target. Null when the template
                    // configures none → omitted from the wire payload → backend wraps nothing.
                    LlmFallback = request.Request.LlmFallback,
                    // STT settings
                    SttProvider = parameters.SttProvider,
                    LanguageCode = parameters.Language,  // Note: field is called LanguageCode, not Language
                    // Get custom vocabulary from SpeechInputHandler
                    CustomVocabulary = speechInputHandler?.ParsedCustomVocabulary,
                    CustomVocabularyBoost = 10.0f, // Fixed boost value for Google STT
                    // Enable metrics if configured
                    EnableMetrics = enableMetrics,
                    // Context caching (Gemini cost optimization)
                    ContextCacheName = request.Request.ContextCacheName,
                    // Anonymous observability correlation IDs (null when host project
                    // hasn't registered a provider or no IDs are available yet).
                    // Never contains UserId — GDPR gate enforced at model level.
                    Observability = AIBridgeObservability.TryGetContext()
                };

                // CRITICAL FIX: Subscribe to SessionStarted BEFORE sending SessionStart
                // This prevents race condition where SessionStarted arrives before we subscribe
                var sessionStartedReceived = false;
                Action sessionStartedHandler = () => { sessionStartedReceived = true; };

                if (request.NpcClient != null)
                {
                    request.NpcClient.OnSessionStarted += sessionStartedHandler;
                    if (enableVerboseLogging)
                        Debug.Log("[RequestOrchestrator] Subscribed to SessionStarted event - ready to receive confirmation");
                }

                // Start the conversation via WebSocket
                var sendTask = _webSocketClient.SendSessionStartAsync(sessionStartMessage);
                yield return new WaitUntil(() => sendTask.IsCompleted);

                if (sendTask.IsFaulted)
                {
                    // Recoverable: SendSessionStartAsync already tried EnsureConnectionAsync (fresh
                    // JWT + rebuilt socket) and could not reach the backend for THIS turn. The next
                    // push-to-talk tries again, so this must not raise the fatal restart popup.
                    UserErrorLogger.LogRecoverableError(
                        "Connection lost. Please try again.",
                        $"[RequestOrchestrator] Failed to send SessionStart: {sendTask.Exception?.GetBaseException().Message}");

                    // Unsubscribe on error
                    if (request.NpcClient != null)
                    {
                        request.NpcClient.OnSessionStarted -= sessionStartedHandler;
                    }

                    // CRITICAL: release the turn here. Leaving it armed is what made this surface
                    // minutes later at push-to-talk release instead of now.
                    AbortActiveTurn("SessionStart could not be sent");
                    yield break;
                }

                if (enableVerboseLogging)
                    Debug.Log($"[RequestOrchestrator] SessionStart message sent successfully");

                // Deliberately NOT arming the watchdog here. The backend's first possible signal for a
                // player turn is a transcript, and it cannot produce one while the player is still
                // speaking — so a push-to-talk hold longer than turnFirstSignalTimeoutSeconds failed a
                // healthy turn. HandleRecordingStopped arms it once EndOfSpeech is away.

                // Wait for SessionStarted confirmation from backend before flushing
                if (request.NpcClient != null)
                {
                    if (enableVerboseLogging)
                        Debug.Log("[RequestOrchestrator] Waiting for SessionStarted confirmation from backend...");

                    // Wait for SessionStarted confirmation (max 5 seconds)
                    var timeout = 5.0f;
                    var elapsed = 0f;
                    while (!sessionStartedReceived && elapsed < timeout)
                    {
                        yield return null;
                        elapsed += Time.deltaTime;
                    }

                    request.NpcClient.OnSessionStarted -= sessionStartedHandler;

                    if (sessionStartedReceived)
                    {
                        if (enableVerboseLogging)
                            Debug.Log($"[RequestOrchestrator] SessionStarted confirmed after {elapsed:F3}s - now flushing audio buffer");
                    }
                    else
                    {
                        Debug.LogWarning($"[RequestOrchestrator] SessionStarted confirmation timeout after {timeout}s - flushing anyway (may cause STT issues)");
                    }
                }
                else
                {
                    Debug.LogWarning("[RequestOrchestrator] Cannot wait for SessionStarted - no active NPC client, flushing immediately (may cause STT issues)");
                }

                // Now flush buffered audio to WebSocket (after backend is ready)
                if (speechInputHandler?.AudioStreamProcessor != null)
                {
                    speechInputHandler.AudioStreamProcessor.FlushBuffer();
                    if (enableVerboseLogging)
                        Debug.Log("[RequestOrchestrator] Backend confirmed ready - flushed buffered audio to WebSocket");
                }

                // Session might have been completed already (race condition with conversationComplete)
                if (_micSession != null)
                {
                    if (enableVerboseLogging)
                        Debug.Log($"[RequestOrchestrator] Audio request started. Session: {_micSession.RequestId}");
                }
                else
                {
                    if (enableVerboseLogging)
                        Debug.LogWarning("[RequestOrchestrator] Audio request started but session was already completed (possible race condition)");
                }
            }
            finally
            {
                _isProcessingRequest = false;
            }
        }

        private IEnumerator ProcessTextRequest(TextRequest request)
        {
            _isProcessingRequest = true;

            try
            {
                if (enableVerboseLogging)
                    Debug.Log($"[RequestOrchestrator] Processing text request: {request.Text}");

                // Validate NPC client is available
                if (request.NpcClient == null)
                {
                    Debug.LogError($"[RequestOrchestrator] Cannot process the text request for '{request.NpcConfig?.Id}' — it carries no NPC client. This should have been caught in StartTextRequest.");
                    _isProcessingRequest = false;
                    yield break;
                }

                // Start WebSocket session with text input
                var npcName = request.NpcClient.NpcName;
                // NOT the microphone's turn: the player is not talking into a character-speaks-first
                // turn, so this must not touch _micSession. Before the split it did, and a spontaneous
                // NPC line therefore stole the pointer the push-to-talk release depends on.
                //
                // A refusal means this NPC is already mid-turn and physically cannot play a second one.
                // Bail out BEFORE the router entry and the NPC handler below, so nothing is registered
                // for a turn that will never be sent.
                if (!RegisterLiveSession(new ConversationSession(npcName, request.RequestId,
                        request.NpcConfig?.Id, request.Request.OnPlayerTurnsAway)))
                {
                    _isProcessingRequest = false;
                    yield break;
                }

                // The audio path has always done this; the text path never did, so an NPC-initiated turn
                // was not resolvable by the router at all — which also broke NpcAudioPlayer's
                // pause/resume-stream lookup for those turns.
                NpcMessageRouter.Instance.SetActiveRequest(request.RequestId, npcName);

                // CRITICAL: Register the NPC handler with WebSocketClient to receive responses
                if (request.NpcClient is INpcMessageHandler textHandler)
                {
                    _webSocketClient.RegisterNpc(request.RequestId, textHandler);
                    if (enableVerboseLogging)
                        Debug.Log($"[RequestOrchestrator] Registered NPC handler for text request: {request.RequestId}");
                }
                else
                {
                    Debug.LogError($"[RequestOrchestrator] Cannot register NPC handler for the text request for '{npcName}' — its client does not implement INpcMessageHandler. This turn will receive no responses.");
                }

                // Snapshotted when the turn was started, so a turn that began in the meantime cannot
                // have replaced it with another persona's history.
                var messages = request.Messages;

                // Build TextInputMessage for text-based conversation
                var textInputMessage = new TextInputMessage
                {
                    RequestId = request.RequestId,
                    Text = request.Text,
                    IsNpcInitiated = string.IsNullOrEmpty(request.Text), // Empty text = NPC-initiated
                    Context = new ConversationContext
                    {
                        messages = messages,
                        // Same field as on SessionStart; NPC-initiated turns arrive here.
                        personaId = npcName,
                        systemPrompt = request.NpcConfig?.SystemPrompt,
                        voiceId = request.NpcConfig?.VoiceId,
                        ttsStreamingMode = request.NpcConfig?.TtsStreamingMode,
                        llmModel = request.NpcConfig?.LlmModel,
                        llmProvider = request.NpcConfig?.LlmProvider,
                        language = request.NpcConfig?.Language,
                        temperature = request.NpcConfig?.Temperature ?? 0,
                        maxTokens = request.NpcConfig?.MaxTokens ?? 0,
                        // Optional Gemini 2.5+ reasoning budget — carried from the
                        // AI API Template via ConversationRequest. Null = backend uses
                        // provider default (existing pre-thinking behaviour).
                        thinkingBudget = request.Request.ThinkingBudget,
                        // Gemini 3.x reasoning-depth selector; mutually exclusive with thinkingBudget.
                        thinkingLevel = request.Request.ThinkingLevel,
                        ttsModel = request.NpcConfig?.TtsModel,
                        sttProvider = request.NpcConfig?.SttProvider,
                        ttsProvider = request.NpcConfig?.TtsProvider,
                        // ElevenLabs voice settings (from ConversationRequest, set by RuleSystem)
                        voiceStability = request.Request.TtsStability,
                        voiceSimilarityBoost = request.Request.TtsSimilarityBoost,
                        voiceStyle = request.Request.TtsStyle,
                        voiceUseSpeakerBoost = request.Request.TtsSpeakerBoost,
                        voiceSpeed = request.Request.TtsSpeed,
                        ttsLanguageCode = request.Request.TtsLanguageCode, // Force TTS language
                        // Cartesia base emotion (ignored by ElevenLabs/Voxtral backend-side)
                        baseEmotion = request.Request.BaseEmotion,
                        // Context caching (Gemini cost optimization)
                        contextCacheName = request.Request.ContextCacheName,
                        // Anonymous observability correlation IDs; null when host project
                        // hasn't registered a provider yet or no IDs are available.
                        observability = AIBridgeObservability.TryGetContext(),
                    }
                };

                // Send text input via WebSocket
                yield return _webSocketClient.SendTextInputAsync(textInputMessage);

                // The request is now in flight: watch for the backend's first sign of life. This
                // also covers a silently failed send — the turn then fails after the timeout
                // instead of staying half-armed forever.
                StartCoroutine(TurnFirstSignalWatchdog(request.RequestId));

                // This turn's own id, not the microphone session's: the text path deliberately
                // leaves _micSession alone (see RegisterLiveSession above), so reading it here threw
                // on every NPC-initiated turn that had no microphone turn in flight beside it.
                if (enableVerboseLogging)
                    Debug.Log($"[RequestOrchestrator] Text request started. Request: {request.RequestId}");

                // Start latency measurement for NPC-initiated conversations
                // For player-initiated: StartMeasurement is called on PTT release
                // For NPC-initiated: StartMeasurement is called after request is sent
                var tracker = GetLatencyTracker(_activeNpcClient);
                if (tracker != null)
                {
                    tracker.StartMeasurement();
                    if (enableVerboseLogging)
                        Debug.Log($"[RequestOrchestrator] StartMeasurement() called for NPC-initiated conversation: {_activeNpcClient?.NpcName}");
                }
                else
                {
                    if (enableVerboseLogging)
                        Debug.LogWarning($"[RequestOrchestrator] Could not call StartMeasurement() - LatencyTracker is null for {_activeNpcClient?.NpcName}");
                }
            }
            finally
            {
                _isProcessingRequest = false;
            }

            yield return null;
        }
        
        private ConnectionParameters BuildSessionParameters(INpcConfiguration npcConfig)
        {
            // Build connection parameters from NPC configuration
            var parameters = new ConnectionParameters
            {
                RequestId = Guid.NewGuid().ToString(),
                VoiceId = npcConfig.VoiceId,
                Model = npcConfig.TtsModel,
                Language = npcConfig.Language,
                SttProvider = npcConfig.SttProvider,  // STT provider from NPC configuration
                TtsProvider = npcConfig.TtsProvider,  // TTS provider from NPC configuration
                LlmProvider = npcConfig.LlmProvider,  // LLM provider from NPC configuration
                LlmModel = npcConfig.LlmModel,        // LLM model from NPC configuration
                Temperature = npcConfig.Temperature,   // Temperature from NPC configuration
                MaxTokens = npcConfig.MaxTokens,
                TtsStreamingMode = npcConfig.TtsStreamingMode,
                // Audio format settings for UPSTREAM (Microphone → Backend)
                // IMPORTANT: These parameters are used for SessionStart message which configures INPUT audio
                // DOWNSTREAM (TTS output) uses 48kHz PCM, configured separately in backend
                AudioFormat = "opus",
                SampleRate = MicrophoneCapture.Frequency,  // UPSTREAM: 16kHz for STT
                Bitrate = MicrophoneCapture.UPSTREAM_OPUS_BITRATE,  // UPSTREAM: 64kbps (16kHz is the SAMPLE rate, above)
                ChannelCount = 1,
                // Enable metrics if configured
                EnableMetrics = enableMetrics
            };

            return parameters;
        }

        /// <summary>
        /// Get chat history for the current conversation.
        /// RuleSystem path: Messages already includes system prompt as first message
        /// SimpleNpcClient path: Convert SystemPrompt to Messages[0] with role="system"
        /// </summary>
        private List<ChatMessage> GetChatHistory(INpcConfiguration npcConfig, NpcClientBase npcClient)
        {
            if (npcConfig == null)
            {
                if (enableVerboseLogging)
                    Debug.LogWarning("[RequestOrchestrator] No NPC config - returning empty messages");
                return new List<ChatMessage>();
            }

            // Priority 1: Use Messages if provided (RuleSystem path - already complete with system prompt)
            if (npcConfig.Messages != null && npcConfig.Messages.Count > 0)
            {
                if (enableVerboseLogging)
                {
                    Debug.Log($"[RequestOrchestrator] Using {npcConfig.Messages.Count} messages from config (includes system prompt)");

                    // Log each message with role and content preview
                    for (int i = 0; i < npcConfig.Messages.Count; i++)
                    {
                        var msg = npcConfig.Messages[i];
                        var contentPreview = msg.Content?.Length > 500
                            ? msg.Content.Substring(0, 500) + "... [TRUNCATED]"
                            : msg.Content;
                        Debug.Log($"[RequestOrchestrator] Message[{i}] role={msg.Role}:\n{contentPreview}");
                    }
                }
                return new List<ChatMessage>(npcConfig.Messages);
            }

            // Priority 2: Build Messages from SystemPrompt + NPC client history (SimpleNpcClient path)
            var messages = new List<ChatMessage>();

            // Add system prompt as first message if available
            if (!string.IsNullOrEmpty(npcConfig.SystemPrompt))
            {
                messages.Add(new ChatMessage
                {
                    Role = "system",
                    Content = npcConfig.SystemPrompt
                });
            }

            // Add chat history from NPC client if available
            if (npcClient != null && npcClient is IConversationHistory historyProvider)
            {
                var history = historyProvider.GetApiHistoryAsChatMessages();
                if (history != null && history.Count > 0)
                {
                    messages.AddRange(history);
                }
            }

            if (enableVerboseLogging)
            {
                if (messages.Count > 0)
                {
                    Debug.Log($"[RequestOrchestrator] Built {messages.Count} messages from SystemPrompt + history");

                    // Log each message with role and content preview
                    for (int i = 0; i < messages.Count; i++)
                    {
                        var msg = messages[i];
                        var contentPreview = msg.Content?.Length > 500
                            ? msg.Content.Substring(0, 500) + "... [TRUNCATED]"
                            : msg.Content;
                        Debug.Log($"[RequestOrchestrator] Message[{i}] role={msg.Role}:\n{contentPreview}");
                    }
                }
                else
                {
                    Debug.Log("[RequestOrchestrator] No messages available - using empty array");
                }
            }

            return messages;
        }

        /// <summary>
        /// Helper method to get LatencyTracker from NpcClientBase via reflection.
        /// LatencyTracker is internal in ConversationMetadataHandler, so we need reflection to access it.
        /// </summary>
        private LatencyTracker GetLatencyTracker(NpcClientBase npcClient)
        {
            if (npcClient == null)
            {
                if (enableVerboseLogging)
                    Debug.LogWarning("[RequestOrchestrator] GetLatencyTracker: npcClient is NULL");
                return null;
            }

            if (npcClient.MetadataHandler == null)
            {
                if (enableVerboseLogging)
                    Debug.LogWarning($"[RequestOrchestrator] GetLatencyTracker: MetadataHandler is NULL for NPC: {npcClient.NpcName}");
                return null;
            }

            var latencyTracker = typeof(ConversationMetadataHandler)
                .GetField("_latencyTracker", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.GetValue(npcClient.MetadataHandler) as LatencyTracker;

            if (latencyTracker == null && enableVerboseLogging)
            {
                Debug.LogWarning($"[RequestOrchestrator] GetLatencyTracker: Failed to get LatencyTracker via reflection for NPC: {npcClient.NpcName}");
            }

            return latencyTracker;
        }

        #endregion

        #region Internal Classes

        // Everything a queued turn needs to go on the wire, captured when the turn was STARTED.
        // Reading any of this off a shared field at process time is what let one persona's system
        // prompt, chat history, voice settings and Gemini context-cache name end up in another
        // persona's turn: the queue releases as soon as a request is sent, so a second turn can
        // overwrite those fields while the first is still being assembled.
        private class AudioRequest
        {
            public INpcConfiguration NpcConfig;
            public string RequestId;
            public ConversationRequest Request;
            public NpcClientBase NpcClient;
            public List<ChatMessage> Messages;
        }

        private class TextRequest
        {
            public INpcConfiguration NpcConfig;
            public string Text;
            public string RequestId;
            public ConversationRequest Request;
            public NpcClientBase NpcClient;
            public List<ChatMessage> Messages;
        }


        #endregion
    }
}