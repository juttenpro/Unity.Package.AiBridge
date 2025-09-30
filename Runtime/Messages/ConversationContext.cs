using System;
using System.Collections.Generic;
using UnityEngine;

namespace Tsc.AIBridge.Messages
{
    /// <summary>
    /// Unified context for all conversation-related parameters.
    /// Client-driven configuration - Unity determines all values.
    /// Replaces Dictionary<string,string> for type safety.
    /// </summary>
    [Serializable]
    public class ConversationContext
    {
        /// <summary>
        /// System prompt that defines the AI's behavior and role
        /// </summary>
        [TextArea(3, 10)]
        public string systemPrompt;

        /// <summary>
        /// Complete chat history including previous messages
        /// </summary>
        public List<ChatMessage> messages;

        /// <summary>
        /// Voice ID for text-to-speech generation
        /// </summary>
        public string voiceId;

        /// <summary>
        /// LLM model to use (e.g., "gpt-4", "gpt-4o-mini", "gemini-pro")
        /// </summary>
        public string llmModel;

        /// <summary>
        /// LLM provider (e.g., "openai", "azure-openai", "vertexai")
        /// </summary>
        public string llmProvider;

        /// <summary>
        /// Temperature for LLM response generation (0.0-1.0)
        /// </summary>
        [Range(0f, 2f)]
        public float temperature;

        /// <summary>
        /// Maximum tokens for LLM response
        /// </summary>
        [Min(1)]
        public int maxTokens;

        /// <summary>
        /// Language for the conversation (e.g., "nl-NL", "en-US")
        /// </summary>
        public string language;

        /// <summary>
        /// TTS streaming mode ("batch" or "sentence")
        /// </summary>
        public string ttsStreamingMode;

        /// <summary>
        /// TTS model to use
        /// </summary>
        public string ttsModel;

        /// <summary>
        /// Speech-to-text provider ("google", "azure")
        /// </summary>
        public string sttProvider;

        // REMOVED: audioFormat, sampleRate, opusBitrate
        // These are misleading - API always uses opus_48000_64 hardcoded
        // ElevenLabs supports different bitrates but API doesn't expose this



        /// <summary>
        /// Merge with RequestOrchestrator settings (LLM configuration).
        /// Applies orchestrator-level settings to this context, overriding existing values.
        /// </summary>
        /// <param name="llmProvider">The LLM provider to use (e.g., "openai", "vertexai")</param>
        /// <param name="llmModel">The specific LLM model (e.g., "gpt-4o-mini", "gemini-pro")</param>
        /// <param name="temperature">Optional temperature override for response generation (0.0-2.0)</param>
        /// <param name="maxTokens">Optional maximum token count override</param>
        /// <param name="language">Optional language override (e.g., "nl-NL", "en-US")</param>
        public void ApplyOrchestratorSettings(
            string llmProvider,
            string llmModel,
            float? temperature = null,
            int? maxTokens = null,
            string language = null)
        {
            this.llmProvider = llmProvider;
            this.llmModel = llmModel;

            if (temperature.HasValue)
                this.temperature = temperature.Value;

            if (maxTokens.HasValue)
                this.maxTokens = maxTokens.Value;

            if (!string.IsNullOrEmpty(language))
                this.language = language;
        }
    }
}