using System;
using System.Collections.Generic;
using Newtonsoft.Json;
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
        [JsonProperty("systemPrompt")]
        public string systemPrompt;

        /// <summary>
        /// Complete chat history including previous messages
        /// </summary>
        [JsonProperty("messages")]
        public List<ChatMessage> messages;

        /// <summary>
        /// Voice ID for text-to-speech generation
        /// </summary>
        [JsonProperty("voiceId")]
        public string voiceId;

        /// <summary>
        /// LLM model to use (e.g., "gpt-4", "gpt-4o-mini", "gemini-pro")
        /// </summary>
        [JsonProperty("llmModel")]
        public string llmModel;

        /// <summary>
        /// LLM provider (e.g., "openai", "azure-openai", "vertexai")
        /// </summary>
        [JsonProperty("llmProvider")]
        public string llmProvider;

        /// <summary>
        /// Temperature for LLM response generation (0.0-1.0)
        /// </summary>
        [Range(0f, 2f)]
        [JsonProperty("temperature")]
        public float temperature;

        /// <summary>
        /// Maximum tokens for LLM response
        /// </summary>
        [Min(1)]
        [JsonProperty("maxTokens")]
        public int maxTokens;

        /// <summary>
        /// Language for the conversation (e.g., "nl-NL", "en-US")
        /// </summary>
        [JsonProperty("language")]
        public string language;

        /// <summary>
        /// TTS streaming mode ("batch" or "sentence")
        /// </summary>
        [JsonProperty("ttsStreamingMode")]
        public string ttsStreamingMode;

        /// <summary>
        /// TTS model to use
        /// </summary>
        [JsonProperty("ttsModel")]
        public string ttsModel;

        /// <summary>
        /// Speech-to-text provider ("google", "azure")
        /// </summary>
        [JsonProperty("sttProvider")]
        public string sttProvider;

        /// <summary>
        /// TTS provider ("elevenlabs", "voxtral", "cartesia")
        /// </summary>
        [JsonProperty("ttsProvider")]
        public string ttsProvider;

        #region ElevenLabs Voice Settings

        /// <summary>
        /// ElevenLabs voice stability (0.0 to 1.0) - controls consistency.
        /// Lower values allow more expressive/variable speech.
        /// </summary>
        [Range(0f, 1f)]
        [JsonProperty("voiceStability")]
        public float? voiceStability;

        /// <summary>
        /// ElevenLabs voice similarity boost (0.0 to 1.0) - controls voice matching.
        /// Higher values make the voice more similar to the original.
        /// </summary>
        [Range(0f, 1f)]
        [JsonProperty("voiceSimilarityBoost")]
        public float? voiceSimilarityBoost;

        /// <summary>
        /// ElevenLabs voice style exaggeration (0.0 to 1.0) - for newer models.
        /// Controls the expressiveness and style of the speech.
        /// </summary>
        [Range(0f, 1f)]
        [JsonProperty("voiceStyle")]
        public float? voiceStyle;

        /// <summary>
        /// ElevenLabs speaker boost - enhances clarity and presence.
        /// </summary>
        [JsonProperty("voiceUseSpeakerBoost")]
        public bool? voiceUseSpeakerBoost;

        /// <summary>
        /// ElevenLabs voice speed (0.7 to 1.2) - controls speech rate.
        /// Default 1.0 is normal speed, lower is slower, higher is faster.
        /// </summary>
        [Range(0.7f, 1.2f)]
        [JsonProperty("voiceSpeed")]
        public float? voiceSpeed;

        /// <summary>
        /// Optional ISO 639-1 language code to force TTS pronunciation (e.g., "nl", "en", "de").
        /// When set, ElevenLabs uses this language instead of auto-detecting.
        /// Prevents accent drift (e.g., Flemish instead of Dutch).
        /// When null/empty, ElevenLabs auto-detects the language.
        /// </summary>
        [JsonProperty("ttsLanguageCode")]
        public string ttsLanguageCode;

        #endregion

        /// <summary>
        /// Response format for LLM output (OpenAI-compatible providers only).
        /// Use "json_object" to request clean JSON without markdown code blocks.
        /// Null = use LLM default format (typically text with potential markdown).
        /// Only applied for OpenAI and Azure OpenAI providers.
        /// </summary>
        [JsonProperty("responseFormat")]
        public string responseFormat;

        /// <summary>
        /// Google Cloud region for Vertex AI requests (e.g., "europe-west4", "us-central1").
        /// Only used for Vertex AI provider.
        /// Null = use GOOGLE_LOCATION environment variable as fallback.
        /// </summary>
        [JsonProperty("location")]
        public string location;

        #region Context Caching (Gemini Cost Optimization)

        /// <summary>
        /// Full Gemini cached content resource name.
        /// Format: "projects/{project}/locations/{location}/cachedContents/{id}"
        /// Obtained from POST /api/cache/ensure endpoint.
        /// When provided, VertexAIService uses this cache for 75% cost reduction.
        /// </summary>
        [JsonProperty("contextCacheName")]
        public string contextCacheName;

        #endregion

        /// <summary>
        /// Optional per-character base emotion (grondtoon) for TTS voice modulation.
        /// Cartesia-only; silently ignored by ElevenLabs and Voxtral. The per-sentence
        /// [EMOTION:x] marker from the LLM overrides this value, except when the LLM
        /// emits "neutral" (treated as a non-signal so BaseEmotion keeps flowing).
        /// Null = no base emotion; Cartesia falls back to its own default.
        /// </summary>
        [JsonProperty("baseEmotion")]
        public string baseEmotion;

        /// <summary>
        /// Anonymous observability correlation IDs, carried by TextInputMessage and
        /// AnalysisRequestMessage via this context object. See <see cref="ObservabilityContext"/>.
        /// </summary>
        [JsonProperty("observability")]
        public ObservabilityContext observability;

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