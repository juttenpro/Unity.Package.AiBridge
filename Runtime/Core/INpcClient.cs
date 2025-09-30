namespace Tsc.AIBridge.Core
{
    /// <summary>
    /// Interface for NPC client implementations.
    /// Defines the contract for NPC clients that handle conversation and audio playback.
    /// </summary>
    public interface INpcClient
    {
        /// <summary>
        /// Check if the NPC is currently speaking
        /// </summary>
        bool IsSpeaking { get; }

        /// <summary>
        /// Check if the NPC is currently listening
        /// </summary>
        bool IsListening { get; }

        /// <summary>
        /// Get the NPC's unique identifier
        /// </summary>
        string NpcId { get; }

        /// <summary>
        /// Get the NPC's display name
        /// </summary>
        string NpcName { get; }

        // History methods removed - now optional via IConversationHistory interface
        // This allows flexibility: RuleSystem-based implementations provide messages directly,
        // while third-party implementations can optionally implement IConversationHistory

        /// <summary>
        /// Stop any ongoing audio playback
        /// </summary>
        void StopAudio();

        /// <summary>
        /// Pause audio playback
        /// </summary>
        void PauseAudio();

        /// <summary>
        /// Resume audio playback
        /// </summary>
        void ResumeAudio();
    }
}