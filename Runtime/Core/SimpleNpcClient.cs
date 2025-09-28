using System.Collections.Generic;
using UnityEngine;
using Tsc.AIBridge.Messages;

namespace Tsc.AIBridge.Core
{
    /// <summary>
    /// Simple NPC client for core package - no PersonaSO dependencies.
    /// Can be configured via Inspector or programmatically.
    /// For PersonaSO integration, use the extended package NpcClient.
    /// </summary>
    public class SimpleNpcClient : NpcClientBase
    {
        #region Configuration

        [Header("NPC Configuration")]
        [SerializeField] private string npcName = "NPC";

        [Header("Voice Settings")]
        [SerializeField] private string voiceId = "default";
        [SerializeField] private string ttsStreamingMode = "batch";

        [Header("Interruption Settings")]
        [Tooltip("Whether this NPC can be interrupted during speech")]
        [SerializeField] private bool allowInterruption = true;

        [Tooltip("Persistence time in seconds for interruption detection")]
        [Range(0f, 5f)]
        [SerializeField] private float persistenceTime = 1.5f;

        [Header("Chat History")]
        [Tooltip("Simple chat history for testing - in production use extended package")]
        private List<ChatMessage> chatHistory = new List<ChatMessage>();

        #endregion

        #region Property Overrides

        public override string NpcName => npcName;

        #endregion

        #region Public Properties

        /// <summary>
        /// Voice ID for TTS
        /// </summary>
        public string VoiceId => voiceId;

        /// <summary>
        /// TTS streaming mode ("sentence" or "batch")
        /// </summary>
        public string TtsStreamingMode => ttsStreamingMode;

        /// <summary>
        /// Whether this NPC can be interrupted
        /// </summary>
        public bool AllowInterruption => allowInterruption;

        /// <summary>
        /// Persistence time for interruption detection
        /// </summary>
        public float PersistenceTime => persistenceTime;

        #endregion

        #region Configuration Methods

        /// <summary>
        /// Set NPC name
        /// </summary>
        public void SetNpcName(string name)
        {
            npcName = name;
        }

        /// <summary>
        /// Set voice configuration
        /// </summary>
        public void SetVoiceConfiguration(string voiceId, string streamingMode = null)
        {
            this.voiceId = voiceId;
            if (!string.IsNullOrEmpty(streamingMode))
                this.ttsStreamingMode = streamingMode;
        }

        /// <summary>
        /// Set interruption settings
        /// </summary>
        public void SetInterruptionSettings(bool allow, float persistence)
        {
            this.allowInterruption = allow;
            this.persistenceTime = persistence;
        }


        /// <summary>
        /// Configure this NPC programmatically
        /// </summary>
        public void Configure(
            string npcName = null,
            string voiceId = null,
            string ttsStreamingMode = null,
            bool? allowInterruption = null,
            float? persistenceTime = null)
        {
            if (!string.IsNullOrEmpty(npcName)) this.npcName = npcName;
            if (!string.IsNullOrEmpty(voiceId)) this.voiceId = voiceId;
            if (!string.IsNullOrEmpty(ttsStreamingMode)) this.ttsStreamingMode = ttsStreamingMode;
            if (allowInterruption.HasValue) this.allowInterruption = allowInterruption.Value;
            if (persistenceTime.HasValue) this.persistenceTime = persistenceTime.Value;

            LogDebug($"Configured NPC: {this.npcName}");
        }

        #endregion

        #region Abstract Method Implementations

        /// <summary>
        /// Get the conversation history as chat messages for API
        /// </summary>
        public override List<ChatMessage> GetApiHistoryAsChatMessages()
        {
            return new List<ChatMessage>(chatHistory);
        }

        /// <summary>
        /// Clear the conversation history
        /// </summary>
        public override void ClearHistory()
        {
            chatHistory.Clear();
            Debug.Log($"[SimpleNpcClient] Chat history cleared for {npcName}");
        }

        /// <summary>
        /// Add a player message to the conversation history
        /// </summary>
        public override void AddPlayerMessage(string message)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                AddMessageToHistory("user", message);
                Debug.Log($"[SimpleNpcClient] Added player message for {npcName}: {message}");
            }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Add a message to the chat history
        /// </summary>
        public void AddMessageToHistory(string role, string content)
        {
            chatHistory.Add(new ChatMessage { Role = role, Content = content });
        }

        /// <summary>
        /// Get session parameters for this NPC
        /// </summary>
        public override Dictionary<string, string> GetSessionParameters()
        {
            var parameters = new Dictionary<string, string>();

            // Add voice settings if configured
            if (!string.IsNullOrEmpty(voiceId))
            {
                parameters["voiceId"] = voiceId;
            }

            if (!string.IsNullOrEmpty(ttsStreamingMode))
            {
                parameters["ttsStreamingMode"] = ttsStreamingMode;
            }

            return parameters;
        }

        #endregion

        #region Protected Methods

        protected override void ValidateConfiguration()
        {
            if (string.IsNullOrEmpty(npcName))
            {
                Debug.LogWarning($"[SimpleNpcClient] No NPC name configured for {gameObject.name}");
            }

            if (string.IsNullOrEmpty(voiceId))
            {
                Debug.LogWarning($"[SimpleNpcClient] No voice ID configured for {gameObject.name}");
            }

            Debug.Log($"[SimpleNpcClient] Initialized: {npcName}, Voice: {voiceId}, Streaming: {ttsStreamingMode}");
        }

        #endregion
    }
}