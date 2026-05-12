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
    /// BUSINESS REQUIREMENT: De AIBridge AnalysisService moet de thinkingBudget die de
    /// content creator in de AI API Template heeft gekozen meeverzenden in de
    /// WebSocket-payload naar de backend. Anders kunnen creators de keuze "wel/niet
    /// thinking gebruiken voor deze analyse" niet effectief maken — een blijvende
    /// truncatie-bug op Placebo + AcuteZorg (gemini-2.5-flash/-lite met maxTokens=800).
    ///
    /// WHY: ConversationContext.thinkingBudget is de wire-laag tussen Unity en backend
    /// voor zowel text-input als analysis flows. Zonder het veld kan de backend's
    /// ConversationOrchestrator het niet doorzetten aan VertexAIService.
    ///
    /// WHAT: Test dat AnalysisService.RequestAnalysisAsync een optionele thinkingBudget
    /// parameter accepteert en die in de uitgaande AnalysisRequestMessage.Context plaatst.
    /// Bestaande aanroepen zonder de parameter (oudere callers, zoals niet-bijgewerkte
    /// host-projecten) moeten null doorgeven zodat de backend zijn provider default
    /// toepast — bit-voor-bit identiek aan pre-thinking-config gedrag.
    ///
    /// HOW: Mock WebSocket adapter vangt de uitgaande AnalysisRequestMessage; we
    /// inspecteren Context.thinkingBudget direct (geen JSON round-trip nodig — het
    /// type is een Serializable C# class).
    ///
    /// SUCCESS CRITERIA:
    /// - thinkingBudget=0  → Context.thinkingBudget == 0   (disable thinking voor flash/-lite)
    /// - thinkingBudget=-1 → Context.thinkingBudget == -1  (dynamic)
    /// - thinkingBudget=1024 → Context.thinkingBudget == 1024 (explicit reservation)
    /// - thinkingBudget niet meegegeven → Context.thinkingBudget == null (backward compat)
    ///
    /// BUSINESS IMPACT:
    /// - Falen = template-keuze van content creator (llmThinkingBudget=0 in
    ///   Placebo Analysis.asset) verdwijnt bij de wire-grens; backend valt terug op
    ///   provider default (dynamic thinking) en truncatie blijft optreden.
    /// </summary>
    public class AnalysisServiceThinkingBudgetTests
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
        public IEnumerator RequestAnalysisAsync_WithThinkingBudgetZero_PlacesItInContext()
        {
            // Arrange
            var messages = new List<ChatMessage> { new ChatMessage { Role = "user", Content = "test" } };

            // Act — start the call but cancel completion via the captured message; the
            // mock never returns a response so we cancel below to keep tests fast.
            var task = AnalysisService.Instance.RequestAnalysisAsync(
                messages, "vertexai", "gemini-2.5-flash", 0.2f, 800, "json_object",
                thinkingBudget: 0);

            // Wait until the mock captured the outgoing request
            yield return new WaitUntil(() => _mock.CapturedRequest != null);

            // Assert
            Assert.IsNotNull(_mock.CapturedRequest, "Outgoing AnalysisRequestMessage must be captured");
            Assert.AreEqual(0, _mock.CapturedRequest.Context.thinkingBudget,
                "thinkingBudget=0 must be wire-visible so backend can disable thinking for flash/-lite");

            // Cleanup — release the task by sending an analysis response
            SendAnalysisResponse(_mock.LastRegisteredRequestId);
            yield return new WaitUntil(() => task.IsCompleted);
        }

        [UnityTest]
        public IEnumerator RequestAnalysisAsync_WithDynamicThinkingBudget_PlacesItInContext()
        {
            // Arrange
            var messages = new List<ChatMessage> { new ChatMessage { Role = "user", Content = "test" } };

            // Act
            var task = AnalysisService.Instance.RequestAnalysisAsync(
                messages, "vertexai", "gemini-2.5-flash", 0.5f, 8192, "json_object",
                thinkingBudget: -1);

            yield return new WaitUntil(() => _mock.CapturedRequest != null);

            // Assert
            Assert.AreEqual(-1, _mock.CapturedRequest.Context.thinkingBudget);

            SendAnalysisResponse(_mock.LastRegisteredRequestId);
            yield return new WaitUntil(() => task.IsCompleted);
        }

        [UnityTest]
        public IEnumerator RequestAnalysisAsync_WithExplicitThinkingBudget_PlacesItInContext()
        {
            // Arrange
            var messages = new List<ChatMessage> { new ChatMessage { Role = "user", Content = "test" } };

            // Act
            var task = AnalysisService.Instance.RequestAnalysisAsync(
                messages, "vertexai", "gemini-2.5-pro", 0.5f, 8192, "json_object",
                thinkingBudget: 2048);

            yield return new WaitUntil(() => _mock.CapturedRequest != null);

            // Assert
            Assert.AreEqual(2048, _mock.CapturedRequest.Context.thinkingBudget);

            SendAnalysisResponse(_mock.LastRegisteredRequestId);
            yield return new WaitUntil(() => task.IsCompleted);
        }

        [UnityTest]
        public IEnumerator RequestAnalysisAsync_WithoutThinkingBudget_LeavesItNull()
        {
            // Backward compat: bestaande callers die het optionele argument weglaten
            // moeten null op de wire zetten. De backend treat null als "provider default".
            // Arrange
            var messages = new List<ChatMessage> { new ChatMessage { Role = "user", Content = "test" } };

            // Act — geen thinkingBudget parameter
            var task = AnalysisService.Instance.RequestAnalysisAsync(
                messages, "openai", "gpt-4o-mini", 0.7f, 800, "json_object");

            yield return new WaitUntil(() => _mock.CapturedRequest != null);

            // Assert
            Assert.IsNull(_mock.CapturedRequest.Context.thinkingBudget,
                "thinkingBudget must default to null so backward-compat callers don't suddenly disable thinking");

            SendAnalysisResponse(_mock.LastRegisteredRequestId);
            yield return new WaitUntil(() => task.IsCompleted);
        }

        // --- Test helpers ---

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

        /// <summary>
        /// Mock adapter that captures the outgoing AnalysisRequestMessage so tests can
        /// inspect Context fields without round-tripping JSON.
        /// </summary>
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
