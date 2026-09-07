using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Tsc.AIBridge.Core;
using UnityEngine;
using UnityEngine.TestTools;

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
    /// skips conversationComplete, and a server hang. In all three, _micSession stayed armed,
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
    /// - verdict logic: disabled → stop; turn no longer live → stop; signal seen → stop;
    ///   paused → wait without consuming budget; budget exhausted → fail.
    /// - failing the turn clears _micSession/_isRequestActive/_isProcessingRequest and raises
    ///   OnSttFailed with a recognizable reason, exactly like the disconnect recovery path.
    /// - transcript and audio-start record a signal on THEIR OWN turn and on no other.
    ///
    /// STEP 11: both turn-specific inputs are now values read off that turn. They used to be ids
    /// compared against single slots on the orchestrator — "the current request id" was the
    /// MICROPHONE's, so from the moment NPC turns stopped writing that pointer (step 8) an
    /// NPC-initiated turn was never "current", and its watchdog bowed out on its very first tick.
    /// Those turns had no watchdog at all. And the player turn's budget started when SessionStart was
    /// sent, so holding push-to-talk longer than the timeout failed a healthy turn from the inside.
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
                isTurnStillLive: true, firstSignalSeen: false,
                isPaused: false, elapsedSinceArmedSeconds: 999f, timeoutSeconds: 0f);

            Assert.AreEqual(RequestOrchestrator.TurnWatchdogVerdict.StopWatching, verdict,
                "timeout 0 disables the watchdog entirely");
        }

        [Test]
        public void Evaluate_TurnNoLongerLive_StopsWatching()
        {
            // Completed, cancelled, failed or displaced — all four are "not live", and all four mean
            // someone else owns the outcome now.
            Assert.AreEqual(RequestOrchestrator.TurnWatchdogVerdict.StopWatching,
                RequestOrchestrator.EvaluateTurnWatchdog(false, false, false, 10f, 120f),
                "a released turn is settled — nothing to watch");
        }

        [Test]
        public void Evaluate_SignalSeen_StopsWatching()
        {
            var verdict = RequestOrchestrator.EvaluateTurnWatchdog(
                isTurnStillLive: true, firstSignalSeen: true,
                isPaused: false, elapsedSinceArmedSeconds: 119f, timeoutSeconds: 120f);

            Assert.AreEqual(RequestOrchestrator.TurnWatchdogVerdict.StopWatching, verdict,
                "any backend signal (transcript/audio/completion) proves the chain is alive — phase-1 ends, " +
                "so the watchdog can never cut off a long monologue later");
        }

        [Test]
        public void Evaluate_Paused_WaitsWithoutConsumingBudget()
        {
            var verdict = RequestOrchestrator.EvaluateTurnWatchdog(
                isTurnStillLive: true, firstSignalSeen: false,
                isPaused: true, elapsedSinceArmedSeconds: 500f, timeoutSeconds: 120f);

            Assert.AreEqual(RequestOrchestrator.TurnWatchdogVerdict.KeepWaitingPaused, verdict,
                "PauseManager pauses backend streaming, so silence during pause is legitimate — " +
                "paused time must not count toward the timeout");
        }

        [Test]
        public void Evaluate_WithinBudget_KeepsWaiting()
        {
            var verdict = RequestOrchestrator.EvaluateTurnWatchdog(
                true, false, false, elapsedSinceArmedSeconds: 60f, timeoutSeconds: 120f);

            Assert.AreEqual(RequestOrchestrator.TurnWatchdogVerdict.KeepWaiting, verdict);
        }

        [Test]
        public void Evaluate_BudgetExhausted_FailsTurn()
        {
            var verdict = RequestOrchestrator.EvaluateTurnWatchdog(
                true, false, false, elapsedSinceArmedSeconds: 120f, timeoutSeconds: 120f);

            Assert.AreEqual(RequestOrchestrator.TurnWatchdogVerdict.FailTurn, verdict,
                "no signal of life within the budget: the turn is dead and must fail loudly");
        }

        #endregion

        #region What the coroutine actually asks about (state -> verdict)

        [Test]
        public void AnNpcInitiatedTurnIsWatchedAtAll()
        {
            // THE regression this step exists for. Liveness used to be "is this the microphone's
            // session?", and a character-speaks-first turn never is — so it was abandoned on the first
            // tick and a dead backend for that NPC went completely unnoticed.
            Register("esra-turn");

            Assert.AreEqual(RequestOrchestrator.TurnWatchdogVerdict.FailTurn,
                _orchestrator.EvaluateTurnWatchdogFor("esra-turn", 130f),
                "An NPC-initiated turn with no sign of life must fail like any other. Judging it by the " +
                "microphone's pointer meant it was never watched.");
        }

        [Test]
        public void AForeignTurnsSignalDoesNotSatisfyThisTurn()
        {
            // The old single slot held "the id a signal was last seen for", so any turn's signal could
            // answer for any other turn's watchdog. The flag now lives on each session.
            Track("mic-turn");
            Register("esra-turn");

            _orchestrator.MarkAudioStreamReceived("esra-turn");

            Assert.AreEqual(RequestOrchestrator.TurnWatchdogVerdict.StopWatching,
                _orchestrator.EvaluateTurnWatchdogFor("esra-turn", 130f));
            Assert.AreEqual(RequestOrchestrator.TurnWatchdogVerdict.FailTurn,
                _orchestrator.EvaluateTurnWatchdogFor("mic-turn", 130f),
                "Esra's audio says nothing about the player's turn — masking it is how a dead backend " +
                "stayed invisible for the rest of the lesson.");
        }

        [Test]
        public void AReleasedTurnIsNoLongerWatched()
        {
            Register("esra-turn");
            _orchestrator.CompleteSession("esra-turn");

            Assert.AreEqual(RequestOrchestrator.TurnWatchdogVerdict.StopWatching,
                _orchestrator.EvaluateTurnWatchdogFor("esra-turn", 130f));
        }

        [Test]
        public void AnUnknownTurnIsNoLongerWatched()
        {
            Assert.AreEqual(RequestOrchestrator.TurnWatchdogVerdict.StopWatching,
                _orchestrator.EvaluateTurnWatchdogFor(null, 130f),
                "A null id must not throw its way out of a coroutine that runs every second.");
        }

        #endregion

        #region Failing the turn (recovery path)

        [Test]
        public void FailUnresponsiveTurn_ClearsState_AndRaisesSttFailed()
        {
            SetField("_micSession", new ConversationSession("TestNpc", "turn-1"));
            SetField("_isRequestActive", true);
            SetField("_isProcessingRequest", true);

            AIBridge.Messages.NoTranscriptMessage failed = null;
            _orchestrator.OnSttFailed += msg => failed = msg;

            InvokeFailUnresponsiveTurn("turn-1");

            Assert.IsNull(GetField<ConversationSession>("_micSession"),
                "the dead turn's session must be cleared so the next PTT starts clean");
            Assert.IsFalse(GetField<bool>("_isRequestActive"),
                "_isRequestActive must be cleared so EndOfSpeech is not sent for the dead RequestId");
            Assert.IsFalse(GetField<bool>("_isProcessingRequest"));
            Assert.IsNotNull(failed,
                "OnSttFailed must fire so the RuleSystem resets IsReactionBusy — same contract as the disconnect path");
            Assert.AreEqual("TurnResponseTimeout", failed.Reason);
            Assert.AreEqual("turn-1", failed.RequestId,
                "The failure must name the turn it belongs to. The client-side listener releases the turn " +
                "by this id, so without it the turn is never released and that NPC's talk button starts " +
                "swallowing short presses for the rest of the lesson.");
        }

        [Test]
        public void FailUnresponsiveTurn_WithoutActiveRequest_StillClearsSession_WithoutEvent()
        {
            // _isRequestActive already false (e.g. pause stopped the recording): no duplicate
            // SttFailed, but the session slot must still be released.
            SetField("_micSession", new ConversationSession("TestNpc", "turn-1"));
            SetField("_isRequestActive", false);

            var eventCount = 0;
            _orchestrator.OnSttFailed += _ => eventCount++;

            InvokeFailUnresponsiveTurn("turn-1");

            Assert.IsNull(GetField<ConversationSession>("_micSession"));
            Assert.AreEqual(0, eventCount,
                "mirrors HandleWebSocketDisconnected: SttFailed only fires when a request was still active");
        }

        #endregion

        #region Signal tracking

        [Test]
        public void RaiseTranscriptionReceived_RecordsSignalForTheTranscriptsOwnTurn()
        {
            // Two live turns: the microphone's, and the one this transcript belongs to.
            var mic = Track("mic-turn");
            var esra = Register("esra-turn");

            _orchestrator.RaiseTranscriptionReceived("hallo", "esra-turn");

            Assert.IsTrue(esra.FirstSignalSeen,
                "A transcript proves the backend is alive for ITS OWN turn.");
            Assert.IsFalse(mic.FirstSignalSeen,
                "And for no other. Crediting whatever session the orchestrator points at silences that " +
                "turn's watchdog and masks the dead one.");
        }

        [Test]
        public void RaiseTranscriptionReceived_WithoutARequestId_RecordsNothing()
        {
            // A transcript with no id cannot prove anything about any particular turn, and guessing
            // "the current one" is what this fix removes. Warn and record nothing.
            var mic = Track("mic-turn");

            LogAssert.Expect(LogType.Warning, new Regex("without a RequestId"));
            _orchestrator.RaiseTranscriptionReceived("hallo", null);

            Assert.IsFalse(mic.FirstSignalSeen,
                "No id means no proof of life for any turn — never fall back to the current session.");
        }

        [Test]
        public void RaiseTranscriptionReceived_ForATurnThatIsGone_RecordsNothing()
        {
            // A late transcript for a cancelled or displaced turn must not resurrect it, and must not
            // land on whatever turn happens to be live now.
            var mic = Track("mic-turn");

            _orchestrator.RaiseTranscriptionReceived("hallo", "already-finished");

            Assert.IsFalse(mic.FirstSignalSeen);
        }

        [Test]
        public void RaiseTranscriptionReceived_StillForwardsTheTranscript()
        {
            // The stamp is a side effect; the event is the method's actual job and must not depend on it.
            string forwardedTranscript = null;
            string forwardedId = null;
            _orchestrator.OnTranscriptionReceived += (t, id) => { forwardedTranscript = t; forwardedId = id; };

            _orchestrator.RaiseTranscriptionReceived("hallo", "turn-1");

            Assert.AreEqual("hallo", forwardedTranscript);
            Assert.AreEqual("turn-1", forwardedId);
        }

        [Test]
        public void MarkAudioStreamReceived_RecordsSignalForTheTurnWhoseAudioItIs()
        {
            // Two live turns, and the audio belongs to the one that is NOT the microphone's. The old
            // version set the microphone's session to the same id it implicitly used, so it passed while
            // the code credited whatever the microphone pointed at.
            var mic = Track("mic-turn");
            var esra = Register("esra-turn");

            _orchestrator.MarkAudioStreamReceived("esra-turn");

            Assert.IsTrue(esra.FirstSignalSeen,
                "Audio proves the backend is alive for the turn whose audio it is — crediting the " +
                "microphone's turn instead silences that turn's watchdog and masks the dead one.");
            Assert.IsFalse(mic.FirstSignalSeen);
            Assert.AreEqual(1, esra.StreamsReceived);
            Assert.AreEqual(0, _orchestrator.GetStreamsReceived("mic-turn"),
                "The microphone's turn produced no audio and must not be marked as if it had — that flag " +
                "decides whether its completion still has to clean the turn up.");
        }

        [Test]
        public void MarkAudioStreamReceived_WithoutARequestId_RecordsNothing()
        {
            var mic = Track("mic-turn");

            LogAssert.Expect(LogType.Warning, new Regex("Audio started for an unnamed turn"));
            _orchestrator.MarkAudioStreamReceived(null);

            Assert.IsFalse(mic.FirstSignalSeen,
                "No id means no proof of life for any particular turn — never fall back to the microphone's.");
            Assert.AreEqual(0, _orchestrator.GetStreamsReceived("mic-turn"));
        }

        [Test]
        public void MarkAudioStreamReceived_ForATurnThatIsGone_RecordsNothing()
        {
            // Late audio for a turn that was cancelled or displaced must not resurrect it.
            var mic = Track("mic-turn");

            _orchestrator.MarkAudioStreamReceived("already-finished");

            Assert.IsFalse(mic.FirstSignalSeen);
            Assert.AreEqual(0, _orchestrator.GetStreamsReceived("mic-turn"));
        }

        private ConversationSession Track(string requestId)
        {
            var session = new ConversationSession("TestNpc", requestId, "TestNpc");
            var method = typeof(RequestOrchestrator).GetMethod("SetMicSession", PrivateInstance);
            Assert.IsNotNull(method, "SetMicSession not found on RequestOrchestrator");
            method.Invoke(_orchestrator, new object[] { session });
            return session;
        }

        private ConversationSession Register(string requestId)
        {
            var session = new ConversationSession("Esra", requestId, "Esra");
            var method = typeof(RequestOrchestrator).GetMethod("RegisterLiveSession", PrivateInstance);
            Assert.IsNotNull(method, "RegisterLiveSession not found on RequestOrchestrator");
            method.Invoke(_orchestrator, new object[] { session });
            return session;
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
