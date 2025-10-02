using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using Tsc.AIBridge.Messages;
using Tsc.AIBridge.WebSocket;

namespace Tsc.AIBridge.Core
{
    /// <summary>
    /// Base class for NPC clients with common functionality.
    /// Extended by SimpleNpcClient (core) and ExtendedNpcClient (with PersonaSO).
    /// Implements IConversationHistory with virtual methods for flexible overriding.
    /// </summary>
    public abstract class NpcClientBase : MonoBehaviour, IConversationHistory, INpcMessageHandler
    {
        #region Properties

        /// <summary>
        /// Whether this NPC is currently active
        /// </summary>
        public bool IsActive { get; set; }

        /// <summary>
        /// Whether this NPC is currently talking
        /// </summary>
        public bool IsTalking { get; set; }

        /// <summary>
        /// NPC display name
        /// </summary>
        public abstract string NpcName { get; }

        /// <summary>
        /// Last response text from this NPC
        /// </summary>
        public string LastResponseText { get; protected set; }

        /// <summary>
        /// Metadata handler for processing WebSocket messages (internal for router access)
        /// </summary>
        internal ConversationMetadataHandler _metadataHandler;

        /// <summary>
        /// Public access to metadata handler for testing and router access.
        /// This property provides controlled access to the internal handler.
        /// </summary>
        public ConversationMetadataHandler MetadataHandler => _metadataHandler;

        /// <summary>
        /// Latency tracker for performance monitoring
        /// </summary>
        protected LatencyTracker _latencyTracker;

        #endregion

        #region Public API

        /// <summary>
        /// Check if the NPC is currently speaking
        /// </summary>
        public bool IsSpeaking => IsTalking;

        /// <summary>
        /// Check if the NPC is currently listening
        /// </summary>
        public bool IsListening { get; protected set; }

        /// <summary>
        /// Get the NPC's unique identifier
        /// </summary>
        public virtual string NpcId => gameObject.GetInstanceID().ToString();

        /// <summary>
        /// Stop any ongoing audio playback
        /// </summary>
        public virtual void StopAudio()
        {
            // Override in derived classes to control audio playback
            IsTalking = false;
            OnAudioStopped?.Invoke();
        }

        /// <summary>
        /// Pause audio playback
        /// </summary>
        public virtual void PauseAudio()
        {
            // Override in derived classes to control audio playback
        }

        /// <summary>
        /// Resume audio playback
        /// </summary>
        public virtual void ResumeAudio()
        {
            // Override in derived classes to control audio playback
        }

        #endregion

        #region Unity Lifecycle

        protected virtual void Awake()
        {
            // Audio is now handled by StreamingAudioPlayer/NpcAudioPlayer components
        }

        protected virtual void Start()
        {
            ValidateConfiguration();
            InitializeMessageHandlers();
        }

        /// <summary>
        /// Initialize message handlers for WebSocket communication
        /// </summary>
        protected virtual void InitializeMessageHandlers()
        {
            // Initialize latency tracker if metrics are enabled
            // This can be overridden in derived classes to check specific settings
            _latencyTracker = new LatencyTracker(NpcName);

            // Initialize metadata handler for processing WebSocket messages
            _metadataHandler = new ConversationMetadataHandler(NpcName, _latencyTracker, enableVerboseLogging: false);

            // Subscribe to transcription events to forward to RequestOrchestrator
            _metadataHandler.OnTranscription += HandleTranscription;

            // Subscribe to SessionStarted event and forward to public OnSessionStarted event
            _metadataHandler.OnSessionStarted += () => OnSessionStarted?.Invoke();

            // Subscribe to AI response event and call OnAiResponseReceived method
            _metadataHandler.OnAIResponse += (response) =>
            {
                OnAiResponseReceived(response);
            };

            // Register with the message router to receive WebSocket messages
            NpcMessageRouter.Instance.RegisterNpc(this);
        }

        /// <summary>
        /// Handle transcription received from WebSocket
        /// </summary>
        protected virtual void HandleTranscription(string transcript)
        {
            // Forward to RequestOrchestrator if it exists
            var orchestrator = RequestOrchestrator.Instance;
            if (orchestrator != null)
            {
                orchestrator.RaiseTranscriptionReceived(transcript);
            }

            // Fire static event for test UI (LatencyLogUI)
            OnTranscriptionReceivedStatic?.Invoke(NpcName, transcript);

            Debug.Log($"[{NpcName}] Transcription: {transcript}");
        }

        protected abstract void ValidateConfiguration();

        protected virtual void OnDestroy()
        {
            // Cleanup message handlers
            if (_metadataHandler != null)
            {
                _metadataHandler.OnTranscription -= HandleTranscription;
                // Note: OnSessionStarted uses lambda, automatically cleaned up when _metadataHandler is disposed
            }

            // Unregister from message router (only if it exists, don't create new one during cleanup)
            if (NpcMessageRouter.HasInstance)
            {
                NpcMessageRouter.Instance.UnregisterNpc(this);
            }

            StopAudio();
        }

        #endregion

        #region Events

        /// <summary>
        /// Event fired when backend confirms session started.
        /// Used by RequestOrchestrator to know when STT stream is ready.
        /// </summary>
        public event Action OnSessionStarted;

        /// <summary>
        /// Event fired when response is received
        /// </summary>
        public event Action<string> OnResponseReceived;

        /// <summary>
        /// Event fired when audio starts playing
        /// </summary>
        public event Action OnAudioStarted;

        /// <summary>
        /// Event fired when audio stops playing
        /// </summary>
        public event Action OnAudioStopped;

        /// <summary>
        /// Event fired when conversation starts
        /// </summary>
        public event Action OnConversationStarted;

        /// <summary>
        /// Event fired when conversation ends
        /// </summary>
        public event Action OnConversationEnded;

        /// <summary>
        /// Static event for transcription received - for test UI (LatencyLogUI)
        /// </summary>
        public static event Action<string, string> OnTranscriptionReceivedStatic; // (personaName, transcript)

        /// <summary>
        /// Static event for AI response received - for test UI (LatencyLogUI)
        /// </summary>
        public static event Action<string, string> OnAIResponseReceivedStatic; // (personaName, response)

        /// <summary>
        /// Unity event fired when NPC responds with text
        /// </summary>
        [Header("Events")]
        public UnityEvent<string> OnNpcResponse = new UnityEvent<string>();

        #endregion

        #region Public Methods

        /// <summary>
        /// Start a conversation with text input (equivalent to audio streaming but without STT)
        /// </summary>
        /// <param name="userInput">The user's text input</param>
        /// <param name="context">Conversation context with all parameters</param>
        public virtual void StartTextConversation(string userInput, ConversationContext context)
        {
            Debug.Log($"[{GetType().Name}] Starting text conversation: {userInput}");

            // Create text input message with context
            var textMessage = CreateTextInputMessage(userInput, false, context);

            // Send to backend
            SendTextInputMessage(textMessage);
        }

        /// <summary>
        /// Send scripted text directly to TTS without LLM processing.
        /// Useful for pre-defined NPC responses, system messages, or scripted dialogue.
        /// </summary>
        /// <param name="text">The text to convert to speech</param>
        /// <param name="voice">Optional voice override (null = use default)</param>
        /// <param name="model">Optional TTS model override (null = use default)</param>
        public virtual async void SendDirectTTS(string text, string voice = null, string model = null)
        {
            Debug.Log($"[{GetType().Name}] Sending DirectTTS: {text} (voice: {voice ?? "default"})");

            // Create DirectTTS message
            var directTtsMessage = new DirectTTSMessage
            {
                RequestId = Guid.NewGuid().ToString(),
                Text = text,
                Voice = voice,
                Model = model
            };

            // Get the WebSocket client
            var webSocketClient = WebSocketClient.Instance;
            if (webSocketClient == null)
            {
                Debug.LogError($"[{GetType().Name}] WebSocketClient not found!");
                return;
            }

            // Register this NPC to receive responses for this request
            webSocketClient.RegisterNpc(directTtsMessage.RequestId, this);

            // Send DirectTTS message
            await webSocketClient.SendDirectTTSAsync(directTtsMessage);

            Debug.Log($"[{GetType().Name}] DirectTTS message sent with RequestId: {directTtsMessage.RequestId}");
        }

        /// <summary>
        /// Create a TextInputMessage with the given parameters
        /// </summary>
        protected virtual TextInputMessage CreateTextInputMessage(string text, bool isNpcInitiated, ConversationContext context)
        {
            return new TextInputMessage
            {
                RequestId = Guid.NewGuid().ToString(),
                Text = text,
                IsNpcInitiated = isNpcInitiated,
                Context = context
            };
        }

        /// <summary>
        /// Send a TextInputMessage to the backend
        /// </summary>
        protected virtual async void SendTextInputMessage(TextInputMessage message)
        {
            Debug.Log($"[{GetType().Name}] Sending text input message. NPC-initiated: {message.IsNpcInitiated}");

            // Get the WebSocket client
            var webSocketClient = WebSocketClient.Instance;
            if (webSocketClient == null)
            {
                Debug.LogError($"[{GetType().Name}] WebSocketClient not found in scene");
                return;
            }

            // Register this NPC for the request
            NpcMessageRouter.Instance.SetActiveRequest(message.RequestId, NpcName);

            // Send the text input message
            try
            {
                await webSocketClient.SendTextInputAsync(message);
                Debug.Log($"[{GetType().Name}] Sent text input message. RequestId: {message.RequestId}, NPC-initiated: {message.IsNpcInitiated}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[{GetType().Name}] Failed to send text input: {ex.Message}");
            }

            // Fire event
            OnConversationStarted?.Invoke();
        }


        /// <summary>
        /// Handle AI response received from the backend.
        /// Stores the response and triggers relevant events.
        /// </summary>
        /// <param name="response">The text response from the AI</param>
        /// <param name="metadata">Optional metadata about the response (timing, model info, etc.)</param>
        public virtual void OnAiResponseReceived(string response, Dictionary<string, object> metadata = null)
        {
            Debug.Log($"[{GetType().Name}] AI response: {response}");

            // Store last response
            LastResponseText = response;

            // Fire static event for test UI (LatencyLogUI)
            OnAIResponseReceivedStatic?.Invoke(NpcName, response);

            // Fire events
            OnResponseReceived?.Invoke(response);
            OnNpcResponse?.Invoke(response);
        }

        /// <summary>
        /// Handle audio playback started
        /// </summary>
        public virtual void OnAudioPlaybackStarted()
        {
            IsTalking = true;
            OnAudioStarted?.Invoke();
        }

        /// <summary>
        /// Handle audio playback stopped
        /// </summary>
        public virtual void OnAudioPlaybackStopped()
        {
            IsTalking = false;
            OnAudioStopped?.Invoke();
        }

        /// <summary>
        /// End current conversation
        /// </summary>
        public virtual void EndConversation()
        {
            IsTalking = false;
            OnConversationEnded?.Invoke();
        }


        #endregion

        #region Protected Helpers

        /// <summary>
        /// Log debug message with NPC context
        /// </summary>
        protected void LogDebug(string message)
        {
            Debug.Log($"[{GetType().Name}:{NpcName}] {message}");
        }

        /// <summary>
        /// Log warning with NPC context
        /// </summary>
        protected void LogWarning(string message)
        {
            Debug.LogWarning($"[{GetType().Name}:{NpcName}] {message}");
        }

        /// <summary>
        /// Log error with NPC context
        /// </summary>
        protected void LogError(string message)
        {
            Debug.LogError($"[{GetType().Name}:{NpcName}] {message}");
        }

        #endregion

        #region IConversationHistory Implementation (Virtual)

        /// <summary>
        /// Get the conversation history as chat messages for API.
        /// Default implementation returns empty list. Override in derived classes to provide actual history.
        /// </summary>
        public virtual List<ChatMessage> GetApiHistoryAsChatMessages()
        {
            // Default: return empty list
            // Derived classes can override to provide actual history
            return new List<ChatMessage>();
        }

        /// <summary>
        /// Clear the conversation history.
        /// Default implementation does nothing. Override in derived classes that manage history.
        /// </summary>
        public virtual void ClearHistory()
        {
            // Default: no-op
            // Derived classes can override if they manage history
            // RuleSystem-based implementations typically don't clear history here
        }

        /// <summary>
        /// Add a player message to the conversation history.
        /// Default implementation does nothing. Override in derived classes that track history.
        /// </summary>
        /// <param name="message">The player's message to add</param>
        public virtual void AddPlayerMessage(string message)
        {
            // Default: no-op
            // Derived classes can override if they track history
        }

        #endregion

        #region INpcMessageHandler Implementation

        /// <summary>
        /// Handle incoming text messages from WebSocket
        /// </summary>
        public virtual void OnTextMessage(string json)
        {
            // Handle text messages - typically JSON messages like AiResponse, AudioStreamStart, etc.
            // This is usually handled by the NpcMessageRouter, but we need to implement it for the interface
            Debug.Log($"[{GetType().Name}] Received text message: {json}");
        }

        /// <summary>
        /// Handle incoming binary messages from WebSocket (audio data)
        /// </summary>
        public virtual void OnBinaryMessage(byte[] data)
        {
            // Handle binary messages - typically audio data
            // This is usually handled by the audio processing system
            Debug.Log($"[{GetType().Name}] Received binary message: {data.Length} bytes");
        }

        /// <summary>
        /// Called when a request is completed
        /// </summary>
        public virtual void OnRequestComplete(string requestId)
        {
            Debug.Log($"[{GetType().Name}] Request completed: {requestId}");
            // Clean up any request-specific resources if needed
        }

        #endregion
    }
}