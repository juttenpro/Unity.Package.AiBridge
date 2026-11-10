using NUnit.Framework;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Tsc.AIBridge.Messages;
using Tsc.AIBridge.Services;
using Tsc.AIBridge.WebSocket;
using UnityEngine;
using UnityEngine.TestTools;

namespace Tsc.AIBridge.Tests.Runtime
{
    /// <summary>
    /// WHY: the AI API Template's LLM fallback must reach the backend for analysis too (Decisions on Azure EU:
    /// when the Decisions call fails, the backend retries on the fallback). AnalysisService builds the outgoing
    /// ConversationContext itself, so a fallback the caller passes and the service drops would never be used.
    /// Callers that pass nothing (older host code) must send no fallback, exactly as before.
    ///
    /// NOT COVERED: JSON keys (AnalysisLlmFallbackSerializationTests), the backend's retry.
    /// </summary>
    public class AnalysisServiceLlmFallbackTests
    {
        private CapturingMockAdapter _mock;

        [SetUp]
        public void SetUp()
        {
            _mock = new CapturingMockAdapter();
            _mock.SetConnected(true);
            AnalysisService.Instance.WebSocketAdapter = _mock;
        }

        [TearDown]
        public void TearDown()
        {
            AnalysisService.Instance.WebSocketAdapter = new WebSocketClientProductionAdapter();
        }

        [UnityTest]
        public IEnumerator RequestAnalysisAsync_WithFallback_PutsThatFallbackInTheContext()
        {
            var messages = new List<ChatMessage> { new ChatMessage { Role = "user", Content = "test" } };
            var fallback = new LlmFallbackConfig { Provider = "vertexai", Model = "gemini-2.5-flash-lite", MaxTokens = 640 };

            var task = AnalysisService.Instance.RequestAnalysisAsync(
                messages, "azure-decisions", "gpt-6-luna", 0.2f, 800, "json_object",
                azureResourceName: "DecisionsWestEurope", llmFallback: fallback);

            yield return new WaitUntil(() => _mock.CapturedRequest != null);

            Assert.That(_mock.CapturedRequest.Context.llmFallback, Is.SameAs(fallback),
                "the template's fallback was dropped by AnalysisService: a failed Decisions call would leave the analysis empty");

            SendAnalysisResponse(_mock.LastRegisteredRequestId);
            yield return new WaitUntil(() => task.IsCompleted);
        }

        [UnityTest]
        public IEnumerator RequestAnalysisAsync_WithoutFallback_SendsNone()
        {
            var messages = new List<ChatMessage> { new ChatMessage { Role = "user", Content = "test" } };

            var task = AnalysisService.Instance.RequestAnalysisAsync(
                messages, "openai", "gpt-4o-mini", 0.7f, 800, "json_object");

            yield return new WaitUntil(() => _mock.CapturedRequest != null);

            Assert.That(_mock.CapturedRequest.Context.llmFallback, Is.Null,
                "a caller without a fallback must send none, as before this field existed");

            SendAnalysisResponse(_mock.LastRegisteredRequestId);
            yield return new WaitUntil(() => task.IsCompleted);
        }

        private void SendAnalysisResponse(string requestId)
        {
            var json = $@"{{
                ""type"": ""analysisresponse"",
                ""requestId"": ""{requestId}"",
                ""analysis"": ""done"",
                ""llmResponse"": {{}},
                ""metadata"": {{}},
                ""timing"": {{}}
            }}";
            AnalysisService.Instance.OnTextMessage(json);
        }

        private class CapturingMockAdapter : IWebSocketClientAdapter
        {
            private bool _isConnected;
            private readonly Dictionary<string, INpcMessageHandler> _handlers = new();

            public bool IsConnected => _isConnected;
            public string LastRegisteredRequestId { get; private set; }
            public AnalysisRequestMessage CapturedRequest { get; private set; }

            public void SetConnected(bool connected) => _isConnected = connected;

            public void RegisterNpc(string requestId, INpcMessageHandler handler)
            {
                _handlers[requestId] = handler;
                LastRegisteredRequestId = requestId;
            }

            public void UnregisterNpc(string requestId) => _handlers.Remove(requestId);

            public Task SendAnalysisRequestAsync(AnalysisRequestMessage message)
            {
                CapturedRequest = message;
                return Task.CompletedTask;
            }
        }
    }
}
