using System.Collections.Generic;
using Tsc.AIBridge.Messages;

namespace Tsc.AIBridge.Utils
{
    /// <summary>
    /// Performance-optimized helper for ChatMessage collections.
    /// Provides efficient creation and manipulation of ChatMessage lists/arrays.
    /// </summary>
    public static class ChatMessageHelper
    {
        /// <summary>
        /// Creates a ChatMessage list with pre-allocated capacity for better performance.
        /// </summary>
        /// <param name="capacity">Expected number of messages</param>
        /// <returns>Pre-allocated List&lt;ChatMessage&gt;</returns>
        public static List<ChatMessage> CreateList(int capacity = 4)
        {
            return new List<ChatMessage>(capacity);
        }

        /// <summary>
        /// Creates a ChatMessage list with system prompt pre-added.
        /// Optimized for common case of system prompt + conversation history.
        /// </summary>
        /// <param name="systemPrompt">System prompt content</param>
        /// <param name="expectedHistorySize">Expected conversation history size</param>
        /// <returns>List with system prompt added</returns>
        public static List<ChatMessage> CreateWithSystemPrompt(string systemPrompt, int expectedHistorySize = 0)
        {
            var messages = new List<ChatMessage>(expectedHistorySize + 1); // +1 for system prompt
            
            if (!string.IsNullOrEmpty(systemPrompt))
            {
                messages.Add(new ChatMessage
                {
                    Role = "system",
                    Content = systemPrompt
                });
            }
            
            return messages;
        }

        /// <summary>
        /// Creates a ChatMessage array from a list for performance-critical operations.
        /// Use this when you need array performance but have a List&lt;ChatMessage&gt;.
        /// </summary>
        /// <param name="messages">Source list</param>
        /// <returns>ChatMessage array</returns>
        public static ChatMessage[] ToArray(List<ChatMessage> messages)
        {
            if (messages == null || messages.Count == 0)
                return new ChatMessage[0];
                
            return messages.ToArray();
        }

        /// <summary>
        /// Creates a ChatMessage list from an array.
        /// Use this when you have an array but need List&lt;ChatMessage&gt; for serialization.
        /// </summary>
        /// <param name="messages">Source array</param>
        /// <returns>ChatMessage list</returns>
        public static List<ChatMessage> FromArray(ChatMessage[] messages)
        {
            if (messages == null || messages.Length == 0)
                return new List<ChatMessage>(0);
                
            return new List<ChatMessage>(messages);
        }

        /// <summary>
        /// Efficiently clones a ChatMessage list with pre-allocated capacity.
        /// </summary>
        /// <param name="source">Source list to clone</param>
        /// <returns>New list with same content</returns>
        public static List<ChatMessage> Clone(List<ChatMessage> source)
        {
            if (source == null)
                return new List<ChatMessage>(0);
                
            return new List<ChatMessage>(source);
        }

        /// <summary>
        /// Adds a user message to the list efficiently.
        /// </summary>
        /// <param name="messages">Target list</param>
        /// <param name="content">User message content</param>
        public static void AddUserMessage(List<ChatMessage> messages, string content)
        {
            if (messages != null && !string.IsNullOrEmpty(content))
            {
                messages.Add(new ChatMessage
                {
                    Role = "user",
                    Content = content
                });
            }
        }

        /// <summary>
        /// Adds an assistant message to the list efficiently.
        /// </summary>
        /// <param name="messages">Target list</param>
        /// <param name="content">Assistant message content</param>
        public static void AddAssistantMessage(List<ChatMessage> messages, string content)
        {
            if (messages != null && !string.IsNullOrEmpty(content))
            {
                messages.Add(new ChatMessage
                {
                    Role = "assistant",
                    Content = content
                });
            }
        }
    }
}
