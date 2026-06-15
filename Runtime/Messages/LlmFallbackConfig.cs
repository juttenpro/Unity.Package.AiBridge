using Newtonsoft.Json;

namespace Tsc.AIBridge.Messages
{
    /// <summary>
    /// Optional per-template reactive dialogue-LLM fallback target, carried on
    /// <see cref="SessionStartMessage.LlmFallback"/> to the backend. When the primary LLM
    /// produces no first token in time, the backend retries the turn with this provider/model
    /// and its OWN sampling knobs — a different provider must not inherit the primary's params.
    ///
    /// Null on SessionStartMessage = no fallback configured: the backend wraps nothing and a
    /// stalled primary degrades gracefully (unchanged behaviour). The system prompt + chat
    /// history are reused from the primary, so only these LLM knobs travel over the wire.
    ///
    /// Field names mirror the ApiOrchestrator backend's LlmFallbackConfig wire contract exactly.
    /// Set per AI API Template; replaces the previous global appsettings fallback that leaked
    /// across tenants.
    /// </summary>
    public class LlmFallbackConfig
    {
        /// <summary>Fallback provider ("vertexai", "openai", "azure-openai", "mistral"). Required.</summary>
        [JsonProperty("provider")]
        public string Provider { get; set; }

        /// <summary>Fallback model / Azure deployment name. Required.</summary>
        [JsonProperty("model")]
        public string Model { get; set; }

        /// <summary>Sampling temperature. Omitted when unset → backend provider default.</summary>
        [JsonProperty("temperature", NullValueHandling = NullValueHandling.Ignore)]
        public float? Temperature { get; set; }

        /// <summary>Nucleus sampling. Omitted when unset.</summary>
        [JsonProperty("topP", NullValueHandling = NullValueHandling.Ignore)]
        public float? TopP { get; set; }

        /// <summary>OpenAI/Azure frequency penalty. Ignored by non-OpenAI providers.</summary>
        [JsonProperty("frequencyPenalty", NullValueHandling = NullValueHandling.Ignore)]
        public float? FrequencyPenalty { get; set; }

        /// <summary>OpenAI/Azure presence penalty. Ignored by non-OpenAI providers.</summary>
        [JsonProperty("presencePenalty", NullValueHandling = NullValueHandling.Ignore)]
        public float? PresencePenalty { get; set; }

        /// <summary>Max output tokens. Omitted when unset → backend provider default.</summary>
        [JsonProperty("maxTokens", NullValueHandling = NullValueHandling.Ignore)]
        public int? MaxTokens { get; set; }

        /// <summary>Reasoning-token budget for a thinking-capable Gemini fallback. Vertex AI only.</summary>
        [JsonProperty("thinkingBudget", NullValueHandling = NullValueHandling.Ignore)]
        public int? ThinkingBudget { get; set; }

        /// <summary>Google Cloud region for a Vertex AI fallback. Omitted → backend GOOGLE_LOCATION env.</summary>
        [JsonProperty("location", NullValueHandling = NullValueHandling.Ignore)]
        public string Location { get; set; }

        /// <summary>"json_object" for clean-JSON output (OpenAI/Azure/Vertex). Omitted → provider default.</summary>
        [JsonProperty("responseFormat", NullValueHandling = NullValueHandling.Ignore)]
        public string ResponseFormat { get; set; }
    }
}
