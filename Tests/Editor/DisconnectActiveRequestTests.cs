using System.Reflection;
using NativeWebSocket;
using NUnit.Framework;
using Tsc.AIBridge.Core;
using Tsc.AIBridge.Messages;
using UnityEngine;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: Active requests must be aborted on WebSocket disconnect so the
    /// RuleSystem can reset IsReactionBusy and the NPC remains responsive.
    ///
    /// WHY: When the Cloud Run backend instance is replaced (OOM, scaling), all WebSocket
    /// connections drop simultaneously. Without aborting the active request, IsReactionBusy
    /// stays permanently true and the NPC never responds again — the user must restart the app.
    ///
    /// WHAT: Tests that RequestOrchestrator fires OnSttFailed with reason "ConnectionLost"
    /// when a WebSocket disconnect occurs during an active audio request, and that the
    /// _isRequestActive flag is properly reset to prevent double-firing.
    ///
    /// HOW: Uses reflection to set _isRequestActive, subscribes to OnSttFailed event, then
    /// fires the disconnect handler and verifies the event was raised with correct parameters.
    ///
    /// SUCCESS CRITERIA:
    /// - OnSttFailed fires exactly once with Reason="ConnectionLost" when active request exists
    /// - OnSttFailed does NOT fire when no active request exists
    /// - _isRequestActive is set to false after disconnect cleanup
    /// - Double disconnect does not fire OnSttFailed twice (guard via _isRequestActive)
    ///
    /// BUSINESS IMPACT:
    /// - Failure = permanent NPC freeze requiring app restart during live training sessions
    /// - Affects all simultaneous users when a server instance is replaced
    /// - Especially critical in medical training (IVA bedrijfsartsen) with paying customers
    /// </summary>
    [TestFixture]
    public class DisconnectActiveRequestTests
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
        public void HandleWebSocketDisconnected_WithActiveRequest_FiresSttFailed()
        {
            // Arrange
            SetField("_isRequestActive", true);

            NoTranscriptMessage receivedMessage = null;
            _orchestrator.OnSttFailed += msg => receivedMessage = msg;

            // Act
            InvokeDisconnectHandler(WebSocketCloseCode.Abnormal);

            // Assert
            Assert.IsNotNull(receivedMessage, "OnSttFailed should have been invoked");
            Assert.AreEqual("ConnectionLost", receivedMessage.Reason,
                "Reason should indicate connection loss, not silence");
        }

        [Test]
        public void HandleWebSocketDisconnected_WithoutActiveRequest_DoesNotFireSttFailed()
        {
            // Arrange: _isRequestActive is false by default
            bool sttFailedFired = false;
            _orchestrator.OnSttFailed += _ => sttFailedFired = true;

            // Act
            InvokeDisconnectHandler(WebSocketCloseCode.Normal);

            // Assert
            Assert.IsFalse(sttFailedFired,
                "OnSttFailed should NOT fire when there is no active request");
        }

        [Test]
        public void HandleWebSocketDisconnected_WithActiveRequest_SetsIsRequestActiveFalse()
        {
            // Arrange
            SetField("_isRequestActive", true);

            // Act
            InvokeDisconnectHandler(WebSocketCloseCode.Abnormal);

            // Assert
            var isActive = GetField<bool>("_isRequestActive");
            Assert.IsFalse(isActive,
                "_isRequestActive should be false after disconnect cleanup");
        }

        [Test]
        public void HandleWebSocketDisconnected_CalledTwice_FiresSttFailedOnlyOnce()
        {
            // Arrange
            SetField("_isRequestActive", true);

            int fireCount = 0;
            _orchestrator.OnSttFailed += _ => fireCount++;

            // Act: simulate duplicate disconnect callbacks
            InvokeDisconnectHandler(WebSocketCloseCode.Abnormal);
            InvokeDisconnectHandler(WebSocketCloseCode.Abnormal);

            // Assert
            Assert.AreEqual(1, fireCount,
                "OnSttFailed should fire exactly once — second call finds _isRequestActive=false and skips");
        }

        #region Helpers

        private void SetField(string fieldName, object value)
        {
            var field = typeof(RequestOrchestrator).GetField(fieldName, PrivateInstance);
            Assert.IsNotNull(field, $"Field '{fieldName}' not found on RequestOrchestrator");
            field.SetValue(_orchestrator, value);
        }

        private T GetField<T>(string fieldName)
        {
            var field = typeof(RequestOrchestrator).GetField(fieldName, PrivateInstance);
            Assert.IsNotNull(field, $"Field '{fieldName}' not found on RequestOrchestrator");
            return (T)field.GetValue(_orchestrator);
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
