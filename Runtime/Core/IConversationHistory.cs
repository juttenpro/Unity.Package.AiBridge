using System.Collections.Generic;
using Tsc.AIBridge.Messages;

namespace Tsc.AIBridge.Core
{
    /// <summary>
    /// Optional interface for NPCs that support conversation history.
    /// This is NOT required for basic NPC functionality.
    /// Third-party implementations can use this for history management.
    /// Main RuleSystem-based implementations will provide messages directly.
    /// </summary>
    public interface IConversationHistory
    {
        /// <summary>
        /// Get the conversation history as chat messages for API.
        /// Used by third-party implementations that manage their own history.
        /// </summary>
        /// <returns>List of chat messages representing the conversation history</returns>
        List<ChatMessage> GetApiHistoryAsChatMessages();

        /// <summary>
        /// Clear the conversation history.
        /// Used by third-party implementations that need to reset state.
        /// </summary>
        void ClearHistory();

        /// <summary>
        /// Add a player message to the conversation history.
        /// Used to track what the player said.
        /// </summary>
        /// <param name="message">The player's message to add to history</param>
        void AddPlayerMessage(string message);
    }
}