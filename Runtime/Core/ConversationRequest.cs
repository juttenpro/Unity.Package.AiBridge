using System.Collections.Generic;

namespace Tsc.AIBridge.Core
{
    /// <summary>
    /// Complete conversation request containing all settings determined by RuleSystem.
    /// Created when player starts talking (PTT) or when NPC initiates conversation.
    /// </summary>
    public class ConversationRequest
    {
        // NPC identification
        public string NpcId { get; set; }

        // Conversation content (determined by RuleSystem)
        // Messages array includes system prompt as first message with role="system"
        public List<Messages.ChatMessage> Messages { get; set; }

        // Conversation type
        public bool IsNpcInitiated { get; set; } = false; // If true, skip STT and use TextInput flow

        // API Configuration (determined by RuleSystem per conversation)
        public string SttProvider { get; set; } = "google";
        public string LlmProvider { get; set; } = "openai";  // vertexai, openai, azure-openai
        public string LlmModel { get; set; } = "gpt-4o-mini";
        public string TtsProvider { get; set; } = "elevenlabs";
        public string TtsModel { get; set; } = "eleven_turbo_v2_5";
        public string Language { get; set; } = "nl-NL";
        public int MaxTokens { get; set; } = 500;
        public float Temperature { get; set; } = 0.7f;
        public float TopP { get; set; } = 1.0f;
        public float TopK { get; set; } = 0f;
        public float FrequencyPenalty { get; set; } = 0f;
        public float PresencePenalty { get; set; } = 0f;
        public string VoiceId { get; set; }  // TTS voice identifier
        public string TtsStreamingMode { get; set; } = "batch";
        public float TtsStability { get; set; } = 0.5f;
        public float TtsSimilarityBoost { get; set; } = 0.75f;
        public bool TtsSpeakerBoost { get; set; } = true;
        public float TtsStyle { get; set; } = 0f;
        public float TtsSpeed { get; set; } = 1.0f;

        /// <summary>
        /// Optional ISO 639-1 language code to force TTS pronunciation (e.g., "nl", "en", "de").
        /// When set, ElevenLabs uses this language instead of auto-detecting.
        /// Prevents accent drift (e.g., Flemish instead of Dutch).
        /// When null/empty, ElevenLabs auto-detects the language.
        /// </summary>
        public string TtsLanguageCode { get; set; }

        public bool AllowInterruption { get; set; } = true;
        public float InterruptionPersistenceTime { get; set; } = 1.5f;

        #region Context Caching (Gemini Cost Optimization)

        /// <summary>
        /// Full Gemini cached content resource name.
        /// Format: "projects/{project}/locations/{location}/cachedContents/{id}"
        /// Obtained from ContextCacheManager after calling EnsureCache().
        /// When provided, VertexAIService uses this cache for 75% cost reduction on cached tokens.
        /// Note: When using cache, system prompt should NOT be included in Messages (it's in the cache).
        /// </summary>
        public string ContextCacheName { get; set; }

        #endregion
    }
}