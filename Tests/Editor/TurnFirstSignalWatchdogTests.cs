using System.Reflection;
using NUnit.Framework;
using Tsc.AIBridge.Core;
using UnityEngine;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: when the backend never responds to a turn, the NPC must not stay
    /// silent forever — the turn must fail loudly so the RuleSystem can reset and the player can
    /// simply ask again.
    ///
    /// WHY: 2026-06-12 robustness audit, client high H8. After EndOfSpeech/TextInput there was no
    /// client-side watchdog at all; the client relied entirely on the SERVER's per-stage timeouts
    /// reaching it. Three real cases break that assumption: a half-open TCP connection (WiFi drop
    /// without RST — no app-level keepalive exists in either direction), a backend error path that
    /// skips conversationComplete, and a server hang. In all three, _currentSession stayed armed,
    /// no event ever fired, and the NPC stared at the player in silence until the TCP layer
    /// happened to notice (minutes) or an NPC switch.
    ///
    /// WHAT: a phase-1-only watchdog. It watches a single window — request sent → FIRST backend
    /// response signal (transcript, audio playback start, or completion) — and fails the turn via
    /// the same recovery path as a WebSocket disconnect (RaiseSttFailed + state reset). It
    /// deliberately STOPS once any signal proves the backend is alive: it can therefore never cut
    /// off a long Full-mode monologue or a paused session mid-stream. Paused time does not count
    /// toward the timeout (PauseManager pauses backend streaming, so silence during pause is
    /// legitimate). The decision logic is a pure function so the timing edge cases are testable
    /// without PlayMode.
    ///
    /// SUCCESS CRITERIA:
    /// - verdict logic: disabled → stop; turn ended/replaced → stop; signal seen → stop;
    ///   paused → wait without consuming budget; budget exhausted → fail.
    /// - failing the turn clears _currentSession/_isRequestActive/_isProcessingRequest and raises
    ///   OnSttFailed with a recognizable reason, exactly like the disconnect recovery path.
    /// - transcript and audio-start record themselves as a signal for the current turn.
    /// </summary>
    [TestFixture]
    public class TurnFirstSignalWatchdogTests
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

        #region Verdict logic (pure function)

        [Test]
        public void Evaluate_TimeoutDisabled_StopsWatching()
        {
            var verdict = RequestOrchestrator.EvaluateTurnWatchdog(
                watchedRequestId: "turn-1", currentRequestId: "turn-1", signalSeenForRequestId: null,
                isPaused: false, elapsedSinceSendSeconds: 999f, timeoutSeconds: 0f);

            Assert.AreEqual(RequestOrchestrator.TurnWatchdogVerdict.StopWatching, verdict,
                "timeout 0 disables the watchdog entirely");
        }

        [Test]
        public void Evaluate_TurnEndedOrReplaced_StopsWatching()
        {
            // conversationComplete / disconnect cleanup nulled the session, or a new turn started.
            Assert.AreEqual(RequestOrchestrator.TurnWatchdogVerdict.StopWatching,
                RequestOrchestrator.EvaluateTurnWatchdog("turn-1", null, null, false, 10f, 120f),
                "a cleaned-up session means the turn is settled — nothing to watch");
            Assert.AreEqual(RequestOrchestrator.TurnWatchdogVerdict.StopWatching,
                RequestOrchestrator.EvaluateTurnWatchdog("turn-1", "turn-2", null, false, 10f, 120f),
                "a replaced session means a new turn owns the state — this watchdog must bow out");
        }

        [Test]
        public void Evaluate_SignalSeenForWatchedTurn_StopsWatching()
        {
            var verdict = RequestOrchestrator.EvaluateTurnWatchdog(
                "turn-1", "turn-1", signalSeenForRequestId: "turn-1",
                isPaused: false, elapsedSinceSendSeconds: 119f, timeoutSeconds: 120f);

            Assert.AreEqual(RequestOrchestrator.TurnWatchdogVerdict.StopWatching, verdict,
                "any backend signal (transcript/audio/completion) proves the chain is alive — phase-1 ends, " +
                "so the watchdog can never cut off a long monologue later");
        }

        [Test]
        public void Evaluate_SignalSeenForOlderTurn_DoesNotSatisfyThisTurn()
        {
            var verdict = RequestOrchestrator.EvaluateTurnWatchdog(
                "turn-2", "turn-2", signalSeenForRequestId: "turn-1",
                isPaused: false, elapsedSinceSendSeconds: 130f, timeoutSeconds: 120f);

            Assert.AreEqual(RequestOrchestrator.TurnWatchdogVerdict.FailTurn, verdict,
                "a signal recorded for a PREVIOUS turn must not mask this turn's dead backend");
        }

        [Test]
        public void Evaluate_Paused_WaitsWithoutConsumingBudget()
        {
            var verdict = RequestOrchestrator.EvaluateTurnWatchdog(
                "turn-1", "turn-1", null,
                isPaused: true, elapsedSinceSendSeconds: 500f, timeoutSeconds: 120f);

            Assert.AreEqual(RequestOrchestrator.TurnWatchdogVerdict.KeepWaitingPaused, verdict,
                "PauseManager pauses backend streaming, so silence during pause is legitimate — " +
                "paused time must not count toward the timeout");
        }

        [Test]
        public void Evaluate_WithinBudget_KeepsWaiting()
        {
            var verdict = RequestOrchestrator.EvaluateTurnWatchdog(
                "turn-1", "turn-1", null, false, elapsedSinceSendSeconds: 60f, timeoutSeconds: 120f);

            Assert.AreEqual(RequestOrchestrator.TurnWatchdogVerdict.KeepWaiting, verdict);
        }

        [Test]
        public void Evaluate_BudgetExhausted_FailsTurn()
        {
            var verdict = RequestOrchestrator.EvaluateTurnWatchdog(
                "turn-1", "turn-1", null, false, elapsedSinceSendSeconds: 120f, timeoutSeconds: 120f);

            Assert.AreEqual(RequestOrchestrator.TurnWatchdogVerdict.FailTurn, verdict,
                "no signal of life within the budget: the turn is dead and must fail loudly");
        }

        #endregion

        #region Failing the turn (recovery path)

        [Test]
        public void FailUnresponsiveTurn_ClearsState_AndRaisesSttFailed()
        {
            SetField("_currentSession", new ConversationSession("TestNpc", "turn-1"));
            SetField("_isRequestActive", true);
            SetField("_isProcessingRequest", true);

            AIBridge.Messages.NoTranscriptMessage failed = null;
            _orchestrator.OnSttFailed += msg => failed = msg;

            InvokeFailUnresponsiveTurn("turn-1");

            Assert.IsNull(GetField<ConversationSession>("_currentSession"),
                "the dead turn's session must be cleared so the next PTT starts clean");
            Assert.IsFalse(GetField<bool>("_isRequestActive"),
                "_isRequestActive must be cleared so EndOfSpeech is not sent for the dead RequestId");
            Assert.IsFalse(GetField<bool>("_isProcessingRequest"));
            Assert.IsNotNull(failed,
                "OnSttFailed must fire so the RuleSystem resets IsReactionBusy — same contract as the disconnect path");
            Assert.AreEqual("TurnResponseTimeout", failed.Reason);
        }

        [Test]
        public void FailUnresponsiveTurn_WithoutActiveRequest_StillClearsSession_WithoutEvent()
        {
            // _isRequestActive already false (e.g. pause stopped the recording): no duplicate
            // SttFailed, but the session slot must still be released.
            SetField("_currentSession", new ConversationSession("TestNpc", "turn-1"));
            SetField("_isRequestActive", false);

            var eventCount = 0;
            _orchestrator.OnSttFailed += _ => eventCount++;

            InvokeFailUnresponsiveTurn("turn-1");

            Assert.IsNull(GetField<ConversationSession>("_currentSession"));
            Assert.AreEqual(0, eventCount,
                "mirrors HandleWebSocketDisconnected: SttFailed only fires when a request was still active");
        }

        #endregion

        #region Signal tracking

        [Test]
        public void RaiseTranscriptionReceived_RecordsSignalForCurrentTurn()
        {
            SetField("_currentSession", new ConversationSession("TestNpc", "turn-1"));

            _orchestrator.RaiseTranscriptionReceived("hallo");

            Assert.AreEqual("turn-1", GetField<string>("_turnSignalSeenForRequestId"),
                "a transcript is the first proof of life for an audio turn");
        }

        [Test]
        public void MarkAudioStreamReceived_RecordsSignalForCurrentTurn()
        {
            SetField("_currentSession", new ConversationSession("TestNpc", "turn-1"));

            _orchestrator.MarkAudioStreamReceived();

            Assert.AreEqual("turn-1", GetField<string>("_turnSignalSeenForRequestId"),
                "audio playback start is the first proof of life for an NPC-initiated text turn");
        }

        #endregion

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

        private void InvokeFailUnresponsiveTurn(string requestId)
        {
            var method = typeof(RequestOrchestrator).GetMethod("FailUnresponsiveTurn", PrivateInstance);
            Assert.IsNotNull(method,
                "FailUnresponsiveTurn method not found on RequestOrchestrator — fix not yet implemented");
            method.Invoke(_orchestrator, new object[] { requestId });
        }

        #endregion
    }
}
