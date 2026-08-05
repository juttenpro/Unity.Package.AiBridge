using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Tsc.AIBridge.WebSocket;
using UnityEngine;
using UnityEngine.TestTools;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: WebSocketConnection is a transport. It reports failures, it never
    /// decides that a failure is final — and it never reconnects on its own.
    ///
    /// WHY: This class used to run its own auto-reconnect loop (10 attempts, exponential backoff),
    /// and that loop could not work. HandleClose starts the loop and THEN raises OnDisconnected,
    /// which makes WebSocketClient.CleanupConnection() unsubscribe from this connection and null its
    /// reference. So even a successful reconnect was invisible: nobody was listening any more,
    /// WebSocketClient.IsConnected stayed false, and the reopened socket sat unread on the backend
    /// occupying one of Kestrel's 100 upgraded-connection slots. Worse, the loop reused the ORIGINAL
    /// JWT — valid for exactly one hour, ClockSkew.Zero server-side — so after an hour every attempt
    /// failed on auth, and attempt 10 called UserErrorLogger.LogError("Connection lost … restart"),
    /// which the host ErrorHandler turns into "the application needs to be restarted". That is how a
    /// recoverable hiccup ended a live training session (customer HMC, IVA bedrijfsartsen).
    ///
    /// Reconnection now has exactly ONE owner: WebSocketClient.EnsureConnectionAsync, which every
    /// SendXAsync calls first. It fetches a FRESH JWT, disposes the stale socket, builds a new
    /// WebSocketConnection and re-subscribes to it. That path was always there and always correct.
    ///
    /// WHAT: Tests that failures from this class are reported as warnings (never as errors, which
    /// would raise the fatal popup), that OnError still fires so callers can react, and that no
    /// reconnect state remains on the type.
    ///
    /// HOW: Reflection to reach private handlers, LogAssert to pin log severity. LogAssert fails a
    /// test on any unexpected LogError, which is what makes "never LogError" enforceable.
    ///
    /// SUCCESS CRITERIA:
    /// - Connection errors log a warning and raise OnError; they never log an error
    /// - Errors during shutdown log nothing at all
    /// - Cleanup never logs an error
    /// - No auto-reconnect fields survive on WebSocketConnection
    ///
    /// BUSINESS IMPACT:
    /// - A LogError here reaches the user as "app must restart" and throws away their session.
    /// - A second reconnect authority silently leaks backend sockets and re-introduces the
    ///   expired-token retry storm.
    /// </summary>
    [TestFixture]
    public class WebSocketConnectionTests
    {
        private GameObject _ownerObject;
        private MonoBehaviour _owner;
        private WebSocketConnection _connection;

        private static readonly System.Text.RegularExpressions.Regex ConnectionErrorWarning =
            new(@"\[WebSocketConnection\] Connection error:");

        [SetUp]
        public void SetUp()
        {
            _ownerObject = new GameObject("TestWebSocketOwner");
            _owner = _ownerObject.AddComponent<TestMonoBehaviour>();
            _connection = new WebSocketConnection(_owner);
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

        #region HandleError - severity is always Warning

        /// <summary>
        /// Every transport error is recoverable from this class's point of view, because the next
        /// send re-runs EnsureConnectionAsync. There is no longer any state in which this class may
        /// escalate to LogError — doing so would surface the fatal restart popup for a hiccup.
        /// </summary>
        [TestCase("Unable to connect to the remote server")]
        [TestCase("Connection refused")]
        [TestCase("Server unreachable")]
        public void HandleError_AlwaysLogsWarning_NeverError(string error)
        {
            LogAssert.Expect(LogType.Warning, ConnectionErrorWarning);

            InvokePrivateMethod("HandleError", error);

            // LogAssert fails the test if any LogError was emitted.
        }

        [Test]
        public void HandleError_DuringShutdown_LogsNothing()
        {
            // A manual disconnect (scene unload, app quit) is not a failure worth reporting.
            SetPrivateField("_isDisconnecting", true);

            InvokePrivateMethod("HandleError", "Connection lost");

            // No log of any severity expected.
        }

        [Test]
        public void HandleError_StillInvokesOnErrorEvent()
        {
            string receivedError = null;
            _connection.OnError += error => receivedError = error;

            LogAssert.Expect(LogType.Warning, ConnectionErrorWarning);

            InvokePrivateMethod("HandleError", "Unable to connect to the remote server");

            Assert.AreEqual("Unable to connect to the remote server", receivedError,
                "OnError must still fire so callers can update their state even though we only warn.");
        }

        [Test]
        public void HandleError_WhenErrorHandlerThrows_LogsWarningNotError()
        {
            _connection.OnError += _ => throw new InvalidOperationException("Handler crashed");

            LogAssert.Expect(LogType.Warning, ConnectionErrorWarning);
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(
                @"\[WebSocketConnection\] Error in error handler:"));

            InvokePrivateMethod("HandleError", "Network timeout");

            // A crashing subscriber must not escalate into a fatal popup either.
        }

        #endregion

        #region Cleanup - never LogError

        [Test]
        public void Cleanup_WhenNoWebSocket_DoesNotLogError()
        {
            InvokePrivateMethod("Cleanup");
        }

        #endregion

        #region Single reconnect authority

        /// <summary>
        /// Guards the architectural decision, not an implementation detail: reconnection lives in
        /// WebSocketClient.EnsureConnectionAsync only. This test exists because the bug was HAVING a
        /// second authority here — one that raced with the first, leaked backend sockets and retried
        /// with an expired token. If someone re-adds reconnect state to this class, this fails and
        /// points them at the reasoning in the fixture summary above.
        /// </summary>
        [Test]
        public void WebSocketConnection_HasNoAutoReconnectState()
        {
            var reconnectFields = typeof(WebSocketConnection)
                .GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public)
                .Where(f => f.Name.IndexOf("reconnect", StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(f => f.Name)
                .ToArray();

            Assert.IsEmpty(reconnectFields,
                "Reconnection must be owned solely by WebSocketClient.EnsureConnectionAsync (fresh JWT, " +
                "re-subscribed handlers). Found reconnect state on WebSocketConnection: " +
                string.Join(", ", reconnectFields));
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
