using System.Collections.Generic;
using SimulationCrew.AIBridge.Messages;

namespace SimulationCrew.AIBridge.Core
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

        /// <summary>
        /// Get the conversation history as chat messages for API
        /// </summary>
        List<ChatMessage> GetApiHistoryAsChatMessages();

        /// <summary>
        /// Clear the conversation history
        /// </summary>
        void ClearHistory();

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