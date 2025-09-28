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
    /// </summary>
    public abstract class NpcClientBase : MonoBehaviour, INpcClient
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

        #region INpcClient Implementation

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
        /// Get the conversation history as chat messages for API
        /// </summary>
        public abstract List<ChatMessage> GetApiHistoryAsChatMessages();

        /// <summary>
        /// Clear the conversation history
        /// </summary>
        public abstract void ClearHistory();

        /// <summary>
        /// Add a player message to the conversation history
        /// </summary>
        public abstract void AddPlayerMessage(string message);

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

            Debug.Log($"[{NpcName}] Transcription: {transcript}");
        }

        protected abstract void ValidateConfiguration();

        protected virtual void OnDestroy()
        {
            // Cleanup message handlers
            if (_metadataHandler != null)
            {
                _metadataHandler.OnTranscription -= HandleTranscription;
            }

            // Unregister from message router
            NpcMessageRouter.Instance.UnregisterNpc(this);

            StopAudio();
        }

        #endregion

        #region Events

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
        /// Unity event fired when NPC responds with text
        /// </summary>
        [Header("Events")]
        public UnityEvent<string> OnNpcResponse = new UnityEvent<string>();

        #endregion

        #region Public Methods

        /// <summary>
        /// Start a conversation with text input
        /// </summary>
        public virtual void StartTextConversation(string userInput, Dictionary<string, string> parameters = null)
        {
            Debug.Log($"[{GetType().Name}] Starting text conversation: {userInput}");

            // Fire event
            OnConversationStarted?.Invoke();

            // Subclasses should override to actually send the request
        }

        /// <summary>
        /// Handle AI response received
        /// </summary>
        public virtual void OnAiResponseReceived(string response, Dictionary<string, object> metadata = null)
        {
            Debug.Log($"[{GetType().Name}] AI response: {response}");

            // Store last response
            LastResponseText = response;

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

        /// <summary>
        /// Get session parameters for API request.
        /// Subclasses can override to add voice/TTS settings.
        /// </summary>
        public virtual Dictionary<string, string> GetSessionParameters()
        {
            // Base implementation only provides NPC identity
            // Subclasses can add voice settings if available
            return new Dictionary<string, string>
            {
                ["npcName"] = NpcName
            };
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
    }
}