using System.Reflection;
using NUnit.Framework;
using Tsc.AIBridge.Core;
using Tsc.AIBridge.Messages;
using UnityEngine;
using UnityEngine.TestTools;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: When a conversation turn cannot reach the backend, the turn is
    /// abandoned cleanly and reported as RECOVERABLE — never as a crash that ends the session.
    ///
    /// WHY: Field report (Edward, customer HMC, IVA bedrijfsartsen). Releasing the push-to-talk
    /// button produced "[RequestOrchestrator] Cannot send end messages - WebSocket not connected
    /// and no reconnection in progress". Two defects combined to make that a session-ending event:
    ///
    /// 1. When SendSessionStartAsync faulted (socket down at turn start), ProcessAudioRequest
    ///    yield-broke WITHOUT resetting _isRequestActive / _currentSession. The turn stayed armed,
    ///    so the failure surfaced minutes later at push-to-talk RELEASE instead of immediately,
    ///    and the RuleSystem's IsReactionBusy stayed set in the meantime.
    /// 2. Both log lines were untagged Debug.LogError, which the host project's ErrorHandler
    ///    classifies as Fatal — "Something went wrong / The application needs to be restarted",
    ///    with a button that quits the app. The condition is in fact recoverable: every
    ///    WebSocketClient.SendXAsync calls EnsureConnectionAsync first, which fetches a FRESH JWT
    ///    and rebuilds the socket, so the next push-to-talk reconnects on its own.
    ///
    /// WHAT: Covers the shared abort contract (AbortActiveTurn) that both the disconnect handler
    /// and the faulted-SessionStart path now use, and the [Recoverable] marker that
    /// UserErrorLogger emits for the host ErrorHandler to key on.
    ///
    /// HOW: Reflection over RequestOrchestrator's private state, matching the established pattern
    /// in DisconnectActiveRequestTests. The faulted-SessionStart CALL SITE itself is not unit
    /// tested — ProcessAudioRequest needs an NpcClient, SpeechInputHandler, WebSocketClient,
    /// NpcMessageRouter singleton and a live scene, which project convention puts out of scope for
    /// EditMode tests. The unit under test here is the abort contract it delegates to.
    ///
    /// SUCCESS CRITERIA:
    /// - AbortActiveTurn fires OnSttFailed once with Reason="ConnectionLost" when a turn is armed
    /// - AbortActiveTurn clears _isRequestActive, _currentSession and _isProcessingRequest
    /// - AbortActiveTurn is idempotent: a second call does not re-fire OnSttFailed
    /// - UserErrorLogger.LogRecoverableError emits BOTH the [UserError:...] tag (so the popup can
    ///   show a true message) and the [Recoverable] marker (so it is downgraded to non-fatal)
    ///
    /// BUSINESS IMPACT:
    /// - Without the state reset: the NPC freezes for the rest of the turn and the user only finds
    ///   out after speaking a full sentence into a dead socket.
    /// - Without the [Recoverable] marker: one WiFi hiccup ends a paying customer's training
    ///   session with an "app must restart" popup.
    /// </summary>
    [TestFixture]
    public class RecoverableTurnFailureTests
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

        // -----------------------------------------------------------------
        // AbortActiveTurn — the shared recovery contract
        // -----------------------------------------------------------------

        [Test]
        public void AbortActiveTurn_WithArmedTurn_FiresSttFailedWithConnectionLost()
        {
            SetField("_isRequestActive", true);

            NoTranscriptMessage received = null;
            _orchestrator.OnSttFailed += msg => received = msg;

            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(
                @"\[RequestOrchestrator\] Active turn aborted"));

            InvokeAbort("SessionStart send failed");

            Assert.IsNotNull(received,
                "OnSttFailed must fire so the RuleSystem resets IsReactionBusy and the NPC stays responsive.");
            Assert.AreEqual("ConnectionLost", received.Reason,
                "Reason must be ConnectionLost, not silence — the user did speak, the socket was gone.");
        }

        [Test]
        public void AbortActiveTurn_WithArmedTurn_ClearsAllTurnState()
        {
            SetField("_isRequestActive", true);
            SetField("_isProcessingRequest", true);
            SetField("_currentSession", new ConversationSession("TestNpc", "test-request-id"));

            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(
                @"\[RequestOrchestrator\] Active turn aborted"));

            InvokeAbort("SessionStart send failed");

            Assert.IsFalse(GetField<bool>("_isRequestActive"),
                "_isRequestActive must be cleared so push-to-talk release does not report a second failure.");
            Assert.IsNull(GetField<ConversationSession>("_currentSession"),
                "_currentSession must be cleared so the next push-to-talk on the SAME NPC starts clean.");
            Assert.IsFalse(_orchestrator.IsProcessingRequest(),
                "IsProcessingRequest() must return false so the UI and RuleSystem unblock a new turn.");
        }

        [Test]
        public void AbortActiveTurn_WithoutArmedTurn_DoesNotFireSttFailedButStillClearsStaleSession()
        {
            // A turn that already completed can leave a stale session behind (e.g. the socket died
            // right after the last chunk). Recovery must still clear it, but must NOT report a
            // failure for a turn that was never armed.
            SetField("_currentSession", new ConversationSession("TestNpc", "stale-request-id"));
            SetField("_isProcessingRequest", true);

            var sttFailedFired = false;
            _orchestrator.OnSttFailed += _ => sttFailedFired = true;

            InvokeAbort("SessionStart send failed");

            Assert.IsFalse(sttFailedFired,
                "OnSttFailed must not fire when no turn was armed — that would fabricate a failed turn.");
            Assert.IsNull(GetField<ConversationSession>("_currentSession"),
                "A stale session must be cleared even when _isRequestActive was already false.");
        }

        [Test]
        public void AbortActiveTurn_CalledTwice_FiresSttFailedOnlyOnce()
        {
            // The disconnect handler and the faulted-SessionStart path can both fire for the same
            // turn. The _isRequestActive guard is what keeps that from double-reporting to the
            // RuleSystem, which would evaluate the sttFailed rule twice for one utterance.
            SetField("_isRequestActive", true);

            var fireCount = 0;
            _orchestrator.OnSttFailed += _ => fireCount++;

            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(
                @"\[RequestOrchestrator\] Active turn aborted"));

            InvokeAbort("SessionStart send failed");
            InvokeAbort("WebSocket disconnected");

            Assert.AreEqual(1, fireCount,
                "OnSttFailed must fire exactly once — the second call finds _isRequestActive=false and skips.");
        }

        // -----------------------------------------------------------------
        // UserErrorLogger — the [Recoverable] marker
        // -----------------------------------------------------------------

        /// <summary>
        /// The host project's ErrorHandler registers "[Recoverable]" as a non-fatal pattern and
        /// separately parses "[UserError:...]" for the popup text. Both must be present in a single
        /// log line: the marker suppresses the fatal popup, the tag carries a true message for the
        /// cases where the error is surfaced some other way. Asserting the exact shape here is what
        /// keeps the two repositories in sync — the ErrorHandler side has a mirror test.
        /// </summary>
        [Test]
        public void LogRecoverableError_EmitsBothUserErrorTagAndRecoverableMarker()
        {
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(
                @"^\[UserError:Connection lost\. Please try again\.\] \[Recoverable\] technical detail$"));

            UserErrorLogger.LogRecoverableError("Connection lost. Please try again.", "technical detail");
        }

        /// <summary>
        /// The plain LogError path must stay unmarked, so genuinely unrecoverable conditions
        /// (authentication failure, missing configuration) keep their fatal popup. If this ever
        /// starts emitting [Recoverable], every fatal error in the app silently stops surfacing.
        /// </summary>
        [Test]
        public void LogError_DoesNotEmitRecoverableMarker()
        {
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(
                @"^\[UserError:Authentication failed\.\] technical detail$"));

            UserErrorLogger.LogError("Authentication failed.", "technical detail");
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

        private void InvokeAbort(string context)
        {
            var method = typeof(RequestOrchestrator).GetMethod("AbortActiveTurn", PrivateInstance);
            Assert.IsNotNull(method, "AbortActiveTurn method not found on RequestOrchestrator");
            method.Invoke(_orchestrator, new object[] { context });
        }

        #endregion
    }
}
