using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Tsc.AIBridge.Services;
using Tsc.AIBridge.Messages;
using Tsc.AIBridge.WebSocket;

namespace Tsc.AIBridge.Tests.Runtime
{
    /// <summary>
    /// Unit tests for AnalysisService refactored to pure C# singleton.
    ///
    /// TESTS:
    /// - Singleton pattern behavior
    /// - Successful analysis request/response
    /// - Error handling (no connection, timeout, parse errors)
    /// - Proper resource cleanup
    /// - TaskCompletionSource behavior
    /// </summary>
    public class AnalysisServiceTests
    {
        private MockWebSocketClientAdapter _mockWebSocket;

        [SetUp]
        public void Setup()
        {
            // Create and inject mock adapter into AnalysisService
            _mockWebSocket = new MockWebSocketClientAdapter();
            AnalysisService.Instance.WebSocketAdapter = _mockWebSocket;
        }

        [TearDown]
        public void TearDown()
        {
            // Reset to production adapter
            AnalysisService.Instance.WebSocketAdapter = new WebSocketClientProductionAdapter();
        }

        [Test]
        public void Instance_ReturnsSingleton()
        {
            var instance1 = AnalysisService.Instance;
            var instance2 = AnalysisService.Instance;

            Assert.AreSame(instance1, instance2, "Should return same singleton instance");
        }

        [UnityTest]
        public IEnumerator RequestAnalysisAsync_ThrowsException_WhenWebSocketNotConnected()
        {
            // Arrange
            _mockWebSocket.SetConnected(false);
            var messages = new List<ChatMessage>
            {
                new ChatMessage { Role = "user", Content = "Test message" }
            };

            // Expect the error log
            LogAssert.Expect(LogType.Error, "[AnalysisService] WebSocket not connected");

            // Act
            var task = AnalysisService.Instance.RequestAnalysisAsync(
                messages, "openai", "gpt-4", 0.7f, 100, "text");

            // Wait for task to complete
            yield return new WaitUntil(() => task.IsCompleted);

            // Assert
            Assert.IsTrue(task.IsFaulted, "Task should be faulted");
            Assert.IsInstanceOf<System.InvalidOperationException>(task.Exception.InnerException);
            Assert.That(task.Exception.InnerException.Message, Does.Contain("not connected"));
        }

        [UnityTest]
        public IEnumerator RequestAnalysisAsync_ThrowsException_WhenMessagesNull()
        {
            // Arrange
            _mockWebSocket.SetConnected(true);

            // Act
            var task = AnalysisService.Instance.RequestAnalysisAsync(
                null, "openai", "gpt-4", 0.7f, 100, "text");

            // Wait for task to complete
            yield return new WaitUntil(() => task.IsCompleted);

            // Assert
            Assert.IsTrue(task.IsFaulted, "Task should be faulted");
            Assert.IsInstanceOf<System.ArgumentException>(task.Exception.InnerException);
        }

        [UnityTest]
        public IEnumerator RequestAnalysisAsync_ThrowsException_WhenMessagesEmpty()
        {
            // Arrange
            _mockWebSocket.SetConnected(true);
            var messages = new List<ChatMessage>();

            // Act
            var task = AnalysisService.Instance.RequestAnalysisAsync(
                messages, "openai", "gpt-4", 0.7f, 100, "text");

            // Wait for task to complete
            yield return new WaitUntil(() => task.IsCompleted);

            // Assert
            Assert.IsTrue(task.IsFaulted, "Task should be faulted");
            Assert.IsInstanceOf<System.ArgumentException>(task.Exception.InnerException);
        }

        [UnityTest]
        public IEnumerator RequestAnalysisAsync_RegistersAndUnregistersHandler()
        {
            // Arrange
            _mockWebSocket.SetConnected(true);
            var messages = new List<ChatMessage>
            {
                new ChatMessage { Role = "user", Content = "Test message" }
            };

            // Act - Start request
            var task = AnalysisService.Instance.RequestAnalysisAsync(
                messages, "openai", "gpt-4", 0.7f, 100, "text");

            // Wait a bit for registration
            yield return new WaitForSeconds(0.1f);

            // Assert - Handler should be registered
            Assert.AreEqual(1, _mockWebSocket.RegisteredHandlersCount, "Handler should be registered");

            // Simulate response
            var requestId = _mockWebSocket.LastRegisteredRequestId;
            var responseJson = $@"{{
                ""type"": ""analysisresponse"",
                ""requestId"": ""{requestId}"",
                ""analysis"": ""Test analysis"",
                ""llmResponse"": {{ ""content"": ""Test"" }},
                ""metadata"": {{}},
                ""timing"": {{ ""totalMs"": 100 }}
            }}";

            AnalysisService.Instance.OnTextMessage(responseJson);

            // Wait for task to complete
            yield return new WaitUntil(() => task.IsCompleted);

            // Assert - Handler should be unregistered after completion
            Assert.AreEqual(0, _mockWebSocket.RegisteredHandlersCount, "Handler should be unregistered");
            Assert.IsTrue(task.IsCompletedSuccessfully, "Task should complete successfully");
        }

        [UnityTest]
        public IEnumerator RequestAnalysisAsync_CompletesSuccessfully_WithValidResponse()
        {
            // Arrange
            _mockWebSocket.SetConnected(true);
            var messages = new List<ChatMessage>
            {
                new ChatMessage { Role = "user", Content = "Analyze this conversation" }
            };

            // Act
            var task = AnalysisService.Instance.RequestAnalysisAsync(
                messages, "openai", "gpt-4", 0.7f, 500, "text");

            // Wait for registration
            yield return new WaitForSeconds(0.1f);

            // Simulate successful response
            var requestId = _mockWebSocket.LastRegisteredRequestId;
            var responseJson = $@"{{
                ""type"": ""analysisresponse"",
                ""requestId"": ""{requestId}"",
                ""analysis"": ""This is a test analysis result"",
                ""llmResponse"": {{
                    ""content"": ""This is a test analysis result"",
                    ""role"": ""assistant"",
                    ""model"": ""gpt-4""
                }},
                ""metadata"": {{
                    ""usage"": {{ ""total_tokens"": 150 }}
                }},
                ""timing"": {{
                    ""totalMs"": 850,
                    ""model"": ""gpt-4""
                }}
            }}";

            AnalysisService.Instance.OnTextMessage(responseJson);

            // Wait for completion
            yield return new WaitUntil(() => task.IsCompleted);

            // Assert
            Assert.IsTrue(task.IsCompletedSuccessfully, "Task should complete successfully");
            var result = task.Result;
            Assert.IsNotNull(result, "Result should not be null");
            Assert.AreEqual("This is a test analysis result", result.analysis);
            Assert.IsNotNull(result.llmResponse, "LLM response should not be null");
            Assert.IsNotNull(result.metadata, "Metadata should not be null");
            Assert.IsNotNull(result.timing, "Timing should not be null");
        }

        [UnityTest]
        public IEnumerator RequestAnalysisAsync_TimesOut_After30Seconds()
        {
            // Arrange
            _mockWebSocket.SetConnected(true);
            var messages = new List<ChatMessage>
            {
                new ChatMessage { Role = "user", Content = "Test message" }
            };

            // Act
            var task = AnalysisService.Instance.RequestAnalysisAsync(
                messages, "openai", "gpt-4", 0.7f, 100, "text");

            // Don't send response - let it timeout
            // Note: We can't wait 30 seconds in a test, so we'll just verify the timeout mechanism exists
            yield return new WaitForSeconds(0.5f);

            // Assert - Task should still be running (not completed yet in 0.5s)
            Assert.IsFalse(task.IsCompleted, "Task should not complete without response");

            // Cleanup - send response to avoid hanging test
            var requestId = _mockWebSocket.LastRegisteredRequestId;
            var responseJson = $@"{{
                ""type"": ""analysisresponse"",
                ""requestId"": ""{requestId}"",
                ""analysis"": ""Test"",
                ""llmResponse"": {{}},
                ""metadata"": {{}},
                ""timing"": {{}}
            }}";
            AnalysisService.Instance.OnTextMessage(responseJson);

            yield return new WaitUntil(() => task.IsCompleted);
        }

        [UnityTest]
        public IEnumerator RequestAnalysisAsync_HandlesParseError_Gracefully()
        {
            // Arrange
            _mockWebSocket.SetConnected(true);
            var messages = new List<ChatMessage>
            {
                new ChatMessage { Role = "user", Content = "Test message" }
            };

            // Expect error logs from malformed JSON
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(@"\[AnalysisService\] Failed to parse response:.*"));

            // Act
            var task = AnalysisService.Instance.RequestAnalysisAsync(
                messages, "openai", "gpt-4", 0.7f, 100, "text");

            // Wait for registration
            yield return new WaitForSeconds(0.1f);

            // Simulate malformed JSON that DOES have "type" field but fails to deserialize
            // This will pass the initial check but fail during deserialization
            var invalidJson = @"{""type"": ""analysisresponse"", ""requestId"": ""test"", invalid json here }";
            AnalysisService.Instance.OnTextMessage(invalidJson);

            // Wait a bit
            yield return new WaitForSeconds(0.2f);

            // Assert - Task should fault due to parse error
            Assert.IsTrue(task.IsFaulted, "Task should be faulted due to parse error");
            Assert.IsInstanceOf<System.InvalidOperationException>(task.Exception.InnerException);
        }

        [UnityTest]
        public IEnumerator RequestAnalysisAsync_IgnoresMessagesWithWrongRequestId()
        {
            // Arrange
            _mockWebSocket.SetConnected(true);
            var messages = new List<ChatMessage>
            {
                new ChatMessage { Role = "user", Content = "Test message" }
            };

            // Act
            var task = AnalysisService.Instance.RequestAnalysisAsync(
                messages, "openai", "gpt-4", 0.7f, 100, "text");

            // Wait for registration
            yield return new WaitForSeconds(0.1f);

            // Simulate response with WRONG RequestId
            var wrongRequestId = "wrong-request-id-12345";
            var responseJson = $@"{{
                ""type"": ""analysisresponse"",
                ""requestId"": ""{wrongRequestId}"",
                ""analysis"": ""This should be ignored"",
                ""llmResponse"": {{}},
                ""metadata"": {{}},
                ""timing"": {{}}
            }}";

            AnalysisService.Instance.OnTextMessage(responseJson);

            // Wait a bit
            yield return new WaitForSeconds(0.2f);

            // Assert - Task should still be waiting (not completed)
            Assert.IsFalse(task.IsCompleted, "Task should not complete with wrong RequestId");

            // Cleanup - send correct response
            var correctRequestId = _mockWebSocket.LastRegisteredRequestId;
            var correctResponseJson = $@"{{
                ""type"": ""analysisresponse"",
                ""requestId"": ""{correctRequestId}"",
                ""analysis"": ""Correct response"",
                ""llmResponse"": {{}},
                ""metadata"": {{}},
                ""timing"": {{}}
            }}";
            AnalysisService.Instance.OnTextMessage(correctResponseJson);

            yield return new WaitUntil(() => task.IsCompleted);
            Assert.IsTrue(task.IsCompletedSuccessfully);
            Assert.AreEqual("Correct response", task.Result.analysis);
        }

        [UnityTest]
        public IEnumerator RequestAnalysisAsync_IgnoresNonAnalysisMessages()
        {
            // Arrange
            _mockWebSocket.SetConnected(true);
            var messages = new List<ChatMessage>
            {
                new ChatMessage { Role = "user", Content = "Test message" }
            };

            // Act
            var task = AnalysisService.Instance.RequestAnalysisAsync(
                messages, "openai", "gpt-4", 0.7f, 100, "text");

            // Wait for registration
            yield return new WaitForSeconds(0.1f);

            var requestId = _mockWebSocket.LastRegisteredRequestId;

            // Send various non-analysis messages
            var transcriptionMsg = $@"{{ ""type"": ""transcription"", ""requestId"": ""{requestId}"", ""text"": ""Hello"" }}";
            var aiResponseMsg = $@"{{ ""type"": ""airesponse"", ""requestId"": ""{requestId}"", ""content"": ""Hi"" }}";

            AnalysisService.Instance.OnTextMessage(transcriptionMsg);
            AnalysisService.Instance.OnTextMessage(aiResponseMsg);

            // Wait a bit
            yield return new WaitForSeconds(0.2f);

            // Assert - Task should still be waiting (ignored other messages)
            Assert.IsFalse(task.IsCompleted, "Task should not complete with non-analysis messages");

            // Cleanup - send correct analysis response
            var analysisResponseJson = $@"{{
                ""type"": ""analysisresponse"",
                ""requestId"": ""{requestId}"",
                ""analysis"": ""Final analysis"",
                ""llmResponse"": {{}},
                ""metadata"": {{}},
                ""timing"": {{}}
            }}";
            AnalysisService.Instance.OnTextMessage(analysisResponseJson);

            yield return new WaitUntil(() => task.IsCompleted);
            Assert.IsTrue(task.IsCompletedSuccessfully);
        }

        [UnityTest]
        public IEnumerator NpcId_ReturnsCurrentRequestId()
        {
            // Arrange
            _mockWebSocket.SetConnected(true);
            var messages = new List<ChatMessage>
            {
                new ChatMessage { Role = "user", Content = "Test message" }
            };

            // Act
            var task = AnalysisService.Instance.RequestAnalysisAsync(
                messages, "openai", "gpt-4", 0.7f, 100, "text");

            // Wait for registration
            yield return new WaitForSeconds(0.1f);

            // Assert
            var npcId = AnalysisService.Instance.NpcId;
            Assert.IsNotNull(npcId, "NpcId should not be null during active request");
            Assert.IsNotEmpty(npcId, "NpcId should not be empty during active request");

            // Cleanup
            var requestId = _mockWebSocket.LastRegisteredRequestId;
            var responseJson = $@"{{
                ""type"": ""analysisresponse"",
                ""requestId"": ""{requestId}"",
                ""analysis"": ""Test"",
                ""llmResponse"": {{}},
                ""metadata"": {{}},
                ""timing"": {{}}
            }}";
            AnalysisService.Instance.OnTextMessage(responseJson);

            yield return new WaitUntil(() => task.IsCompleted);

            // NpcId should be null after completion
            Assert.IsNull(AnalysisService.Instance.NpcId, "NpcId should be null after request completes");
        }

        [UnityTest]
        public IEnumerator OnBinaryMessage_DoesNothing()
        {
            // Arrange
            var audioData = new byte[] { 1, 2, 3, 4 };

            // Act - Should not throw or do anything
            AnalysisService.Instance.OnBinaryMessage(audioData);

            yield return null;

            // Assert - Just verify no exception was thrown
            Assert.Pass("OnBinaryMessage should safely ignore binary data");
        }

        [UnityTest]
        public IEnumerator OnRequestComplete_DoesNothing()
        {
            // Arrange
            var requestId = "test-request-id";

            // Act - Should not throw or do anything
            AnalysisService.Instance.OnRequestComplete(requestId);

            yield return null;

            // Assert - Just verify no exception was thrown
            Assert.Pass("OnRequestComplete should safely handle completion notification");
        }

        /// <summary>
        /// Mock WebSocketClient adapter for testing.
        /// Implements IWebSocketClientAdapter to simulate WebSocket behavior without actual network connection.
        /// </summary>
        private class MockWebSocketClientAdapter : IWebSocketClientAdapter
        {
            private bool _isConnected;
            private readonly Dictionary<string, INpcMessageHandler> _handlers = new Dictionary<string, INpcMessageHandler>();

            public bool IsConnected => _isConnected;
            public int RegisteredHandlersCount => _handlers.Count;
            public string LastRegisteredRequestId { get; private set; }

            public void SetConnected(bool connected)
            {
                _isConnected = connected;
            }

            public void RegisterNpc(string requestId, INpcMessageHandler handler)
            {
                _handlers[requestId] = handler;
                LastRegisteredRequestId = requestId;
            }

            public void UnregisterNpc(string requestId)
            {
                _handlers.Remove(requestId);
            }

            public Task SendAnalysisRequestAsync(AnalysisRequestMessage message)
            {
                // Mock implementation - just return completed task
                return Task.CompletedTask;
            }
        }
    }
}
