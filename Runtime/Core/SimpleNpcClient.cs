using System;
using System.Collections.Generic;
using UnityEngine;
using Tsc.AIBridge.Messages;

namespace Tsc.AIBridge.Core
{
    /// <summary>
    /// Self-contained NPC client with all AI provider settings configurable from the Inspector.
    /// Implements INpcConfiguration so it can be used directly with RequestOrchestrator.
    /// For PersonaSO integration (RuleSystem-driven), use the extended package NpcClient.
    /// Inherits IConversationHistory implementation from NpcClientBase.
    /// </summary>
    public class SimpleNpcClient : NpcClientBase, INpcConfiguration
    {
        #region Configuration

        [Header("NPC Configuration")]
        [SerializeField] private string npcName = "NPC";

        [Tooltip("System prompt that defines this NPC's personality and behavior")]
        [TextArea(3, 10)]
        [SerializeField] private string systemPrompt;

        [Header("LLM Settings")]
        [Tooltip("LLM provider: \"openai\", \"vertexai\", or \"azure-openai\"")]
        [SerializeField] private string llmProvider = "openai";

        [Tooltip("LLM model name (e.g. \"gpt-4o-mini\", \"gemini-1.5-flash\")")]
        [SerializeField] private string llmModel = "gpt-4o-mini";

        [Tooltip("LLM response randomness (0 = deterministic, 2 = very creative)")]
        [Range(0f, 2f)]
        [SerializeField] private float temperature = 0.7f;

        [Tooltip("Maximum tokens in the LLM response")]
        [Min(1)]
        [SerializeField] private int maxTokens = 500;

        [Header("STT Settings")]
        [Tooltip("Speech-to-Text provider: \"google\", \"azure\", or \"openai\"")]
        [SerializeField] private string sttProvider = "google";

        [Tooltip("Language code for STT recognition (e.g. \"en-US\", \"nl-NL\")")]
        [SerializeField] private string language = "en-US";

        [Header("TTS Settings")]
        [Tooltip("ElevenLabs voice ID")]
        [SerializeField] private string voiceId = "default";

        [Tooltip("TTS model: \"eleven_turbo_v2_5\", \"eleven_flash_v2_5\", or \"eleven_multilingual_v2\"")]
        [SerializeField] private string ttsModel = "eleven_turbo_v2_5";

        [Tooltip("TTS streaming mode: \"batch\" (full response) or \"sentence\" (per sentence)")]
        [SerializeField] private string ttsStreamingMode = "batch";

        [Tooltip("Voice playback speed (0.7 = slow, 1.0 = normal, 1.2 = fast)")]
        [Range(0.7f, 1.2f)]
        [SerializeField] private float ttsSpeed = 1.0f;

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

        #region INpcConfiguration Implementation

        string INpcConfiguration.Id => NpcId;
        string INpcConfiguration.Name => npcName;
        string INpcConfiguration.SystemPrompt => systemPrompt;
        List<ChatMessage> INpcConfiguration.Messages => null; // null = use SystemPrompt + history path
        string INpcConfiguration.TtsStreamingMode => ttsStreamingMode;
        string INpcConfiguration.TtsModel => ttsModel;
        string INpcConfiguration.VoiceId => voiceId;
        string INpcConfiguration.Language => language;
        string INpcConfiguration.SttProvider => sttProvider;
        string INpcConfiguration.LlmProvider => llmProvider;
        string INpcConfiguration.LlmModel => llmModel;
        float INpcConfiguration.Temperature => temperature;
        int INpcConfiguration.MaxTokens => maxTokens;
        bool INpcConfiguration.AllowInterruption => allowInterruption;
        float INpcConfiguration.InterruptionPersistenceTime => persistenceTime;
        bool INpcConfiguration.IsActive => IsActive;
        bool INpcConfiguration.IsTalking => IsTalking;

        /// <summary>
        /// Fired when this NPC starts listening for user input
        /// </summary>
        public event Action OnStartListening;

        /// <summary>
        /// Fired when this NPC stops listening for user input
        /// </summary>
        public event Action OnStopListening;

        /// <summary>
        /// Fired when this NPC starts speaking (audio playback begins)
        /// </summary>
        public event Action OnStartSpeaking;

        /// <summary>
        /// Fired when this NPC stops speaking (audio playback ends)
        /// </summary>
        public event Action OnStopSpeaking;

        #endregion

        #region Property Overrides

        public override string NpcName => npcName;

        #endregion

        #region Public Properties

        /// <summary>
        /// System prompt that defines this NPC's personality and behavior
        /// </summary>
        public string SystemPrompt => systemPrompt;

        /// <summary>
        /// Voice ID for TTS
        /// </summary>
        public string VoiceId => voiceId;

        /// <summary>
        /// TTS model name
        /// </summary>
        public string TtsModel => ttsModel;

        /// <summary>
        /// TTS streaming mode ("sentence" or "batch")
        /// </summary>
        public string TtsStreamingMode => ttsStreamingMode;

        /// <summary>
        /// Voice playback speed
        /// </summary>
        public float TtsSpeed => ttsSpeed;

        /// <summary>
        /// LLM provider name
        /// </summary>
        public string LlmProvider => llmProvider;

        /// <summary>
        /// LLM model name
        /// </summary>
        public string LlmModel => llmModel;

        /// <summary>
        /// LLM response temperature
        /// </summary>
        public float Temperature => temperature;

        /// <summary>
        /// Maximum tokens in LLM response
        /// </summary>
        public int MaxTokens => maxTokens;

        /// <summary>
        /// Speech-to-Text provider name
        /// </summary>
        public string SttProvider => sttProvider;

        /// <summary>
        /// Language code for STT recognition
        /// </summary>
        public string Language => language;

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
        /// Sets all NPC parameters in a single call. Only non-null values are applied.
        /// </summary>
        public void Configure(
            string npcName = null,
            string systemPrompt = null,
            string voiceId = null,
            string ttsModel = null,
            string ttsStreamingMode = null,
            float? ttsSpeed = null,
            string llmProvider = null,
            string llmModel = null,
            float? temperature = null,
            int? maxTokens = null,
            string sttProvider = null,
            string language = null,
            bool? allowInterruption = null,
            float? persistenceTime = null)
        {
            if (!string.IsNullOrEmpty(npcName)) this.npcName = npcName;
            if (systemPrompt != null) this.systemPrompt = systemPrompt;
            if (!string.IsNullOrEmpty(voiceId)) this.voiceId = voiceId;
            if (!string.IsNullOrEmpty(ttsModel)) this.ttsModel = ttsModel;
            if (!string.IsNullOrEmpty(ttsStreamingMode)) this.ttsStreamingMode = ttsStreamingMode;
            if (ttsSpeed.HasValue) this.ttsSpeed = ttsSpeed.Value;
            if (!string.IsNullOrEmpty(llmProvider)) this.llmProvider = llmProvider;
            if (!string.IsNullOrEmpty(llmModel)) this.llmModel = llmModel;
            if (temperature.HasValue) this.temperature = temperature.Value;
            if (maxTokens.HasValue) this.maxTokens = maxTokens.Value;
            if (!string.IsNullOrEmpty(sttProvider)) this.sttProvider = sttProvider;
            if (!string.IsNullOrEmpty(language)) this.language = language;
            if (allowInterruption.HasValue) this.allowInterruption = allowInterruption.Value;
            if (persistenceTime.HasValue) this.persistenceTime = persistenceTime.Value;

            LogDebug($"Configured NPC: {this.npcName} (LLM: {this.llmProvider}/{this.llmModel}, STT: {this.sttProvider}, TTS: {this.ttsModel})");
        }

        #endregion

        #region Lifecycle

        protected override void Start()
        {
            base.Start();

            // Bridge NpcClientBase audio events to INpcConfiguration speaking events
            OnAudioStarted += () => OnStartSpeaking?.Invoke();
            OnAudioStopped += () => OnStopSpeaking?.Invoke();
        }

        /// <summary>
        /// Notify that this NPC started listening (call from your input controller)
        /// </summary>
        public void NotifyStartListening()
        {
            IsListening = true;
            OnStartListening?.Invoke();
        }

        /// <summary>
        /// Notify that this NPC stopped listening (call from your input controller)
        /// </summary>
        public void NotifyStopListening()
        {
            IsListening = false;
            OnStopListening?.Invoke();
        }

        #endregion

        #region IConversationHistory Override

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
        /// Creates a fully configured ConversationContext with all parameters from Inspector settings.
        /// </summary>
        /// <param name="systemPromptOverride">Optional system prompt override. If null, uses the Inspector-configured system prompt</param>
        /// <returns>A ConversationContext configured with this NPC's settings</returns>
        public ConversationContext GetConversationContext(string systemPromptOverride = null)
        {
            var context = new ConversationContext
            {
                systemPrompt = systemPromptOverride ?? systemPrompt,
                messages = GetApiHistoryAsChatMessages() ?? new List<ChatMessage>(),
                voiceId = voiceId,
                ttsModel = ttsModel,
                ttsStreamingMode = ttsStreamingMode,
                voiceSpeed = ttsSpeed,
                llmProvider = llmProvider,
                llmModel = llmModel,
                temperature = temperature,
                maxTokens = maxTokens,
                sttProvider = sttProvider,
                language = language,
            };

            return context;
        }

        #endregion

        #region Protected Methods

        protected override void ValidateConfiguration()
        {
            if (string.IsNullOrEmpty(npcName))
                Debug.LogWarning($"[SimpleNpcClient] No NPC name configured for {gameObject.name}");

            if (string.IsNullOrEmpty(voiceId) || voiceId == "default")
                Debug.LogWarning($"[SimpleNpcClient] Voice ID is '{voiceId}' for {gameObject.name} - set a valid ElevenLabs voice ID for TTS");

            if (string.IsNullOrEmpty(llmProvider))
                Debug.LogWarning($"[SimpleNpcClient] No LLM provider configured for {gameObject.name}");

            if (string.IsNullOrEmpty(llmModel))
                Debug.LogWarning($"[SimpleNpcClient] No LLM model configured for {gameObject.name}");

            if (string.IsNullOrEmpty(systemPrompt))
                Debug.LogWarning($"[SimpleNpcClient] No system prompt configured for {gameObject.name} - NPC will have no personality");

            Debug.Log($"[SimpleNpcClient] Initialized: {npcName}, LLM: {llmProvider}/{llmModel}, STT: {sttProvider}, TTS: {ttsModel}, Voice: {voiceId}, Streaming: {ttsStreamingMode}");
        }

        #endregion
    }
}
