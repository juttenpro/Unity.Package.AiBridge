using System.Reflection;
using NUnit.Framework;
using Tsc.AIBridge.Core;
using Tsc.AIBridge.Messages;
using UnityEngine;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: when the player's turn fails, the NPC must be able to answer that
    /// failure — "sorry, I didn't catch that" — in the very rule pass that reacts to it.
    ///
    /// WHY: OnSttFailed is not a notification the RuleSystem gets round to later. Its handler
    /// evaluates the rule graph SYNCHRONOUSLY, inside the event, and what a lesson hangs on a failed
    /// turn is almost always a character-speaks-first line from the same NPC the player was talking
    /// to. While the failed turn still counted as occupying that NPC, StartConversation refused the
    /// line ("still has a turn in flight") and nothing ever retried it — reported 2026-09-11 as a
    /// coach that goes silent in onboarding once the learner presses to talk and says nothing.
    ///
    /// Nothing else frees the NPC in time. A turn with no transcript makes no LLM call, so it
    /// produces no answer and no audio: the release added in 5.14.0 is driven by that turn's audio
    /// ENDING, and this turn has none. What is left is the backend's conversationComplete, which
    /// arrives long after the rule pass is over.
    ///
    /// WHAT: raising the failure frees the NPC BEFORE the event goes out, and leaves the turn live.
    ///
    /// HOW: ask the two questions production asks, at the moment production asks them — from inside
    /// an OnSttFailed handler. Asserting after the event would pass on a release that happens too
    /// late, which is the entire defect.
    ///
    /// SUCCESS CRITERIA:
    /// - Inside the handler the NPC is free, and its next line is accepted by the backstop too
    /// - The failed turn is still live, so its completion and any late message still resolve to it
    /// - Only the NPC whose turn failed is freed
    /// - A failure that names no turn frees nothing
    ///
    /// BUSINESS IMPACT:
    /// - Every "say something when the learner stays quiet" branch depends on this. When it fails the
    ///   lesson dead-ends in silence, with nothing but a console warning to show for it.
    /// </summary>
    [TestFixture]
    public class NpcFreedByFailedTurnTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

        private GameObject _orchestratorObject;
        private RequestOrchestrator _orchestrator;

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

        /// <summary>
        /// The sequence is the point: this is the moment the rule graph runs. A release that happens
        /// after the event returns is exactly the bug, and must not satisfy this test.
        /// </summary>
        [Test]
        public void TheFailedTurnNoLongerOccupiesItsNpcWhileTheRuleGraphRuns()
        {
            StartPlayerTurn("player-turn", "Coach_Alex");

            var npcOccupied = AskInsideFailureHandler("player-turn",
                () => _orchestrator.HasLiveTurnForNpc("Coach_Alex"));

            Assert.IsFalse(npcOccupied,
                "The player's turn produced no transcript, so it will never produce an answer or audio " +
                "and no longer holds the NPC. The rule graph runs inside this event and needs the NPC " +
                "free right now — nothing retries the line it is about to start.");
        }

        [Test]
        public void TheNpcCanStartItsNextLineFromInsideTheFailureHandler()
        {
            StartPlayerTurn("player-turn", "Coach_Alex");

            var recoveryLineAccepted = AskInsideFailureHandler("player-turn",
                () => RegisterNpcTurn("coach-recovery-line", "Coach_Alex"));

            Assert.IsTrue(recoveryLineAccepted,
                "And the orchestrator's own backstop must agree, or the recovery line dies one level " +
                "down instead: prompt composed, request never sent, no reaction and no audio.");
        }

        [Test]
        public void TheFailedTurnStaysLiveSoItsCompletionStillFindsIt()
        {
            StartPlayerTurn("player-turn", "Coach_Alex");

            RaiseFailureFor("player-turn");

            Assert.IsTrue(_orchestrator.IsTurnLive("player-turn"),
                "Freeing the NPC must not release the turn: the backend still sends conversationComplete " +
                "for it, and a completion for a turn nobody tracks any more is dropped — taking its " +
                "routing teardown and its microphone cleanup with it.");
        }

        [Test]
        public void AFailureFreesOnlyTheNpcWhoseTurnFailed()
        {
            StartPlayerTurn("alex-turn", "Coach_Alex");
            RegisterNpcTurn("johan-line", "Coach_Johan");

            RaiseFailureFor("alex-turn");

            Assert.IsFalse(_orchestrator.HasLiveTurnForNpc("Coach_Alex"));
            Assert.IsTrue(_orchestrator.HasLiveTurnForNpc("Coach_Johan"),
                "Johan is mid-answer and still holds his decoder. One turn failing must not hand his " +
                "NPC to a second line that could not be heard.");
        }

        [Test]
        public void AFailureThatNamesNoTurnFreesNothing()
        {
            StartPlayerTurn("player-turn", "Coach_Alex");

            RaiseFailureFor(null);

            Assert.IsTrue(_orchestrator.HasLiveTurnForNpc("Coach_Alex"),
                "A failure message without a RequestId says nothing about which turn died. Freeing " +
                "every NPC on it would let a second line start over one that is still speaking.");
        }

        /// <summary>
        /// Runs <paramref name="question"/> at the point production asks it: inside the OnSttFailed
        /// handler, mid-event, before anything downstream has had a chance to clean up.
        /// </summary>
        private T AskInsideFailureHandler<T>(string failedRequestId, System.Func<T> question)
        {
            var answered = false;
            var answer = default(T);

            void Handler(NoTranscriptMessage _)
            {
                answered = true;
                answer = question();
            }

            _orchestrator.OnSttFailed += Handler;
            try
            {
                RaiseFailureFor(failedRequestId);
            }
            finally
            {
                _orchestrator.OnSttFailed -= Handler;
            }

            Assert.IsTrue(answered, "The handler never ran, so this test proves nothing about ordering.");
            return answer;
        }

        private void RaiseFailureFor(string requestId) =>
            _orchestrator.RaiseSttFailed(new NoTranscriptMessage
            {
                RequestId = requestId,
                Reason = "No speech detected",
                SttProvider = "azure",
            });

        /// <summary>
        /// The turn the player speaks into, registered the way production registers it. NpcName is
        /// deliberately not the NpcId: asset names are not unique in this project, and a conflict
        /// check that matched on the display name would pass a fixture that used one value for both.
        /// </summary>
        private void StartPlayerTurn(string requestId, string npcId)
        {
            var method = typeof(RequestOrchestrator).GetMethod("SetMicSession", PrivateInstance);
            Assert.IsNotNull(method, "SetMicSession not found on RequestOrchestrator");
            method.Invoke(_orchestrator,
                new object[] { new ConversationSession("Alex de coach", requestId, npcId) });
        }

        private bool RegisterNpcTurn(string requestId, string npcId)
        {
            var method = typeof(RequestOrchestrator).GetMethod("RegisterLiveSession", PrivateInstance);
            Assert.IsNotNull(method, "RegisterLiveSession not found on RequestOrchestrator");
            return (bool)method.Invoke(_orchestrator,
                new object[] { new ConversationSession("Alex de coach", requestId, npcId) });
        }
    }
}
