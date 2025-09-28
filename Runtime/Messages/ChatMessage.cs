using System;
using Newtonsoft.Json;

namespace Tsc.AIBridge.Messages
{
    /// <summary>
    /// Represents a single message in the chat history for LLM context.
    /// Used to maintain conversation state between client and server.
    /// Follows the standard chat format expected by most LLM APIs.
    /// </summary>
    [Serializable]
    public class ChatMessage
    {
        /// <summary>
        /// The role of the message sender.
        /// Valid values: "system", "user", "assistant"
        /// </summary>
        [JsonProperty("role")]
        public string Role;

        /// <summary>
        /// The text content of the message.
        /// For system messages: contains the prompt/instructions
        /// For user messages: contains the user's input/question
        /// For assistant messages: contains the AI's response
        /// </summary>
        [JsonProperty("content")]
        public string Content;

        /// <summary>
        /// Timestamp when the message was created.
        /// Used for maintaining chronological order in chat history.
        /// </summary>
        [JsonProperty("timestamp")]
        public float Time;
    }
}