using System.Collections;
using System.Reflection;
using NativeWebSocket;
using NUnit.Framework;
using Tsc.AIBridge.Core;
using UnityEngine;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: Stale requests must be cleared on WebSocket disconnect
    ///
    /// WHY: After a WebSocket disconnect+reconnect, queued audio requests hold stale session IDs.
    /// When processed after reconnection, they cause "Session mismatch!" errors because
    /// _currentSession was overwritten by a newer StartAudioRequest. This results in 8+ STT
    /// failures flooding the user with "No speech detected" warnings.
    ///
    /// WHAT: Tests that RequestOrchestrator clears its audio and text request queues when the
    /// WebSocket fires OnDisconnected, preventing stale requests from being processed.
    ///
    /// HOW: Uses reflection to access private queues, enqueues test requests, then fires the
    /// disconnect handler and verifies queues are empty.
    ///
    /// SUCCESS CRITERIA:
    /// - Audio request queue is emptied on disconnect
    /// - Text request queue is emptied on disconnect
    /// - Empty queues on disconnect produce no log (no noise)
    /// - Non-empty queues log what was cleared (traceability)
    ///
    /// BUSINESS IMPACT:
    /// - Failure = 8+ STT failures per reconnect, "No speech detected" floods
    /// - Interrupted training sessions, confused users
    /// - Especially common on unstable WiFi in medical training environments
    /// </summary>
    [TestFixture]
    public class DisconnectQueueCleanupTests
    {
        private GameObject _orchestratorObject;
        private RequestOrchestrator _orchestrator;

        private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

        [SetUp]
        public void SetUp()
        {
            _orchestratorObject = new GameObject("TestOrchestrator");
            _orchestrator = _orchestratorObject.AddComponent<RequestOrchestrator>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_orchestratorObject != null)
                Object.DestroyImmediate(_orchestratorObject);
        }

        [Test]
        public void HandleWebSocketDisconnected_WithQueuedAudioRequests_ClearsQueue()
        {
            // Arrange: Enqueue audio requests via reflection
            var audioQueue = GetQueue("_audioRequestQueue");
            EnqueueDummyRequests(audioQueue, "AudioRequest", 3);

            Assert.AreEqual(3, audioQueue.Count, "Should have 3 queued audio requests");

            // Act: Fire disconnect handler
            InvokeDisconnectHandler(WebSocketCloseCode.Normal);

            // Assert: Queue should be cleared
            Assert.AreEqual(0, audioQueue.Count, "Audio queue should be empty after disconnect");
        }

        [Test]
        public void HandleWebSocketDisconnected_WithQueuedTextRequests_ClearsQueue()
        {
            // Arrange
            var textQueue = GetQueue("_textRequestQueue");
            EnqueueDummyRequests(textQueue, "TextRequest", 2);

            Assert.AreEqual(2, textQueue.Count, "Should have 2 queued text requests");

            // Act
            InvokeDisconnectHandler(WebSocketCloseCode.Normal);

            // Assert
            Assert.AreEqual(0, textQueue.Count, "Text queue should be empty after disconnect");
        }

        [Test]
        public void HandleWebSocketDisconnected_WithEmptyQueues_DoesNotThrow()
        {
            // Arrange: Queues are already empty after construction

            // Act & Assert: Should not throw or produce errors
            Assert.DoesNotThrow(() => InvokeDisconnectHandler(WebSocketCloseCode.Normal));

            var audioQueue = GetQueue("_audioRequestQueue");
            var textQueue = GetQueue("_textRequestQueue");
            Assert.AreEqual(0, audioQueue.Count);
            Assert.AreEqual(0, textQueue.Count);
        }

        [Test]
        public void HandleWebSocketDisconnected_ClearsBothQueuesSimultaneously()
        {
            // Arrange: Fill both queues
            var audioQueue = GetQueue("_audioRequestQueue");
            var textQueue = GetQueue("_textRequestQueue");

            EnqueueDummyRequests(audioQueue, "AudioRequest", 5);
            EnqueueDummyRequests(textQueue, "TextRequest", 3);

            Assert.AreEqual(5, audioQueue.Count);
            Assert.AreEqual(3, textQueue.Count);

            // Act
            InvokeDisconnectHandler(WebSocketCloseCode.Normal);

            // Assert
            Assert.AreEqual(0, audioQueue.Count, "Audio queue should be empty");
            Assert.AreEqual(0, textQueue.Count, "Text queue should be empty");
        }

        [Test]
        public void HandleWebSocketDisconnected_OnAbnormalClose_AlsoClearsQueues()
        {
            // Arrange
            var audioQueue = GetQueue("_audioRequestQueue");
            EnqueueDummyRequests(audioQueue, "AudioRequest", 2);

            // Act: Abnormal close (network failure)
            InvokeDisconnectHandler(WebSocketCloseCode.Abnormal);

            // Assert: Should clear regardless of close code
            Assert.AreEqual(0, audioQueue.Count, "Queue should be cleared on abnormal close too");
        }

        #region Helpers

        private ICollection GetQueue(string fieldName)
        {
            var field = typeof(RequestOrchestrator).GetField(fieldName, PrivateInstance);
            Assert.IsNotNull(field, $"Field '{fieldName}' not found on RequestOrchestrator");
            var queue = field.GetValue(_orchestrator) as ICollection;
            Assert.IsNotNull(queue, $"Field '{fieldName}' is null or not ICollection");
            return queue;
        }

        private void EnqueueDummyRequests(ICollection queue, string nestedTypeName, int count)
        {
            var requestType = typeof(RequestOrchestrator).GetNestedType(nestedTypeName, BindingFlags.NonPublic);
            Assert.IsNotNull(requestType, $"Nested type '{nestedTypeName}' not found");

            // Get the Enqueue method from the concrete Queue<T> type
            var enqueueMethod = queue.GetType().GetMethod("Enqueue");
            Assert.IsNotNull(enqueueMethod, $"Enqueue method not found on {queue.GetType().Name}");

            for (int i = 0; i < count; i++)
            {
                var request = System.Activator.CreateInstance(requestType);
                enqueueMethod.Invoke(queue, new[] { request });
            }
        }

        private void InvokeDisconnectHandler(WebSocketCloseCode code)
        {
            var method = typeof(RequestOrchestrator).GetMethod("HandleWebSocketDisconnected", PrivateInstance);
            Assert.IsNotNull(method, "HandleWebSocketDisconnected method not found on RequestOrchestrator");
            method.Invoke(_orchestrator, new object[] { code });
        }

        #endregion
    }
}
