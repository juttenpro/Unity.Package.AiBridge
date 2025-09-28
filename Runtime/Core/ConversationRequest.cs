using System.Collections.Generic;

namespace Tsc.AIBridge.Core
{
    /// <summary>
    /// Complete conversation request containing all settings determined by RuleSystem.
    /// Created when player starts talking (PTT) and contains everything needed for the conversation.
    /// </summary>
    public class ConversationRequest
    {
        // NPC identification
        public string NpcId { get; set; }

        // Conversation content (determined by RuleSystem)
        public string SystemPrompt { get; set; }
        public List<Tsc.AIBridge.Messages.ChatMessage> Messages { get; set; }

        // API Configuration (determined by RuleSystem per conversation)
        public string SttProvider { get; set; } = "google";
        public string LlmProvider { get; set; } = "openai";  // vertexai, openai, azure-openai
        public string LlmModel { get; set; } = "gpt-4o-mini";
        public string TtsModel { get; set; } = "eleven_turbo_v2_5";
        public string Language { get; set; } = "nl-NL";
        public int MaxTokens { get; set; } = 500;
        public float Temperature { get; set; } = 0.7f;

        // Voice settings (from PersonaSO)
        public string TtsVoice { get; set; }
        public string TtsStreamingMode { get; set; } = "batch";

        // Interruption settings (from PersonaSO)
        public bool AllowInterruption { get; set; } = true;
        public float InterruptionPersistenceTime { get; set; } = 1.5f;
    }
}