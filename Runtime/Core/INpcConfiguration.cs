using System;

namespace Tsc.AIBridge.Core
{
    /// <summary>
    /// Configuration interface for NPC settings.
    /// Provides all necessary data for AI conversation without dependency on specific implementations.
    /// </summary>
    public interface INpcConfiguration
    {
        /// <summary>
        /// Unique identifier for this NPC
        /// </summary>
        string Id { get; }

        /// <summary>
        /// Display name of the NPC
        /// </summary>
        string Name { get; }

        /// <summary>
        /// System prompt that defines the NPC's personality and behavior
        /// </summary>
        string SystemPrompt { get; }

        /// <summary>
        /// Optional chat history/messages for the conversation.
        /// If null, the orchestrator will get history from the NPC client.
        /// </summary>
        System.Collections.Generic.List<Messages.ChatMessage> Messages { get; }

        /// <summary>
        /// TTS streaming mode (e.g., "batch", "streaming")
        /// </summary>
        string TtsStreamingMode { get; }

        /// <summary>
        /// TTS model to use (e.g., "eleven_turbo_v2_5")
        /// </summary>
        string TtsModel { get; }

        /// <summary>
        /// TTS voice ID
        /// </summary>
        string VoiceId { get; }

        /// <summary>
        /// Language code (e.g., "nl-NL", "en-US")
        /// </summary>
        string Language { get; }

        /// <summary>
        /// STT provider to use (e.g., "google", "azure", "openai")
        /// </summary>
        string SttProvider { get; }

        /// <summary>
        /// LLM provider to use (e.g., "vertexai", "openai", "azure-openai")
        /// </summary>
        string LlmProvider { get; }

        /// <summary>
        /// LLM model to use (e.g., "gpt-4o-mini", "gemini-1.5-flash")
        /// </summary>
        string LlmModel { get; }

        /// <summary>
        /// Temperature for LLM responses (0.0 - 1.0)
        /// </summary>
        float Temperature { get; }

        /// <summary>
        /// Maximum tokens for LLM response
        /// </summary>
        int MaxTokens { get; }

        // Interruption settings
        /// <summary>
        /// Whether this NPC can be interrupted during speech
        /// </summary>
        bool AllowInterruption { get; }

        /// <summary>
        /// How long user must speak to trigger interruption (seconds)
        /// </summary>
        float InterruptionPersistenceTime { get; }

        /// <summary>
        /// Whether this NPC is currently active/selected
        /// </summary>
        bool IsActive { get; }

        /// <summary>
        /// Whether this NPC is currently producing speech (not just "speaking" but actual audio)
        /// </summary>
        bool IsTalking { get; }

        // Animation events (optional)
        event Action OnStartListening;
        event Action OnStopListening;
        event Action OnStartSpeaking;
        event Action OnStopSpeaking;
    }

    /// <summary>
    /// Provider interface for retrieving NPC configurations and clients
    /// </summary>
    public interface INpcProvider
    {
        /// <summary>
        /// Get NPC configuration by ID
        /// </summary>
        INpcConfiguration GetNpcConfiguration(string npcId);

        /// <summary>
        /// Get NPC client instance by ID
        /// </summary>
        NpcClientBase GetNpcClient(string npcId);
    }
}