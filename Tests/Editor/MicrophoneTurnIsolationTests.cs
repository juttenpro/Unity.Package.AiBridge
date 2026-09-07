using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Tsc.AIBridge.Core;
using UnityEngine;
using UnityEngine.TestTools;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: only the player's own turn may disarm the player's microphone.
    ///
    /// WHY: the orchestrator tracked one session and every turn wrote it. A character-speaks-first turn
    /// starting or ending while the player was recording therefore cleared the microphone's bookkeeping,
    /// and the push-to-talk release then found no active request: no EndOfSpeech, no transcript, no
    /// sttFailed, the RuleSystem stayed busy and the NPC was mute until the player switched NPCs. That is
    /// client-critical C4 from the 2026-06-12 audit, and it was reachable in production because the
    /// request queue releases as soon as a request is SENT, not when the turn ends.
    ///
    /// The microphone's turn is now a distinct thing from "some turn". These tests pin that separation
    /// from both directions: a foreign turn ending must leave the microphone alone, and the microphone's
    /// own turn must still behave exactly as it did.
    /// </summary>
    [TestFixture]
    public class MicrophoneTurnIsolationTests
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
        public void AForeignTurnCompletingLeavesTheMicrophoneArmed()
        {
            // The player is recording into turn M for Marc; Esra runs a turn of her own and it completes.
            GiveMicTurn("mic-turn", "Marc");
            RegisterNpcTurn("esra-turn", "Esra");
            SetField("_isRequestActive", true);

            Invoke("HandleConversationCompleted", "esra-turn", false);

            Assert.IsTrue(GetField<bool>("_isRequestActive"),
                "Esra's turn completing must not disarm the microphone — the player is still speaking. " +
                "This is incident C4: push-to-talk release then found no active request.");
            Assert.AreEqual("mic-turn", _orchestrator.GetMicrophoneSessionId(),
                "The microphone's own turn must survive a foreign completion.");
            Assert.IsFalse(_orchestrator.IsTurnLive("esra-turn"), "Esra's turn is finished and released.");
            Assert.IsTrue(_orchestrator.IsTurnLive("mic-turn"));
        }

        [Test]
        public void TheMicrophonesOwnCompletionStillDisarmsIt()
        {
            // The other direction: nothing about the split may make the normal path stop working.
            GiveMicTurn("mic-turn", "Marc");
            SetField("_isRequestActive", true);

            Invoke("HandleConversationCompleted", "mic-turn", true);

            Assert.IsFalse(GetField<bool>("_isRequestActive"));
            Assert.IsNull(_orchestrator.GetMicrophoneSessionId());
            Assert.IsFalse(_orchestrator.IsTurnLive("mic-turn"));
        }

        [Test]
        public void AForeignTurnTimingOutLeavesTheMicrophoneArmed()
        {
            // A bystander's watchdog firing must not tell the RuleSystem the player said nothing.
            GiveMicTurn("mic-turn", "Marc");
            RegisterNpcTurn("esra-turn", "Esra");
            SetField("_isRequestActive", true);

            AIBridge.Messages.NoTranscriptMessage failed = null;
            _orchestrator.OnSttFailed += msg => failed = msg;

            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("No backend response for turn"));
            Invoke("FailUnresponsiveTurn", "esra-turn");

            Assert.IsNull(failed,
                "Reporting 'no transcript' for a bystander would cut short an utterance the player is " +
                "still speaking into their own turn.");
            Assert.IsTrue(GetField<bool>("_isRequestActive"));
            Assert.AreEqual("mic-turn", _orchestrator.GetMicrophoneSessionId());
            Assert.IsFalse(_orchestrator.IsTurnLive("esra-turn"));
        }

        [Test]
        public void ADroppedSocketFailsEveryLiveTurn()
        {
            // One shared flag cannot express which of several turns died, so each is failed by id.
            GiveMicTurn("mic-turn", "Marc");
            RegisterNpcTurn("esra-turn", "Esra");
            RegisterNpcTurn("jeroen-turn", "Jeroen");
            SetField("_isRequestActive", true);

            LogAssert.ignoreFailingMessages = true;
            Invoke("AbortAllLiveTurns", "socket dropped");
            LogAssert.ignoreFailingMessages = false;

            Assert.IsFalse(_orchestrator.IsTurnLive("mic-turn"));
            Assert.IsFalse(_orchestrator.IsTurnLive("esra-turn"));
            Assert.IsFalse(_orchestrator.IsTurnLive("jeroen-turn"));
            Assert.IsFalse(GetField<bool>("_isRequestActive"));
            Assert.IsNull(_orchestrator.GetMicrophoneSessionId());
        }

        [Test]
        public void AnNpcTurnNeverBecomesTheMicrophonesTurn()
        {
            // The direct statement of the split: registering a turn for an NPC must not touch the pointer
            // push-to-talk release depends on.
            RegisterNpcTurn("esra-turn", "Esra");

            Assert.IsNull(_orchestrator.GetMicrophoneSessionId(),
                "A character-speaks-first turn is not something the player is talking into.");
            Assert.IsTrue(_orchestrator.IsTurnLive("esra-turn"));
        }

        [Test]
        public void OneNpcCannotHoldTwoLiveTurns()
        {
            // There is one audio decoder per NPC, so a second concurrent turn for the same NPC cannot be
            // played anyway. Releasing the older one keeps the live set honest; the watchdog would
            // otherwise fail a turn that had actually succeeded.
            RegisterNpcTurn("esra-turn-1", "Esra");

            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("already had a live turn"));
            RegisterNpcTurn("esra-turn-2", "Esra");

            Assert.IsFalse(_orchestrator.IsTurnLive("esra-turn-1"));
            Assert.IsTrue(_orchestrator.IsTurnLive("esra-turn-2"));
        }

        [Test]
        public void TurnsForDifferentNpcsCoexist()
        {
            RegisterNpcTurn("esra-turn", "Esra");
            RegisterNpcTurn("jeroen-turn", "Jeroen");

            Assert.IsTrue(_orchestrator.IsTurnLive("esra-turn"));
            Assert.IsTrue(_orchestrator.IsTurnLive("jeroen-turn"),
                "N NPCs concurrently is the whole point; only same-NPC overlap is refused.");
        }

        [Test]
        public void TheTurnRemembersWhatToDoWhenThePlayerTurnsAway()
        {
            // Carried on the session because the decision is taken when the turn is ABANDONED, long
            // after its request record is gone.
            var session = new ConversationSession("Marc", "mic-turn", "Marc", PlayerTurnsAwayPolicy.LetAnswerFinish);

            Assert.AreEqual(PlayerTurnsAwayPolicy.LetAnswerFinish, session.OnPlayerTurnsAway);
            Assert.AreEqual("Marc", session.NpcId);
        }

        [Test]
        public void TheDefaultPolicyIsTheBehaviourEveryExistingScenarioAlreadyHas()
        {
            Assert.AreEqual(PlayerTurnsAwayPolicy.CancelAnswer,
                new ConversationSession("Marc", "mic-turn").OnPlayerTurnsAway,
                "Changing this default would silently change every existing course.");
            Assert.AreEqual(PlayerTurnsAwayPolicy.CancelAnswer, new ConversationRequest().OnPlayerTurnsAway);
        }

        // --- helpers ---------------------------------------------------------------------------------

        private void GiveMicTurn(string requestId, string npcId)
        {
            var method = typeof(RequestOrchestrator).GetMethod("SetMicSession", PrivateInstance);
            Assert.IsNotNull(method, "SetMicSession not found on RequestOrchestrator");
            method.Invoke(_orchestrator, new object[] { new ConversationSession(npcId, requestId, npcId) });
        }

        private void RegisterNpcTurn(string requestId, string npcId)
        {
            var method = typeof(RequestOrchestrator).GetMethod("RegisterLiveSession", PrivateInstance);
            Assert.IsNotNull(method, "RegisterLiveSession not found on RequestOrchestrator");
            method.Invoke(_orchestrator, new object[] { new ConversationSession(npcId, requestId, npcId) });
        }

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

        private void Invoke(string methodName, params object[] args)
        {
            var method = typeof(RequestOrchestrator).GetMethod(methodName, PrivateInstance);
            Assert.IsNotNull(method, $"{methodName} not found on RequestOrchestrator");
            method.Invoke(_orchestrator, args);
        }
    }
}
