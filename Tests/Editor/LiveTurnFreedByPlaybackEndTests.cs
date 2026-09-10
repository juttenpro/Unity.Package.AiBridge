using System.Reflection;
using NUnit.Framework;
using Tsc.AIBridge.Core;
using UnityEngine;
using UnityEngine.TestTools;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: an NPC that has finished speaking must be able to speak again, without
    /// waiting for the backend to confirm anything.
    ///
    /// WHY: one live turn per NPC is a physical limit — one audio decoder, one streaming player, one
    /// metadata slot — so a second character-speaks-first line is refused while the first is running.
    /// That refusal used to last until <c>conversationComplete</c> released the turn, which made the
    /// NPC's availability depend on a backend message. The text-only backend path sent none at all, so
    /// after its first NPC-initiated line an NPC was refused for the rest of the scene: reported
    /// 2026-09-10 as "the second PromptComposer on the Finished exit produces a prompt but no reaction
    /// and no audio", with "still has a turn in flight" in the console. The backend now sends it, and
    /// these tests make sure a missing or late one can never wedge an NPC again.
    ///
    /// WHAT: playback ending frees the NPC for the next line but does NOT release the turn — a late
    /// message still has to find it, and its routing must outlive its bookkeeping.
    ///
    /// HOW: register turns through the orchestrator's own registration seam and ask the two questions
    /// production asks: <c>HasLiveTurnForNpc</c> (the RuleSystem side, before it mints a turn id) and
    /// registration itself (the backstop).
    ///
    /// SUCCESS CRITERIA:
    /// - Mid-answer ⇒ the NPC is occupied and a second line is refused
    /// - After playback ends ⇒ the NPC is free and a second line is accepted
    /// - The finished turn is still live, so late traffic for it is not orphaned
    /// - Another turn's playback ending frees only that turn's NPC
    ///
    /// BUSINESS IMPACT:
    /// - Every rule chain built out of PromptComposers on each other's Finished exit depends on this;
    ///   when it fails the lesson stops with nothing but a warning to show for it.
    /// </summary>
    [TestFixture]
    public class LiveTurnFreedByPlaybackEndTests
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

        [Test]
        public void AnNpcThatIsStillSpeakingRefusesASecondLine()
        {
            Assert.IsTrue(RegisterNpcTurn("alex-line-1", "Coach_Alex"));

            Assert.IsTrue(_orchestrator.HasLiveTurnForNpc("Coach_Alex"),
                "Mid-answer the NPC is occupied: its decoder is busy and a second line could not be heard.");

            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("Refusing turn"));
            Assert.IsFalse(RegisterNpcTurn("alex-line-2", "Coach_Alex"),
                "The running answer keeps the NPC; the new line is dropped before anything is sent.");
        }

        /// <summary>
        /// The sequence is the point. This is exactly the moment a PromptComposer's Finished port fires:
        /// the audio ended, the node evaluates its Finished subtree synchronously, and the next
        /// PromptComposer in the chain asks to speak — before the backend has confirmed anything.
        /// </summary>
        [Test]
        public void AnNpcIsFreeAgainOnceItsLineHasFinishedPlaying()
        {
            RegisterNpcTurn("alex-line-1", "Coach_Alex");

            // No conversationComplete: this is the case where the backend never sends one.
            _orchestrator.NotifyTurnAudioFinished("alex-line-1");

            Assert.IsFalse(_orchestrator.HasLiveTurnForNpc("Coach_Alex"),
                "The line has been spoken, so the NPC is free — the chained PromptComposer on the " +
                "Finished exit must be allowed to speak, with or without a backend confirmation.");
            Assert.IsTrue(RegisterNpcTurn("alex-line-2", "Coach_Alex"),
                "And the orchestrator's own backstop must agree, or the turn dies one level down.");
        }

        [Test]
        public void AFinishedLineIsStillLiveSoLateTrafficFindsIt()
        {
            RegisterNpcTurn("alex-line-1", "Coach_Alex");

            _orchestrator.NotifyTurnAudioFinished("alex-line-1");

            Assert.IsTrue(_orchestrator.IsTurnLive("alex-line-1"),
                "Playback ending is not proof the backend is done: a completion, a late chunk or a " +
                "timeout still has to resolve to this turn. Freeing the NPC must not release the turn.");
        }

        [Test]
        public void OneNpcsPlaybackEndingLeavesTheOtherNpcOccupied()
        {
            RegisterNpcTurn("alex-line", "Coach_Alex");
            RegisterNpcTurn("johan-line", "Coach_Johan");

            _orchestrator.NotifyTurnAudioFinished("johan-line");

            Assert.IsTrue(_orchestrator.HasLiveTurnForNpc("Coach_Alex"),
                "Alex is still speaking. A turn is freed by ITS OWN playback ending, not by any.");
            Assert.IsFalse(_orchestrator.HasLiveTurnForNpc("Coach_Johan"));
        }

        private bool RegisterNpcTurn(string requestId, string npcId)
        {
            var method = typeof(RequestOrchestrator).GetMethod("RegisterLiveSession", PrivateInstance);
            Assert.IsNotNull(method, "RegisterLiveSession not found on RequestOrchestrator");
            return (bool)method.Invoke(_orchestrator,
                new object[] { new ConversationSession(npcId, requestId, npcId) });
        }
    }
}
