using System;
using System.Collections;
using System.Collections.Generic;
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
    /// </summary>
    public class AnalysisService : MonoBehaviour, INpcMessageHandler
    {
        private static AnalysisService _instance;
        private AnalysisResponse _pendingResponse;
        private string _pendingRequestId;
        private Action<string> _pendingErrorCallback;

        /// <summary>
        /// Get or create singleton instance
        /// </summary>
        public static AnalysisService Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("[AnalysisService]");
                    _instance = go.AddComponent<AnalysisService>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
        }

        /// <summary>
        /// Request analysis via WebSocket - simplest approach.
        /// </summary>
        /// <param name="messages">Chat history messages (system message should be first message with role="system" if needed)</param>
        /// <param name="llmProvider">LLM provider to use (e.g., "openai", "vertexai")</param>
        /// <param name="llmModel">LLM model to use</param>
        /// <param name="temperature">Temperature for response generation</param>
        /// <param name="maxTokens">Maximum tokens for response</param>
        /// <param name="onSuccess">Callback when analysis completes</param>
        /// <param name="onError">Callback on error</param>
        public IEnumerator RequestAnalysis(
            List<ChatMessage> messages,
            string llmProvider,
            string llmModel,
            float temperature,
            int maxTokens,
            Action<AnalysisResponse> onSuccess,
            Action<string> onError)
        {
            // Check WebSocket connection
            if (WebSocketClient.Instance == null || !WebSocketClient.Instance.IsConnected)
            {
                Debug.LogError($"[AnalysisService] WebSocket not connected - Instance null: {WebSocketClient.Instance == null}, Connected: {WebSocketClient.Instance?.IsConnected ?? false}");
                onError?.Invoke("WebSocket not connected - ensure connection is established first");
                yield break;
            }

            // Create request
            var requestId = Guid.NewGuid().ToString();
            Debug.Log($"[AnalysisService] Creating analysis request with ID: {requestId}");

            // Validate messages array has at least one message
            if (messages == null || messages.Count == 0)
            {
                onError?.Invoke("At least one message is required for analysis");
                yield break;
            }

            // LOG: Show all messages being sent to API for debugging
            Debug.Log($"[AnalysisService] === ANALYSIS REQUEST MESSAGES ({messages.Count} total) ===");
            for (int i = 0; i < messages.Count; i++)
            {
                var msg = messages[i];
                var preview = msg.Content.Length > 150 ? msg.Content.Substring(0, 150) + "..." : msg.Content;
                Debug.Log($"[AnalysisService] Message {i}: Role='{msg.Role}', Content='{preview}'");
            }
            Debug.Log($"[AnalysisService] === END MESSAGES ===");

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

            // Store callbacks
            _pendingRequestId = requestId;
            _pendingErrorCallback = onError;
            _pendingResponse = null;

            // Register as handler for this RequestId
            Debug.Log($"[AnalysisService] Registering as handler for RequestId: {requestId}");
            WebSocketClient.Instance.RegisterNpc(requestId, this);

            // Send request via WebSocket
            Debug.Log($"[AnalysisService] Sending analysis request to backend - LLM: {llmProvider}/{llmModel}, Temp: {temperature}, MaxTokens: {maxTokens}");
            _ = WebSocketClient.Instance.SendAnalysisRequestAsync(request);
            Debug.Log($"[AnalysisService] Analysis request sent, waiting for response...");

            // Wait for response (with timeout)
            var timeout = 30f; // 30 second timeout
            var elapsed = 0f;

            while (_pendingResponse == null && elapsed < timeout)
            {
                yield return new WaitForSeconds(0.1f);
                elapsed += 0.1f;
            }

            // Unregister handler
            if (WebSocketClient.Instance != null)
            {
                WebSocketClient.Instance.UnregisterNpc(requestId);
            }

            // Handle result
            if (_pendingResponse != null)
            {
                onSuccess?.Invoke(_pendingResponse);
            }
            else
            {
                onError?.Invoke($"Analysis request timed out after {timeout} seconds");
            }

            // Clear state
            _pendingRequestId = null;
            _pendingErrorCallback = null;
            _pendingResponse = null;
        }

        public string NpcId => _pendingRequestId;

        public void OnTextMessage(string json)
        {
            try
            {
                Debug.Log($"[AnalysisService] OnTextMessage received: {(json.Length > 200 ? json.Substring(0, 200) + "..." : json)}");

                // Check if it's an analysis response
                if (!json.Contains("\"type\":\"analysisresponse\""))
                {
                    Debug.Log($"[AnalysisService] Message is not analysisresponse, ignoring");
                    return;
                }

                Debug.Log($"[AnalysisService] Parsing analysis response...");
                var message = Newtonsoft.Json.JsonConvert.DeserializeObject<AnalysisResponseMessage>(json);
                if (message != null && message.RequestId == _pendingRequestId)
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

                    _pendingResponse = new AnalysisResponse
                    {
                        analysis = message.Analysis,
                        llmResponse = llmResponseJObject,
                        metadata = message.Metadata,
                        timing = message.Timing
                    };

                    Debug.Log($"[AnalysisService] Analysis response received successfully for RequestId: {message.RequestId}");
                }
                else
                {
                    Debug.LogWarning($"[AnalysisService] Analysis response received but RequestId mismatch. Expected: {_pendingRequestId}, Got: {message?.RequestId}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AnalysisService] Failed to parse response: {ex.Message}\nStack trace: {ex.StackTrace}");
                _pendingErrorCallback?.Invoke($"Failed to parse response: {ex.Message}");
            }
        }

        public void OnBinaryMessage(byte[] data)
        {
            // Analysis doesn't use binary messages
        }

        public void OnRequestComplete(string requestId)
        {
            // Request is complete - analysis response should have been received by now
            if (requestId == _pendingRequestId)
            {
                Debug.Log($"[AnalysisService] Request {requestId} marked as complete");
            }
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
}
