using System.Collections.Generic;
using UnityEngine;
using Tsc.AIBridge.Messages;

namespace Tsc.AIBridge.Core
{
    /// <summary>
    /// Simple NPC client for core package - no PersonaSO dependencies.
    /// Can be configured via Inspector or programmatically.
    /// For PersonaSO integration, use the extended package NpcClient.
    /// Inherits IConversationHistory implementation from NpcClientBase.
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
        /// Set NPC name.
        /// Updates the display name for this NPC.
        /// </summary>
        /// <param name="name">The new name for this NPC</param>
        public void SetNpcName(string name)
        {
            npcName = name;
        }

        /// <summary>
        /// Set voice configuration for TTS.
        /// Configures how this NPC's voice will be synthesized.
        /// </summary>
        /// <param name="voiceId">The TTS voice ID to use</param>
        /// <param name="streamingMode">Optional streaming mode ("sentence" or "batch"). If null, keeps current setting</param>
        public void SetVoiceConfiguration(string voiceId, string streamingMode = null)
        {
            this.voiceId = voiceId;
            if (!string.IsNullOrEmpty(streamingMode))
                ttsStreamingMode = streamingMode;
        }

        /// <summary>
        /// Set interruption settings.
        /// Configures how this NPC handles interruptions.
        /// </summary>
        /// <param name="allow">Whether this NPC can be interrupted</param>
        /// <param name="persistence">Time in seconds user must speak to trigger interruption</param>
        public void SetInterruptionSettings(bool allow, float persistence)
        {
            allowInterruption = allow;
            persistenceTime = persistence;
        }


        /// <summary>
        /// Configure this NPC programmatically.
        /// Sets all NPC parameters in a single call.
        /// </summary>
        /// <param name="npcName">Optional NPC name override</param>
        /// <param name="voiceId">Optional voice ID for TTS</param>
        /// <param name="ttsStreamingMode">Optional TTS streaming mode ("sentence" or "batch")</param>
        /// <param name="allowInterruption">Optional interruption enabled flag</param>
        /// <param name="persistenceTime">Optional persistence time in seconds for interruption</param>
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

        #region IConversationHistory Override

        /// <summary>
        /// Get the conversation history as chat messages for API
        /// </summary>
        public override List<ChatMessage> GetApiHistoryAsChatMessages()
        {
            // Pre-allocate capacity to avoid resizing
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
        /// Add a message to the chat history.
        /// Records conversation messages for context.
        /// </summary>
        /// <param name="role">The role of the speaker ("user" or "assistant")</param>
        /// <param name="content">The message content to add</param>
        public void AddMessageToHistory(string role, string content)
        {
            chatHistory.Add(new ChatMessage { Role = role, Content = content });
        }

        /// <summary>
        /// Get conversation context for this NPC.
        /// Creates a fully configured ConversationContext with all required parameters.
        /// </summary>
        /// <param name="systemPrompt">Optional system prompt. If null, uses empty string</param>
        /// <returns>A ConversationContext configured with this NPC's settings</returns>
        public ConversationContext GetConversationContext(string systemPrompt = null)
        {
            var context = new ConversationContext
            {
                systemPrompt = systemPrompt,
                messages = GetApiHistoryAsChatMessages() ?? new List<ChatMessage>(),
                voiceId = voiceId,
                ttsStreamingMode = ttsStreamingMode,
                // These MUST be provided by caller
                language = null,
                temperature = 0,
                maxTokens = 0,
                llmModel = null,
                llmProvider = null,
                ttsModel = null,
                sttProvider = null,
                // Audio settings removed - API always uses opus_48000_64
            };

            return context;
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