using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using Tsc.AIBridge.Messages;
using Tsc.AIBridge.WebSocket;

namespace Tsc.AIBridge.Services
{
    /// <summary>
    /// Analysis service that uses WebSocket for simplicity.
    ///
    /// SIMPLICITY: Analysis over WebSocket is the simplest approach:
    /// - WebSocket connection already exists and is authenticated
    /// - No need for separate REST endpoints or JWT handling
    /// - Request/response pattern works perfectly over WebSocket
    /// - Backend handles it like any other WebSocket message
    ///
    /// This follows the principle: Use what's already there!
    ///
    /// NOTE: This service has NO external dependencies (RuleSystem, Training, etc.)
    /// and therefore belongs in the base AIBridge package for 3rd party use.
    ///
    /// ARCHITECTURE: Pure C# singleton using async/await - no MonoBehaviour overhead.
    /// Uses TaskCompletionSource for event-driven async patterns.
    /// Supports multiple concurrent analysis requests via ConcurrentDictionary.
    /// </summary>
    public class AnalysisService : INpcMessageHandler
    {
        private static AnalysisService _instance;

        /// <summary>
        /// Thread-safe dictionary to track multiple concurrent analysis requests.
        /// Key: RequestId, Value: TaskCompletionSource for that request.
        /// </summary>
        private readonly ConcurrentDictionary<string, TaskCompletionSource<AnalysisResponse>> _pendingRequests = new();

        // Dependency injection for testability - public for test assembly access
        public IWebSocketClientAdapter WebSocketAdapter { get; set; }

        /// <summary>
        /// Get singleton instance
        /// </summary>
        public static AnalysisService Instance
        {
            get
            {
                _instance ??= new AnalysisService();
                return _instance;
            }
        }

        private AnalysisService()
        {
            // Private constructor for singleton pattern
            // Default to production WebSocketClient adapter
            WebSocketAdapter = new WebSocketClientProductionAdapter();
        }

        /// <summary>
        /// Request analysis via WebSocket - simplest approach.
        /// Supports multiple concurrent requests.
        /// </summary>
        /// <param name="messages">Chat history messages (system message should be first message with role="system" if needed)</param>
        /// <param name="llmProvider">LLM provider to use (e.g., "openai", "vertexai")</param>
        /// <param name="llmModel">LLM model to use</param>
        /// <param name="temperature">Temperature for response generation</param>
        /// <param name="maxTokens">Maximum tokens for response</param>
        /// <param name="responseFormat">Response format - use "json_object" for clean JSON without markdown (OpenAI/Azure OpenAI only)</param>
        /// <param name="location">Google Cloud region for Vertex AI (e.g., "europe-west4") - optional, fallback to backend env var</param>
        /// <returns>Task that completes with AnalysisResponse when analysis is done</returns>
        /// <exception cref="InvalidOperationException">Thrown when WebSocket is not connected</exception>
        /// <exception cref="ArgumentException">Thrown when messages array is null or empty</exception>
        /// <exception cref="TimeoutException">Thrown when request times out after 30 seconds</exception>
        public async Task<AnalysisResponse> RequestAnalysisAsync(
            List<ChatMessage> messages,
            string llmProvider,
            string llmModel,
            float temperature,
            int maxTokens,
            string responseFormat,
            string location = null)
        {
            // Check WebSocket connection
            if (!WebSocketAdapter.IsConnected)
            {
                Debug.LogError($"[AnalysisService] WebSocket not connected");
                throw new InvalidOperationException("WebSocket not connected - ensure connection is established first");
            }

            // Validate messages array has at least one message
            if (messages == null || messages.Count == 0)
            {
                throw new ArgumentException("At least one message is required for analysis");
            }

            // Create request with unique ID
            var requestId = Guid.NewGuid().ToString();

            var request = new AnalysisRequestMessage
            {
                RequestId = requestId,
                Context = new ConversationContext
                {
                    // Messages array contains everything, including system message if present
                    messages = messages,
                    llmProvider = llmProvider,
                    llmModel = llmModel,
                    temperature = temperature,
                    maxTokens = maxTokens,
                    responseFormat = responseFormat,
                    location = location,
                    // Language and VoiceId are optional - backend provides defaults
                    language = null,
                    voiceId = null,
                    // TTS/STT fields not needed for analysis
                    systemPrompt = null,
                    ttsStreamingMode = null,
                    ttsModel = null,
                    sttProvider = null
                }
            };

            // Create TaskCompletionSource for this specific request
            var tcs = new TaskCompletionSource<AnalysisResponse>();

            // Add to pending requests dictionary
            if (!_pendingRequests.TryAdd(requestId, tcs))
            {
                // Should never happen with GUIDs, but handle gracefully
                throw new InvalidOperationException($"Duplicate RequestId generated: {requestId}");
            }

            // Register as handler for this RequestId
            WebSocketAdapter.RegisterNpc(requestId, this);

            try
            {
                // Send request via WebSocket
                await WebSocketAdapter.SendAnalysisRequestAsync(request);

                // Wait for response with 30 second timeout
                var timeoutTask = Task.Delay(30000);
                var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    Debug.LogError($"[AnalysisService] Analysis request timed out after 30 seconds (RequestId: {requestId})");
                    throw new TimeoutException($"Analysis request timed out after 30 seconds");
                }

                // Return the successful result
                return await tcs.Task;
            }
            finally
            {
                // Always cleanup: unregister handler and remove from pending
                WebSocketAdapter.UnregisterNpc(requestId);
                _pendingRequests.TryRemove(requestId, out _);
            }
        }

        /// <summary>
        /// NpcId is not used for routing in this service since we track multiple requests.
        /// The WebSocketClient routes by RequestId which we register per-request.
        /// </summary>
        public string NpcId => "AnalysisService";

        public void OnTextMessage(string json)
        {
            try
            {
                // Quick check if JSON contains "type" field (optimization before full parse)
                if (!json.Contains("\"type\"", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                // Parse to check actual type value
                var message = Newtonsoft.Json.JsonConvert.DeserializeObject<AnalysisResponseMessage>(json);

                // Check if it's actually an analysis response
                if (message == null ||
                    !string.Equals(message.Type, "analysisresponse", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                // Find the TaskCompletionSource for this RequestId
                if (_pendingRequests.TryGetValue(message.RequestId, out var tcs))
                {
                    // Convert llmResponse object to JObject if needed
                    Newtonsoft.Json.Linq.JObject llmResponseJObject = null;
                    if (message.LlmResponse != null)
                    {
                        llmResponseJObject = message.LlmResponse as Newtonsoft.Json.Linq.JObject;
                        if (llmResponseJObject == null)
                        {
                            // If not already JObject, serialize and deserialize to convert
                            var jsonString = Newtonsoft.Json.JsonConvert.SerializeObject(message.LlmResponse);
                            llmResponseJObject = Newtonsoft.Json.Linq.JObject.Parse(jsonString);
                        }
                    }

                    var response = new AnalysisResponse
                    {
                        analysis = message.Analysis,
                        llmResponse = llmResponseJObject,
                        metadata = message.Metadata,
                        timing = message.Timing
                    };

                    // Complete the task with the result
                    tcs.TrySetResult(response);
                }
                else
                {
                    // This can happen if the request timed out and was cleaned up before the response arrived
                    Debug.LogWarning($"[AnalysisService] Received response for unknown RequestId: {message.RequestId} (request may have timed out)");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AnalysisService] Failed to parse response: {ex.Message}\nStack trace: {ex.StackTrace}");
            }
        }

        public void OnBinaryMessage(byte[] data)
        {
            // Analysis doesn't use binary messages
        }

        public void OnRequestComplete(string requestId)
        {
            // Request is complete - analysis response should have been received by now
        }

        /// <summary>
        /// Analysis response matching backend model
        /// </summary>
        [Serializable]
        public class AnalysisResponse
        {
            public string analysis;
            public Newtonsoft.Json.Linq.JObject llmResponse;
            public object metadata;
            public object timing;
        }
    }

    /// <summary>
    /// Adapter interface for WebSocketClient to enable testing.
    /// Public for test assembly access.
    /// </summary>
    public interface IWebSocketClientAdapter
    {
        bool IsConnected { get; }
        void RegisterNpc(string requestId, INpcMessageHandler handler);
        void UnregisterNpc(string requestId);
        Task SendAnalysisRequestAsync(AnalysisRequestMessage message);
    }

    /// <summary>
    /// Production adapter that wraps WebSocketClient.Instance.
    /// Public for test assembly access to reset adapter in TearDown.
    /// </summary>
    public class WebSocketClientProductionAdapter : IWebSocketClientAdapter
    {
        public bool IsConnected => WebSocketClient.Instance != null && WebSocketClient.Instance.IsConnected;

        public void RegisterNpc(string requestId, INpcMessageHandler handler)
        {
            WebSocketClient.Instance?.RegisterNpc(requestId, handler);
        }

        public void UnregisterNpc(string requestId)
        {
            WebSocketClient.Instance?.UnregisterNpc(requestId);
        }

        public Task SendAnalysisRequestAsync(AnalysisRequestMessage message)
        {
            return WebSocketClient.Instance?.SendAnalysisRequestAsync(message) ?? Task.CompletedTask;
        }
    }
}
