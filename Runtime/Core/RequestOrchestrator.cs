using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using SimulationCrew.AIBridge.Messages;
using SimulationCrew.AIBridge.WebSocket;
using SimulationCrew.AIBridge.Audio.Interruption;
using SimulationCrew.AIBridge.Input;

namespace SimulationCrew.AIBridge.Core
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
        private static bool _isQuitting = false;

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
        [SerializeField] private WebSocketClient _webSocketClient;
        [SerializeField] private SpeechInputHandler speechInputHandler;

        [Header("Optional Components")]
        [SerializeField] private InterruptionManager interruptionManager; // Optional interruption support

        [Header("NPC Provider (Optional)")]
        [Tooltip("Optional provider for dynamic NPC lookup by ID. If not set, pass INpcConfiguration directly to request methods.")]
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
        public event System.Action<string> OnTranscriptionReceived;

        #endregion

        #region Private Fields

        private Queue<AudioRequest> _audioRequestQueue = new Queue<AudioRequest>();
        private Queue<TextRequest> _textRequestQueue = new Queue<TextRequest>();

        private ConversationSession _currentSession;
        private INpcConfiguration _activeNpcConfig;
        private INpcClient _activeNpcClient; // Cache to avoid FindObjectsByType
        private bool _isProcessingRequest;
        private bool _isInterrupting;
        private Coroutine _processQueueCoroutine;

        // For tracking whether we're waiting for audio to finish
        private bool _isWaitingForAudioStart;
        private bool _isAudioPlaying;

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
                _npcProvider = npcProviderComponent as INpcProvider;
                if (_npcProvider == null)
                {
                    Debug.LogError($"[RequestOrchestrator] Component {npcProviderComponent.name} does not implement INpcProvider!");
                }
            }

        }

        private void Start()
        {
            ValidateRequiredComponents();
            _processQueueCoroutine = StartCoroutine(ProcessRequestQueues());
        }

        private void OnDestroy()
        {
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
                StartTime = Time.time,
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
                StartTime = Time.time,
                RequestId = Guid.NewGuid().ToString()
            };

            _textRequestQueue.Enqueue(request);
            Debug.Log($"[RequestOrchestrator] Text request queued. Queue size: {_textRequestQueue.Count}");
        }

        /// <summary>
        /// Called when PTT is released
        /// </summary>
        public void EndAudioRequest()
        {
            // Note: Animation events should be triggered via the NPC client, not directly

            speechInputHandler?.StopRecording();
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
            return _isAudioPlaying;
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

        #region Private Methods

        private void ValidateRequiredComponents()
        {
            if (_webSocketClient == null)
                _webSocketClient = FindFirstObjectByType<WebSocketClient>();

            if (speechInputHandler == null)
                speechInputHandler = FindFirstObjectByType<SpeechInputHandler>();

            // InterruptionManager is optional
            // Will be null if not using interruption features

            // Log warnings for missing components
            if (_webSocketClient == null)
                Debug.LogError("[RequestOrchestrator] WebSocketClient not found! API communication will not work.");

            if (speechInputHandler == null)
                Debug.LogWarning("[RequestOrchestrator] SpeechInputHandler not found. Audio requests will not work.");

            if (interruptionManager == null)
                Debug.Log("[RequestOrchestrator] InterruptionManager not set. Interruption detection disabled.");
        }

        private IEnumerator ProcessRequestQueues()
        {
            while (true)
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

                // Determine if we should buffer audio
                bool shouldBuffer = ShouldBufferAudioForRequest();

                // Start recording with appropriate mode
                if (shouldBuffer)
                {
                    Debug.Log("[RequestOrchestrator] Starting buffered audio recording");
                    // StartRecordingWithBuffer not implemented yet
                    speechInputHandler?.StartRecording();

                    // Wait a bit for audio to accumulate
                    yield return new WaitForSeconds(0.5f);
                }
                else
                {
                    Debug.Log("[RequestOrchestrator] Starting direct audio streaming");
                    speechInputHandler?.StartRecording();
                }

                // Create session parameters
                var parameters = BuildSessionParameters(request.NpcConfig);

                // Start WebSocket session
                _currentSession = new ConversationSession
                {
                    SessionId = request.RequestId,
                    NpcConfig = request.NpcConfig,
                    StartTime = Time.time
                };

                // Register this request with the NPC router so messages are routed correctly
                var npcName = _activeNpcClient?.NpcName ?? request.NpcConfig.Name;
                NpcMessageRouter.Instance.SetActiveRequest(request.RequestId, npcName);

                var messages = GetChatHistory();
                // TODO: Implement StartConversation in WebSocketClient
                Debug.LogWarning("[RequestOrchestrator] WebSocketClient.StartConversation not yet implemented");

                // TODO: When WebSocketClient receives transcription, fire OnTranscriptionReceived event
                // This will allow AIBridgeRulesHandler to send speechDetected/noSpeechDetected to RuleSystem

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

                // Create session parameters
                var parameters = BuildSessionParameters(request.NpcConfig);

                // Start WebSocket session with text input
                _currentSession = new ConversationSession
                {
                    SessionId = request.RequestId,
                    NpcConfig = request.NpcConfig,
                    StartTime = Time.time
                };

                var messages = GetChatHistory();
                // TODO: Implement StartTextConversation in WebSocketClient
                Debug.LogWarning("[RequestOrchestrator] WebSocketClient.StartTextConversation not yet implemented");

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
                VoiceId = npcConfig.TtsVoice,
                Model = npcConfig.TtsModel,
                Language = npcConfig.Language,
                SttProvider = npcConfig.SttProvider,  // STT provider from NPC configuration
                LlmProvider = npcConfig.LlmProvider,  // LLM provider from NPC configuration
                LlmModel = npcConfig.LlmModel,        // LLM model from NPC configuration
                Temperature = npcConfig.Temperature,   // Temperature from NPC configuration
                MaxTokens = npcConfig.MaxTokens,
                TtsStreamingMode = npcConfig.TtsStreamingMode,
                // Audio format settings for DOWNSTREAM (TTS playback)
                // Note: This is 48kHz for high-quality NPC voice output
                // Different from UPSTREAM microphone capture (16kHz for STT)
                AudioFormat = "opus",
                SampleRate = 48000,  // DOWNSTREAM: High quality TTS audio
                Bitrate = 64000,
                ChannelCount = 1,
                // Enable metrics if configured
                EnableMetrics = enableMetrics
            };

            return parameters;
        }

        /// <summary>
        /// Convert ConnectionParameters to ConversationParameters for NetworkMessageController
        /// </summary>
        private Messages.ConversationParameters ConvertToConversationParameters(ConnectionParameters connParams)
        {
            return new Messages.ConversationParameters
            {
                language = connParams.Language,
                llmProvider = connParams.LlmProvider,
                llmModel = connParams.LlmModel,
                ttsModel = connParams.Model,  // Model in ConnectionParams is TTS model
                sttProvider = connParams.SttProvider,
                voiceId = connParams.VoiceId,
                maxTokens = connParams.MaxTokens,
                temperature = connParams.Temperature,
                ttsStreamingMode = connParams.TtsStreamingMode
            };
        }

        private List<ChatMessage> GetChatHistory()
        {
            // Get chat history from active NPC client
            if (_activeNpcClient != null)
            {
                var history = _activeNpcClient.GetApiHistoryAsChatMessages();
                if (history != null && history.Count > 0)
                {
                    return history;
                }
            }

            // Fallback: Create minimal message list with system prompt
            var messages = new List<ChatMessage>();

            if (_activeNpcConfig != null && !string.IsNullOrEmpty(_activeNpcConfig.SystemPrompt))
            {
                messages.Add(new ChatMessage
                {
                    Role = "system",
                    Content = _activeNpcConfig.SystemPrompt
                });
            }
            else
            {
                // Ultimate fallback
                messages.Add(new ChatMessage
                {
                    Role = "system",
                    Content = "Je bent een behulpzame assistent die in het Nederlands antwoordt."
                });
            }

            return messages;
        }

        #endregion

        #region Internal Classes

        private class AudioRequest
        {
            public INpcConfiguration NpcConfig;
            public string RequestId;
            public float StartTime;
        }

        private class TextRequest
        {
            public INpcConfiguration NpcConfig;
            public string Text;
            public string RequestId;
            public float StartTime;
        }

        private class ConversationSession
        {
            public string SessionId;
            public INpcConfiguration NpcConfig;
            public float StartTime;
        }

        #endregion
    }
}