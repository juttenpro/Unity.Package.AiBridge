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
        /// <param name="systemPrompt">The system prompt for analysis</param>
        /// <param name="messages">Chat history messages</param>
        /// <param name="llmProvider">LLM provider to use (e.g., "openai", "vertexai")</param>
        /// <param name="llmModel">LLM model to use</param>
        /// <param name="temperature">Temperature for response generation</param>
        /// <param name="maxTokens">Maximum tokens for response</param>
        /// <param name="onSuccess">Callback when analysis completes</param>
        /// <param name="onError">Callback on error</param>
        public IEnumerator RequestAnalysis(
            string systemPrompt,
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
                onError?.Invoke("WebSocket not connected - ensure connection is established first");
                yield break;
            }

            // Create request
            var requestId = Guid.NewGuid().ToString();
            var request = new AnalysisRequestMessage
            {
                RequestId = requestId,
                Context = new ConversationContext
                {
                    systemPrompt = systemPrompt,
                    messages = messages,
                    llmProvider = llmProvider,
                    llmModel = llmModel,
                    temperature = temperature,
                    maxTokens = maxTokens,
                    language = null,  // Not relevant for analysis - no TTS/STT involved
                    // TTS/STT fields not needed for analysis
                    voiceId = null,
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
            WebSocketClient.Instance.RegisterNpc(requestId, this);

            // Send request via WebSocket
            _ = WebSocketClient.Instance.SendAnalysisRequestAsync(request);

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
                // Check if it's an analysis response
                if (!json.Contains("\"type\":\"analysisresponse\""))
                    return;

                var message = Newtonsoft.Json.JsonConvert.DeserializeObject<AnalysisResponseMessage>(json);
                if (message != null && message.RequestId == _pendingRequestId)
                {
                    _pendingResponse = new AnalysisResponse
                    {
                        analysis = message.Analysis,
                        llmResponse = message.LlmResponse,
                        metadata = message.Metadata,
                        timing = message.Timing
                    };
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AnalysisService] Failed to parse response: {ex.Message}");
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
            public object llmResponse;
            public object metadata;
            public object timing;
        }
    }
}
