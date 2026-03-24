using System;
using System.Reflection;
using NUnit.Framework;
using Tsc.AIBridge.WebSocket;
using UnityEngine;
using UnityEngine.TestTools;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: WebSocket connection errors must not interrupt users during recoverable failures
    ///
    /// WHY: The ErrorHandler shows a popup to the user whenever Debug.LogError is called.
    /// Transient network issues (DNS hiccups, server cold starts, brief disconnections) are
    /// recoverable via the auto-reconnect mechanism. Showing error popups during these
    /// recoverable situations disrupts the training session and confuses users.
    ///
    /// WHAT: Tests that WebSocketConnection uses the correct log severity based on recovery state:
    /// - LogWarning when auto-reconnect can still recover the connection
    /// - LogError only when all recovery options are exhausted
    /// - Diagnostics (DNS checks, network state) never trigger LogError
    /// - Cleanup and disconnect errors never trigger LogError
    ///
    /// HOW: Uses reflection to set internal reconnection state, then invokes error handlers
    /// and verifies log output via LogAssert.
    ///
    /// SUCCESS CRITERIA:
    /// - Users see zero error popups during recoverable connection failures
    /// - Users see exactly one clear error popup when connection is truly lost
    /// - Diagnostic logging never triggers user-visible errors
    /// - All transient errors are still logged as warnings for developer debugging
    ///
    /// BUSINESS IMPACT:
    /// - Failure = users see error popups during every brief network hiccup in VR training
    /// - Frustrated users, interrupted training sessions, support tickets
    /// - Especially critical in VR where popups are highly disruptive
    /// </summary>
    [TestFixture]
    public class WebSocketConnectionTests
    {
        private GameObject _ownerObject;
        private MonoBehaviour _owner;
        private WebSocketConnection _connection;

        private const int MaxReconnectAttempts = 10;

        [SetUp]
        public void SetUp()
        {
            _ownerObject = new GameObject("TestWebSocketOwner");
            _owner = _ownerObject.AddComponent<TestMonoBehaviour>();
            _connection = new WebSocketConnection(_owner, maxReconnectAttempts: MaxReconnectAttempts);
        }

        [TearDown]
        public void TearDown()
        {
            _connection?.Dispose();
            if (_ownerObject != null)
            {
                UnityEngine.Object.DestroyImmediate(_ownerObject);
            }
        }

        #region HandleError - Log Severity Tests

        [Test]
        public void HandleError_WhenReconnectPossible_LogsWarningNotError()
        {
            // Arrange - auto-reconnect enabled, zero attempts used
            SetPrivateField("_autoReconnectEnabled", true);
            SetPrivateField("_reconnectAttempts", 0);

            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(@"\[WebSocketConnection\] Connection error \(reconnecting\):"));

            // Act
            InvokePrivateMethod("HandleError", "Unable to connect to the remote server");

            // Assert - LogAssert validates no unexpected LogError was emitted
        }

        [Test]
        public void HandleError_WhenAtMaxAttempts_LogsError()
        {
            // Arrange - all reconnect attempts exhausted
            SetPrivateField("_autoReconnectEnabled", true);
            SetPrivateField("_reconnectAttempts", MaxReconnectAttempts);

            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(@"\[WebSocketConnection\] Connection error:"));

            // Act
            InvokePrivateMethod("HandleError", "Unable to connect to the remote server");

            // Assert - LogAssert validates LogError was emitted
        }

        [Test]
        public void HandleError_WhenAutoReconnectDisabled_LogsError()
        {
            // Arrange - auto-reconnect disabled (manual disconnect scenario)
            SetPrivateField("_autoReconnectEnabled", false);
            SetPrivateField("_reconnectAttempts", 0);

            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(@"\[WebSocketConnection\] Connection error:"));

            // Act
            InvokePrivateMethod("HandleError", "Connection refused");

            // Assert - LogAssert validates LogError was emitted
        }

        [Test]
        public void HandleError_WhenOneAttemptBeforeMax_LogsWarning()
        {
            // Arrange - one attempt remaining
            SetPrivateField("_autoReconnectEnabled", true);
            SetPrivateField("_reconnectAttempts", MaxReconnectAttempts - 1);

            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(@"\[WebSocketConnection\] Connection error \(reconnecting\):"));

            // Act
            InvokePrivateMethod("HandleError", "Server unreachable");

            // Assert - still warns because < max, not >=
        }

        [Test]
        public void HandleError_DuringShutdown_LogsNothing()
        {
            // Arrange - component is shutting down
            SetPrivateField("_isDisconnecting", true);

            // Act
            InvokePrivateMethod("HandleError", "Connection lost");

            // Assert - no logs expected at all (LogAssert would fail if any LogError/LogWarning appeared)
        }

        [Test]
        public void HandleError_WhenReconnectPossible_StillInvokesOnErrorEvent()
        {
            // Arrange
            SetPrivateField("_autoReconnectEnabled", true);
            SetPrivateField("_reconnectAttempts", 0);

            string receivedError = null;
            _connection.OnError += error => receivedError = error;

            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(@"\[WebSocketConnection\] Connection error \(reconnecting\):"));

            // Act
            InvokePrivateMethod("HandleError", "Unable to connect to the remote server");

            // Assert - OnError event is still raised so other components can react
            Assert.AreEqual("Unable to connect to the remote server", receivedError,
                "OnError event should still fire even when using LogWarning, so components can update their state");
        }

        #endregion

        #region HandleError - Error Handler Exception Tests

        [Test]
        public void HandleError_WhenErrorHandlerThrows_LogsWarningNotError()
        {
            // Arrange - subscribe a handler that throws
            SetPrivateField("_autoReconnectEnabled", true);
            SetPrivateField("_reconnectAttempts", 0);
            _connection.OnError += _ => throw new InvalidOperationException("Handler crashed");

            // Expect warning for the connection error itself
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(@"\[WebSocketConnection\] Connection error \(reconnecting\):"));
            // Expect warning (not error) for the handler exception
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(@"\[WebSocketConnection\] Error in error handler:"));

            // Act
            InvokePrivateMethod("HandleError", "Network timeout");

            // Assert - handler exception logged as warning, not error popup
        }

        #endregion

        #region Cleanup - Never LogError

        [Test]
        public void Cleanup_WhenNoWebSocket_DoesNotLogError()
        {
            // Arrange - no WebSocket connected (fresh instance)

            // Act
            InvokePrivateMethod("Cleanup");

            // Assert - LogAssert would fail if any LogError was emitted
        }

        #endregion

        #region Reflection Helpers

        private void SetPrivateField(string fieldName, object value)
        {
            var field = typeof(WebSocketConnection).GetField(fieldName,
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, $"Field '{fieldName}' not found on WebSocketConnection");
            field.SetValue(_connection, value);
        }

        private void InvokePrivateMethod(string methodName, params object[] args)
        {
            var method = typeof(WebSocketConnection).GetMethod(methodName,
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(method, $"Method '{methodName}' not found on WebSocketConnection");
            method.Invoke(_connection, args);
        }

        /// <summary>
        /// Minimal MonoBehaviour to serve as owner for WebSocketConnection.
        /// WebSocketConnection checks owner and gameObject validity before logging.
        /// </summary>
        private class TestMonoBehaviour : MonoBehaviour { }

        #endregion
    }
}