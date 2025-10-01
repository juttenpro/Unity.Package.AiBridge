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
        [SerializeField] private SpeechInputHandler speechInputHandler;

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
        private INpcClient _activeNpcClient; // Cache to avoid FindObjectsByType
        private bool _isProcessingRequest; // Queue management - prevents concurrent request STARTS
        private bool _isRequestActive; // Request lifecycle - true from StartAudioRequest until EndAudioRequest/Cancel
        private bool _isInterrupting;
        private Coroutine _processQueueCoroutine;

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

            // Subscribe to SpeechInputHandler's AudioStreamProcessor for encoded audio
            if (speechInputHandler != null && speechInputHandler.AudioStreamProcessor != null)
            {
                speechInputHandler.AudioStreamProcessor.OnOpusAudioEncoded += ProcessAudioChunk;
                Debug.Log("[RequestOrchestrator] Subscribed to AudioStreamProcessor.OnOpusAudioEncoded");
            }
            else
            {
                Debug.LogError("[RequestOrchestrator] SpeechInputHandler or AudioStreamProcessor is null! Audio encoding will not work!");
            }

            // Subscribe to recording stopped event to send EndOfSpeech
            if (speechInputHandler != null)
            {
                speechInputHandler.OnRecordingStopped += HandleRecordingStopped;
                Debug.Log("[RequestOrchestrator] Subscribed to SpeechInputHandler.OnRecordingStopped");
            }

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
        /// Start an audio request with complete conversation request (preferred method)
        /// This is called when RuleSystem determines all conversation parameters
        /// </summary>
        public void StartConversationRequest(ConversationRequest request)
        {
            if (request == null)
            {
                Debug.LogError("[RequestOrchestrator] Cannot start conversation with null request!");
                return;
            }

            Debug.Log($"[RequestOrchestrator] Starting conversation request - NPC: {request.NpcId}, STT: {request.SttProvider}, LLM: {request.LlmModel}");

            // Create a temporary configuration wrapper for the request
            var config = new ConversationRequestAdapter(request);
            StartAudioRequest(config);
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

            // Mark request as active - audio chunks can now be accepted
            _isRequestActive = true;

            // Try to find the NPC client
            if (_npcProvider != null)
            {
                _activeNpcClient = _npcProvider.GetNpcClient(npcConfig.Id);
            }
            else
            {
                // Fallback: try to find any NPC client in scene
                var allClients = FindObjectsByType<NpcClientBase>(FindObjectsSortMode.None);
                _activeNpcClient = allClients.FirstOrDefault();
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
            if (npcConfig == null || string.IsNullOrEmpty(text))
            {
                Debug.LogError("[RequestOrchestrator] Invalid text request parameters!");
                return;
            }

            Debug.Log($"[RequestOrchestrator] Starting text request for {npcConfig.Name}: {text}");

            _activeNpcConfig = npcConfig;

            // Try to find the NPC client
            if (_npcProvider != null)
            {
                _activeNpcClient = _npcProvider.GetNpcClient(npcConfig.Id);
            }

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
                Debug.Log($"[RequestOrchestrator] Cancelling session {_currentSession.SessionId}: {reason}");

                // Discard any buffered audio (RuleSystem rejection or interruption)
                if (speechInputHandler?.AudioStreamProcessor != null)
                {
                    speechInputHandler.AudioStreamProcessor.DiscardBuffer();
                    Debug.Log("[RequestOrchestrator] Discarded buffered audio due to session cancellation");
                }

                // Stop any ongoing recording
                speechInputHandler?.StopRecording();

                // Cancel WebSocket session
                // TODO: Implement CancelCurrentSession in WebSocketClient
                // For now, just log
                Debug.Log("[RequestOrchestrator] Cancel session requested");

                // Clear session
                _currentSession = null;
                _activeNpcConfig = null;
                _activeNpcClient = null;
                _isProcessingRequest = false;
                _isRequestActive = false;
                _isInterrupting = false;
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
            Debug.Log($"[RequestOrchestrator] ProcessAudioChunk called with {encodedAudio?.Length ?? 0} bytes");

            if (encodedAudio == null || encodedAudio.Length == 0)
            {
                Debug.LogWarning("[RequestOrchestrator] ProcessAudioChunk received null or empty data!");
                return;
            }

            Debug.Log($"[RequestOrchestrator] _isRequestActive={_isRequestActive}");

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
                Debug.Log($"[RequestOrchestrator] Sending {encodedAudio.Length} bytes to WebSocket");
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

            try
            {
                // Send EndOfSpeech - indicates user stopped speaking
                await _webSocketClient.SendEndOfSpeechAsync(_currentSession.SessionId);
                Debug.Log($"[RequestOrchestrator] EndOfSpeech sent for session: {_currentSession.SessionId}");

                // Send EndOfAudio - indicates all audio data has been transmitted
                await _webSocketClient.SendEndOfAudioAsync(_currentSession.SessionId);
                Debug.Log($"[RequestOrchestrator] EndOfAudio sent for session: {_currentSession.SessionId}");
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
            var missingComponents = new System.Collections.Generic.List<string>();

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

#if UNITY_EDITOR
                // Pause the editor to force attention
                Debug.Break();
#endif
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
                // Audio is being encoded by AudioStreamProcessor and buffered until we approve

                // Start buffering mode - audio will be queued in AudioStreamProcessor
                if (speechInputHandler?.AudioStreamProcessor != null)
                {
                    speechInputHandler.AudioStreamProcessor.StartBuffering();
                    Debug.Log("[RequestOrchestrator] Started audio buffering - waiting for RuleSystem approval");
                }
                else
                {
                    Debug.LogError("[RequestOrchestrator] Cannot start buffering - AudioStreamProcessor not found!");
                }

                // Create session parameters
                var parameters = BuildSessionParameters(request.NpcConfig);

                // Start WebSocket session
                _currentSession = new ConversationSession
                {
                    SessionId = request.RequestId,
                };

                // Register this request with the NPC router so messages are routed correctly
                var npcName = _activeNpcClient?.NpcName ?? request.NpcConfig.Name;
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

                // Start the conversation via WebSocket
                var sendTask = _webSocketClient.SendSessionStartAsync(sessionStartMessage);
                yield return new WaitUntil(() => sendTask.IsCompleted);

                if (sendTask.IsFaulted)
                {
                    Debug.LogError($"[RequestOrchestrator] Failed to send SessionStart: {sendTask.Exception?.GetBaseException().Message}");
                    yield break;
                }

                Debug.Log($"[RequestOrchestrator] SessionStart message sent successfully");

                // RuleSystem has approved - flush buffered audio to WebSocket
                if (speechInputHandler?.AudioStreamProcessor != null)
                {
                    speechInputHandler.AudioStreamProcessor.FlushBuffer();
                    Debug.Log("[RequestOrchestrator] RuleSystem approved - flushed buffered audio to WebSocket");
                }

                Debug.Log($"[RequestOrchestrator] Audio request started. Session: {_currentSession.SessionId}");
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

                // Start WebSocket session with text input
                _currentSession = new ConversationSession
                {
                    SessionId = request.RequestId,
                };

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
                    IsNpcInitiated = false,
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

                Debug.Log($"[RequestOrchestrator] Text request started. Session: {_currentSession.SessionId}");
            }
            finally
            {
                _isProcessingRequest = false;
            }

            yield return null;
        }

        private bool ShouldBufferAudioForRequest()
        {
            // Decision logic for whether to buffer audio before sending
            // This prevents incomplete sentences being sent to STT

            // Always buffer if NPC is speaking (interruption scenario)
            if (IsNpcSpeaking())
            {
                Debug.Log("[RequestOrchestrator] Buffering audio - NPC is speaking");
                return true;
            }

            // Buffer if we're in an interruption flow
            if (_isInterrupting)
            {
                Debug.Log("[RequestOrchestrator] Buffering audio - interruption in progress");
                return true;
            }

            // Don't buffer for normal conversation starts
            return false;
        }

        private bool IsNpcSpeaking()
        {
            // Check if any NPC is currently playing audio
            if (_activeNpcClient != null)
            {
                return _activeNpcClient.IsSpeaking;
            }

            // Fallback: check all NPCs
            var allClients = FindObjectsByType<NpcClientBase>(FindObjectsSortMode.None);
            return allClients.Any(npc => npc.IsSpeaking);
        }

        private ConnectionParameters BuildSessionParameters(INpcConfiguration npcConfig)
        {
            // Build connection parameters from NPC configuration
            var parameters = new ConnectionParameters
            {
                SessionId = Guid.NewGuid().ToString(),
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

        private List<ChatMessage> GetChatHistory()
        {
            // Get chat history from active NPC client (if it supports history)
            if (_activeNpcClient != null && _activeNpcClient is IConversationHistory historyProvider)
            {
                var history = historyProvider.GetApiHistoryAsChatMessages();
                if (history != null && history.Count > 0)
                {
                    return history;
                }
            }

            // No history available - return empty list
            // Client MUST provide system prompt if needed
            return new List<ChatMessage>();
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

        private class ConversationSession
        {
            public string SessionId;
        }

        #endregion
    }
}