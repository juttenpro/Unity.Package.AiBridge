using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Tsc.AIBridge.Audio.Capture;
using UnityEngine;
using Tsc.AIBridge.Messages;
using Tsc.AIBridge.WebSocket;
using Tsc.AIBridge.Audio.Interruption;
using Tsc.AIBridge.Input;

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
                            Debug.LogError("[RequestOrchestrator] No instance found in scene!");
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

        #endregion

        #region Events

        /// <summary>
        /// Fired when STT transcription is received
        /// </summary>
        public event Action<string> OnTranscriptionReceived;

        #endregion

        #region Private Fields

        private readonly Queue<AudioRequest> _audioRequestQueue = new();
        private readonly Queue<TextRequest> _textRequestQueue = new();

        private WebSocketClient _webSocketClient;
        private ConversationSession _currentSession;
        private INpcConfiguration _activeNpcConfig;
        private NpcClientBase _activeNpcClient; // Cache to avoid FindObjectsByType
        private bool _isProcessingRequest; // Queue management - prevents concurrent request STARTS
        private bool _isRequestActive; // Request lifecycle - true from StartAudioRequest until EndAudioRequest/Cancel
        private Coroutine _processQueueCoroutine;

        /// <summary>
        /// Event fired when the active NPC changes.
        /// Used by InterruptionManager to track which NPC is currently active without reflection.
        /// Parameters: (activeNpcClient, activeNpcConfig)
        /// </summary>
        public event Action<NpcClientBase, INpcConfiguration> OnActiveNpcChanged;

        // For tracking whether we're waiting for audio to finish
        private bool _isWaitingForAudioStart;

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
            Debug.Log("[RequestOrchestrator] Subscribed to AudioStreamProcessor.OnOpusAudioEncoded");

            // Subscribe to recording stopped event to send EndOfSpeech
            speechInputHandler.OnRecordingStopped += HandleRecordingStopped;
            Debug.Log("[RequestOrchestrator] Subscribed to SpeechInputHandler.OnRecordingStopped");

            _processQueueCoroutine = StartCoroutine(ProcessRequestQueues());
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

        #endregion

        #region Public API

        /// <summary>
        /// Set the NPC provider at runtime (for RuleSystem integration)
        /// </summary>
        public void SetNpcProvider(INpcProvider provider)
        {
            _npcProvider = provider;
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

            Debug.Log($"[RequestOrchestrator] Starting conversation request - NPC: {request.NpcId}, " +
                     $"Type: {(request.IsNpcInitiated ? "NPC-initiated" : "Player-initiated")}, " +
                     $"STT: {request.SttProvider}, LLM: {request.LlmModel}");

            // DEBUG: Log message count in request
            Debug.Log($"[RequestOrchestrator] ConversationRequest has {request.Messages?.Count ?? 0} messages before adapter");
            if (request.Messages != null)
            {
                foreach (var msg in request.Messages)
                {
                    var preview = msg.Content?.Length > 50 ? msg.Content.Substring(0, 50) + "..." : msg.Content;
                    Debug.Log($"  - [{msg.Role}] {preview}");
                }
            }

            // Create a temporary configuration wrapper for the request
            var config = new ConversationRequestAdapter(request);

            // Route to appropriate flow based on IsNpcInitiated flag
            if (request.IsNpcInitiated)
            {
                // NPC-initiated: Skip STT, go directly to text input flow (with empty text)
                Debug.Log("[RequestOrchestrator] Using text input flow for NPC-initiated conversation");
                StartTextRequest(config, ""); // Empty text = NPC initiates based on system prompt/history
            }
            else
            {
                // Player-initiated: Use normal audio/STT flow
                Debug.Log("[RequestOrchestrator] Using audio request flow for player-initiated conversation");
                StartAudioRequest(config);
            }
        }

        /// <summary>
        /// Start an audio request with NPC configuration
        /// </summary>
        public void StartAudioRequest(INpcConfiguration npcConfig)
        {
            if (npcConfig == null)
            {
                Debug.LogError("[RequestOrchestrator] Cannot start audio request with null NPC configuration!");
                return;
            }

            Debug.Log($"[RequestOrchestrator] Starting audio request for NPC: {npcConfig.Name}");

            // Check if we're already in a session with a different NPC
            if (_currentSession != null && _activeNpcConfig?.Id != npcConfig.Id)
            {
                Debug.Log($"[RequestOrchestrator] Switching from {_activeNpcConfig?.Name} to {npcConfig.Name}");
                CancelCurrentSession("Switching to different NPC");
            }

            _activeNpcConfig = npcConfig;

            // Get NPC client from provider - MUST be configured!
            if (_npcProvider == null)
            {
                Debug.LogError("[RequestOrchestrator] No NPC provider configured! Set 'npcProviderComponent' in Inspector to a component implementing INpcProvider (e.g., AIBridgeRulesHandler).");
                return;
            }

            _activeNpcClient = _npcProvider.GetNpcClient(npcConfig.Id);

            if (_activeNpcClient == null)
            {
                Debug.LogError($"[RequestOrchestrator] NPC provider returned null for NPC ID: '{npcConfig.Id}'. Check that NPC client exists in scene and matches this ID.");
                return;
            }

            Debug.Log($"[RequestOrchestrator] Found NPC client: {_activeNpcClient.NpcName} for ID: {npcConfig.Id}");

            // Notify listeners (e.g., InterruptionManager) about active NPC change
            OnActiveNpcChanged?.Invoke(_activeNpcClient, _activeNpcConfig);

            // NOTE: Buffering is now handled automatically by AudioStreamProcessor.StartEncoding()
            // Audio is ALWAYS buffered by default until FlushBuffer() is called after SessionStarted
            // This handles all scenarios: normal requests, interruptions, RuleSystem delays, reconnects

            // Mark request as active - audio chunks can now be accepted (they will be buffered)
            _isRequestActive = true;

            // Start PTT duration tracking in latency tracker (after we have _activeNpcClient)
            var tracker = GetLatencyTracker(_activeNpcClient);
            if (tracker != null)
            {
                tracker.MarkRecordingStart();
                Debug.Log($"[RequestOrchestrator] MarkRecordingStart() called for {_activeNpcClient?.NpcName}");
            }
            else
            {
                Debug.LogWarning($"[RequestOrchestrator] Could not call MarkRecordingStart() - LatencyTracker is null");
            }

            // Note: Animation events should be triggered via the NPC client, not directly

            // Queue the request
            var request = new AudioRequest
            {
                NpcConfig = npcConfig,
                RequestId = Guid.NewGuid().ToString()
            };

            _audioRequestQueue.Enqueue(request);
            Debug.Log($"[RequestOrchestrator] Audio request queued. Queue size: {_audioRequestQueue.Count}");
        }

        /// <summary>
        /// Start an audio request by NPC ID
        /// </summary>
        public void StartAudioRequest(string npcId)
        {
            if (_npcProvider == null)
            {
                Debug.LogError("[RequestOrchestrator] No NPC provider set! Cannot start audio request by ID. " +
                              "Either set an NPC provider or use StartAudioRequest(INpcConfiguration) directly.");
                return;
            }

            var npcConfig = _npcProvider.GetNpcConfiguration(npcId);
            if (npcConfig == null)
            {
                Debug.LogError($"[RequestOrchestrator] No NPC configuration found for ID: {npcId}");
                return;
            }

            StartAudioRequest(npcConfig);
        }

        /// <summary>
        /// Start a text-based request (NPC-initiated or system)
        /// </summary>
        public void StartTextRequest(INpcConfiguration npcConfig, string text)
        {
            // Note: text can be empty string for NPC-initiated conversations (NPC speaks first without player input)
            if (npcConfig == null || text == null)
            {
                Debug.LogError("[RequestOrchestrator] Invalid text request parameters!");
                return;
            }

            var isNpcInitiated = string.IsNullOrEmpty(text);
            Debug.Log($"[RequestOrchestrator] Starting text request for {npcConfig.Name}" +
                     (isNpcInitiated ? " (NPC-initiated, no player input)" : $": {text}"));

            _activeNpcConfig = npcConfig;

            // Get NPC client from provider - MUST be configured!
            if (_npcProvider == null)
            {
                Debug.LogError("[RequestOrchestrator] No NPC provider configured! Set 'npcProviderComponent' in Inspector to a component implementing INpcProvider (e.g., AIBridgeRulesHandler).");
                return;
            }

            _activeNpcClient = _npcProvider.GetNpcClient(npcConfig.Id);

            if (_activeNpcClient == null)
            {
                Debug.LogError($"[RequestOrchestrator] NPC provider returned null for NPC ID: '{npcConfig.Id}'. Check that NPC client exists in scene and matches this ID.");
                return;
            }

            Debug.Log($"[RequestOrchestrator] Found NPC client: {_activeNpcClient.NpcName} for ID: {npcConfig.Id}");

            // Notify listeners (e.g., InterruptionManager) about active NPC change
            OnActiveNpcChanged?.Invoke(_activeNpcClient, _activeNpcConfig);

            var request = new TextRequest
            {
                NpcConfig = npcConfig,
                Text = text,
                RequestId = Guid.NewGuid().ToString()
            };

            _textRequestQueue.Enqueue(request);
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

            Debug.Log("[RequestOrchestrator] Audio request ended (PTT released)");
        }

        /// <summary>
        /// Cancel the current session
        /// </summary>
        public void CancelCurrentSession(string reason = "User cancelled")
        {
            if (_currentSession != null)
            {
                Debug.Log($"[RequestOrchestrator] Cancelling session {_currentSession.RequestId}: {reason}");

                // Discard any buffered audio (RuleSystem rejection or interruption)
                if (speechInputHandler?.AudioStreamProcessor != null)
                {
                    speechInputHandler.AudioStreamProcessor.DiscardBuffer();
                    Debug.Log("[RequestOrchestrator] Discarded buffered audio due to session cancellation");
                }

                // Stop any ongoing recording
                speechInputHandler?.StopRecording();

                // Cancel WebSocket session - notify backend to stop LLM/TTS generation
                var requestIdToCancel = _currentSession.RequestId;
                _ = CancelSessionOnBackendAsync(requestIdToCancel, reason);

                // Clear session
                _currentSession = null;
                _activeNpcConfig = null;
                _activeNpcClient = null;
                _isProcessingRequest = false;

                // Notify listeners that there's no active NPC anymore
                OnActiveNpcChanged?.Invoke(null, null);
                _isRequestActive = false;
            }
        }

        /// <summary>
        /// Send SessionCancel message to backend to stop LLM/TTS generation
        /// Does NOT clear session state - use CancelCurrentSession for full cleanup
        /// </summary>
        public void SendSessionCancelToBackend(string requestId, string reason)
        {
            if (string.IsNullOrEmpty(requestId))
            {
                Debug.LogWarning("[RequestOrchestrator] Cannot send SessionCancel - requestId is null or empty");
                return;
            }

            _ = CancelSessionOnBackendAsync(requestId, reason);
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
                Debug.Log($"[RequestOrchestrator] Sent InterruptionOccurred to backend for session {requestId} (reason: {reason})");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[RequestOrchestrator] Failed to send InterruptionOccurred: {ex.Message}");
            }
        }

        /// <summary>
        /// Check if currently processing a request
        /// </summary>
        public bool IsProcessingRequest()
        {
            return _isProcessingRequest || _currentSession != null;
        }

        /// <summary>
        /// Check if audio is currently playing
        /// </summary>
        public bool IsAudioPlaying()
        {
            // Check if the active NPC client is currently playing audio
            return _activeNpcClient?.IsSpeaking ?? false;
        }

        /// <summary>
        /// Check if an interruption is currently active in the current session
        /// </summary>
        public bool IsInterruptionActive()
        {
            return _currentSession?.IsInterruptionActive ?? false;
        }

        /// <summary>
        /// Mark that an interruption has started in the current session
        /// </summary>
        public void StartInterruption()
        {
            if (_currentSession != null)
            {
                _currentSession.IsInterruptionActive = true;
                Debug.Log($"[RequestOrchestrator] Interruption started for session {_currentSession.RequestId}");
            }
        }

        /// <summary>
        /// Mark that an interruption has ended in the current session
        /// </summary>
        public void EndInterruption()
        {
            if (_currentSession != null)
            {
                _currentSession.IsInterruptionActive = false;
                Debug.Log($"[RequestOrchestrator] Interruption ended for session {_currentSession.RequestId}");
            }
        }

        /// <summary>
        /// Get the number of audio streams received in the current session
        /// </summary>
        public int GetCurrentSessionStreamsReceived()
        {
            return _currentSession?.StreamsReceived ?? 0;
        }

        /// <summary>
        /// Get the current session RequestId (null if no active session)
        /// </summary>
        public string GetCurrentSessionId()
        {
            return _currentSession?.RequestId;
        }

        /// <summary>
        /// Complete the current session (used when no audio received)
        /// </summary>
        public void CompleteCurrentSession()
        {
            if (_currentSession != null)
            {
                _currentSession.Complete();
                Debug.Log($"[RequestOrchestrator] Session {_currentSession.RequestId} completed");
                _currentSession = null;
            }
        }

        /// <summary>
        /// Raise the OnTranscriptionReceived event
        /// Called by NpcClientBase when transcription is received from WebSocket
        /// </summary>
        public void RaiseTranscriptionReceived(string transcript)
        {
            OnTranscriptionReceived?.Invoke(transcript);
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
            if (_webSocketClient != null && _webSocketClient.IsConnected)
            {
                _ = _webSocketClient.SendBinaryAsync(encodedAudio);
            }
            else
            {
                Debug.LogWarning($"[RequestOrchestrator] WebSocket not connected - cannot send {encodedAudio.Length} bytes");
            }
        }

        /// <summary>
        /// Handle recording stopped event from SpeechInputHandler.
        /// Sends EndOfSpeech and EndOfAudio messages to backend to trigger transcription.
        /// </summary>
        private async void HandleRecordingStopped()
        {
            Debug.Log("[RequestOrchestrator] Recording stopped - sending EndOfSpeech and EndOfAudio");

            // Only send messages if there's an active request
            if (!_isRequestActive || _currentSession == null)
            {
                Debug.LogWarning("[RequestOrchestrator] Recording stopped but no active request - messages not sent");
                return;
            }

            if (_webSocketClient == null || !_webSocketClient.IsConnected)
            {
                Debug.LogError("[RequestOrchestrator] Cannot send end messages - WebSocket not connected");
                return;
            }

            // Start latency measurement - from PTT release to audio playback start
            var tracker = GetLatencyTracker(_activeNpcClient);
            if (tracker != null)
            {
                tracker.StartMeasurement();
                Debug.Log($"[RequestOrchestrator] StartMeasurement() called for {_activeNpcClient?.NpcName} at PTT release");
            }
            else
            {
                Debug.LogWarning($"[RequestOrchestrator] Could not call StartMeasurement() - LatencyTracker is null");
            }

            try
            {
                // Send EndOfSpeech - indicates user stopped speaking
                await _webSocketClient.SendEndOfSpeechAsync(_currentSession.RequestId);
                Debug.Log($"[RequestOrchestrator] EndOfSpeech sent for session: {_currentSession.RequestId}");

                // Send EndOfAudio - indicates all audio data has been transmitted
                await _webSocketClient.SendEndOfAudioAsync(_currentSession.RequestId);
                Debug.Log($"[RequestOrchestrator] EndOfAudio sent for session: {_currentSession.RequestId}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[RequestOrchestrator] Failed to send end messages: {ex.Message}");
            }
        }

        #endregion

        #region Private Methods

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
            if (interruptionManager == null)
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
                Debug.Log($"[RequestOrchestrator] Processing audio request for {request.NpcConfig.Name}");

                // CRITICAL: Recording is ALREADY started by SpeechInputHandler at PTT press
                // Audio is being encoded by AudioStreamProcessor
                // Buffering was already started in StartAudioRequest() to prevent early audio chunks
                // from being sent before SessionStarted confirmation

                // Create session parameters
                var parameters = BuildSessionParameters(request.NpcConfig);

                // Start WebSocket session
                var npcName = _activeNpcClient?.NpcName ?? request.NpcConfig.Name;
                _currentSession = new ConversationSession(npcName, request.RequestId);

                // Register this request with the NPC router so messages are routed correctly
                NpcMessageRouter.Instance.SetActiveRequest(request.RequestId, npcName);

                // CRITICAL: Register the NPC handler with WebSocketClient to receive responses
                if (_activeNpcClient is INpcMessageHandler handler)
                {
                    _webSocketClient.RegisterNpc(request.RequestId, handler);
                    Debug.Log($"[RequestOrchestrator] Registered NPC handler for RequestId: {request.RequestId}");
                }
                else
                {
                    Debug.LogError("[RequestOrchestrator] Cannot register NPC handler - _activeNpcClient is null or doesn't implement INpcMessageHandler!");
                }

                var messages = GetChatHistory();

                // Build SessionStartMessage with all parameters including custom vocabulary
                var sessionStartMessage = new SessionStartMessage
                {
                    RequestId = request.RequestId,
                    Messages = messages,
                    // Core audio settings
                    AudioFormat = parameters.AudioFormat,
                    SampleRate = parameters.SampleRate,
                    OpusBitrate = parameters.Bitrate,
                    // TTS settings
                    VoiceId = parameters.VoiceId,
                    TtsModel = parameters.Model,
                    TtsOutputFormat = parameters.AudioFormat,
                    TtsStreamingMode = parameters.TtsStreamingMode,
                    // LLM settings
                    LlmProvider = parameters.LlmProvider,
                    LlmModel = parameters.LlmModel,
                    Temperature = parameters.Temperature,
                    MaxTokens = parameters.MaxTokens,
                    // STT settings
                    SttProvider = parameters.SttProvider,
                    LanguageCode = parameters.Language,  // Note: field is called LanguageCode, not Language
                    // Get custom vocabulary from SpeechInputHandler
                    CustomVocabulary = speechInputHandler?.ParsedCustomVocabulary,
                    CustomVocabularyBoost = 10.0f, // Fixed boost value for Google STT
                    // Enable metrics if configured
                    EnableMetrics = enableMetrics
                };

                // CRITICAL FIX: Subscribe to SessionStarted BEFORE sending SessionStart
                // This prevents race condition where SessionStarted arrives before we subscribe
                var sessionStartedReceived = false;
                Action sessionStartedHandler = () => { sessionStartedReceived = true; };

                if (_activeNpcClient != null)
                {
                    _activeNpcClient.OnSessionStarted += sessionStartedHandler;
                    Debug.Log("[RequestOrchestrator] Subscribed to SessionStarted event - ready to receive confirmation");
                }

                // Start the conversation via WebSocket
                var sendTask = _webSocketClient.SendSessionStartAsync(sessionStartMessage);
                yield return new WaitUntil(() => sendTask.IsCompleted);

                if (sendTask.IsFaulted)
                {
                    Debug.LogError($"[RequestOrchestrator] Failed to send SessionStart: {sendTask.Exception?.GetBaseException().Message}");
                    // Unsubscribe on error
                    if (_activeNpcClient != null)
                    {
                        _activeNpcClient.OnSessionStarted -= sessionStartedHandler;
                    }
                    yield break;
                }

                Debug.Log($"[RequestOrchestrator] SessionStart message sent successfully");

                // Wait for SessionStarted confirmation from backend before flushing
                if (_activeNpcClient != null)
                {
                    Debug.Log("[RequestOrchestrator] Waiting for SessionStarted confirmation from backend...");

                    // Wait for SessionStarted confirmation (max 5 seconds)
                    var timeout = 5.0f;
                    var elapsed = 0f;
                    while (!sessionStartedReceived && elapsed < timeout)
                    {
                        yield return null;
                        elapsed += Time.deltaTime;
                    }

                    _activeNpcClient.OnSessionStarted -= sessionStartedHandler;

                    if (sessionStartedReceived)
                    {
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
                    Debug.Log("[RequestOrchestrator] Backend confirmed ready - flushed buffered audio to WebSocket");
                }

                // Session might have been completed already (race condition with conversationComplete)
                if (_currentSession != null)
                {
                    Debug.Log($"[RequestOrchestrator] Audio request started. Session: {_currentSession.RequestId}");
                }
                else
                {
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
                Debug.Log($"[RequestOrchestrator] Processing text request: {request.Text}");

                // Validate NPC client is available
                if (_activeNpcClient == null)
                {
                    Debug.LogError("[RequestOrchestrator] Cannot process text request - no active NPC client! This should have been caught in StartTextRequest.");
                    _isProcessingRequest = false;
                    yield break;
                }

                // Start WebSocket session with text input
                var npcName = _activeNpcClient?.NpcName ?? request.NpcConfig.Name;
                _currentSession = new ConversationSession(npcName, request.RequestId);

                // CRITICAL: Register the NPC handler with WebSocketClient to receive responses
                if (_activeNpcClient is INpcMessageHandler textHandler)
                {
                    _webSocketClient.RegisterNpc(request.RequestId, textHandler);
                    Debug.Log($"[RequestOrchestrator] Registered NPC handler for text request: {request.RequestId}");
                }
                else
                {
                    Debug.LogError("[RequestOrchestrator] Cannot register NPC handler for text request - _activeNpcClient is null or doesn't implement INpcMessageHandler!");
                }

                var messages = GetChatHistory();

                // Build TextInputMessage for text-based conversation
                var textInputMessage = new TextInputMessage
                {
                    RequestId = request.RequestId,
                    Text = request.Text,
                    IsNpcInitiated = string.IsNullOrEmpty(request.Text), // Empty text = NPC-initiated
                    Context = new ConversationContext
                    {
                        messages = messages,
                        systemPrompt = request.NpcConfig?.SystemPrompt,
                        voiceId = request.NpcConfig?.VoiceId,
                        ttsStreamingMode = request.NpcConfig?.TtsStreamingMode,
                        llmModel = request.NpcConfig?.LlmModel,
                        llmProvider = request.NpcConfig?.LlmProvider,
                        language = request.NpcConfig?.Language,
                        temperature = request.NpcConfig?.Temperature ?? 0,
                        maxTokens = request.NpcConfig?.MaxTokens ?? 0,
                        ttsModel = request.NpcConfig?.TtsModel,
                        sttProvider = request.NpcConfig?.SttProvider,
                        // Audio settings removed - API always uses opus_48000_64
                    }
                };

                // Send text input via WebSocket
                yield return _webSocketClient.SendTextInputAsync(textInputMessage);

                Debug.Log($"[RequestOrchestrator] Text request started. Session: {_currentSession.RequestId}");
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
                Bitrate = MicrophoneCapture.UPSTREAM_OPUS_BITRATE,  // UPSTREAM: 16kbps
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
        private List<ChatMessage> GetChatHistory()
        {
            if (_activeNpcConfig == null)
            {
                Debug.LogWarning("[RequestOrchestrator] No active NPC config - returning empty messages");
                return new List<ChatMessage>();
            }

            // Priority 1: Use Messages if provided (RuleSystem path - already complete with system prompt)
            if (_activeNpcConfig.Messages != null && _activeNpcConfig.Messages.Count > 0)
            {
                Debug.Log($"[RequestOrchestrator] Using {_activeNpcConfig.Messages.Count} messages from config (includes system prompt)");
                return new List<ChatMessage>(_activeNpcConfig.Messages);
            }

            // Priority 2: Build Messages from SystemPrompt + NPC client history (SimpleNpcClient path)
            var messages = new List<ChatMessage>();

            // Add system prompt as first message if available
            if (!string.IsNullOrEmpty(_activeNpcConfig.SystemPrompt))
            {
                messages.Add(new ChatMessage
                {
                    Role = "system",
                    Content = _activeNpcConfig.SystemPrompt
                });
            }

            // Add chat history from NPC client if available
            if (_activeNpcClient != null && _activeNpcClient is IConversationHistory historyProvider)
            {
                var history = historyProvider.GetApiHistoryAsChatMessages();
                if (history != null && history.Count > 0)
                {
                    messages.AddRange(history);
                }
            }

            if (messages.Count > 0)
            {
                Debug.Log($"[RequestOrchestrator] Built {messages.Count} messages from SystemPrompt + history");
            }
            else
            {
                Debug.Log("[RequestOrchestrator] No messages available - using empty array");
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
                Debug.LogWarning("[RequestOrchestrator] GetLatencyTracker: npcClient is NULL");
                return null;
            }

            if (npcClient.MetadataHandler == null)
            {
                Debug.LogWarning($"[RequestOrchestrator] GetLatencyTracker: MetadataHandler is NULL for NPC: {npcClient.NpcName}");
                return null;
            }

            var latencyTracker = typeof(ConversationMetadataHandler)
                .GetField("_latencyTracker", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.GetValue(npcClient.MetadataHandler) as LatencyTracker;

            if (latencyTracker == null)
            {
                Debug.LogWarning($"[RequestOrchestrator] GetLatencyTracker: Failed to get LatencyTracker via reflection for NPC: {npcClient.NpcName}");
            }

            return latencyTracker;
        }

        #endregion

        #region Internal Classes

        private class AudioRequest
        {
            public INpcConfiguration NpcConfig;
            public string RequestId;
        }

        private class TextRequest
        {
            public INpcConfiguration NpcConfig;
            public string Text;
            public string RequestId;
        }


        #endregion
    }
}