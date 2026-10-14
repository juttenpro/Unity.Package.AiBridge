using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Tsc.AIBridge.Core;
using UnityEngine;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: content can make an NPC cut off its own running turn to say something new
    /// (play mode Replace), or wait for it to end (play mode Queue).
    ///
    /// WHY: a character-speaks-first line for an NPC that still had a turn in flight was refused, with
    /// no retry. In the Agressietraining that dropped exactly the lines that matter most — Lucius
    /// cutting off a player who talks too long, or reacting to being interrupted (test round
    /// 2026-10-12, "Ignored a character-speaks-first line for 'LuciusDeVries'").
    ///
    /// WHAT: <c>CancelLiveTurnForNpc</c> ends whatever turn occupies that NPC — an NPC line or the
    /// player's own microphone turn — and <c>OnNpcFreed</c> tells the client the moment an NPC can
    /// speak again, whichever path freed it.
    ///
    /// NOT COVERED: the backend actually stopping generation on SessionCancel, and audio that was
    /// already in flight. Those need a real run.
    /// </summary>
    [TestFixture]
    public class NpcTurnCancelledForReplacementTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

        private GameObject _orchestratorObject;
        private RequestOrchestrator _orchestrator;
        private List<string> _freedNpcs;

        [SetUp]
        public void SetUp()
        {
            _orchestratorObject = new GameObject("TestOrchestrator");
            _orchestrator = _orchestratorObject.AddComponent<RequestOrchestrator>();
            _freedNpcs = new List<string>();
            _orchestrator.OnNpcFreed += npcId => _freedNpcs.Add(npcId);
        }

        [TearDown]
        public void TearDown()
        {
            if (_orchestratorObject != null)
                Object.DestroyImmediate(_orchestratorObject);
        }

        [Test]
        public void CancellingAnNpcsLineFreesItForTheReplacingLine()
        {
            RegisterNpcTurn("lucius-line-1", "LuciusDeVries");

            var cancelled = _orchestrator.CancelLiveTurnForNpc("LuciusDeVries", "Replaced by a new line");

            Assert.AreEqual("lucius-line-1", cancelled, "The caller needs the id to close the turn's callbacks.");
            Assert.IsFalse(_orchestrator.HasLiveTurnForNpc("LuciusDeVries"),
                "Replace was refused before: the NPC still counted as busy after it was told to stop.");
            Assert.IsFalse(_orchestrator.IsTurnLive("lucius-line-1"),
                "A cancelled turn produces nothing more, so it must not linger in the live set.");
            Assert.IsTrue(RegisterNpcTurn("lucius-line-2", "LuciusDeVries"),
                "The orchestrator's own backstop must accept the replacing line too.");
        }

        [Test]
        public void CancellingThePlayersTurnLetsTheNpcCutThemOff()
        {
            SetMicSession("player-turn", "LuciusDeVries");

            var cancelled = _orchestrator.CancelLiveTurnForNpc("LuciusDeVries", "NPC cuts the player off");

            Assert.AreEqual("player-turn", cancelled);
            Assert.IsFalse(_orchestrator.HasLiveTurnForNpc("LuciusDeVries"),
                "'Te lang praten': Lucius must be able to speak over a player who is still talking.");
        }

        [Test]
        public void CancellingOneNpcLeavesTheOtherSpeaking()
        {
            RegisterNpcTurn("lucius-line", "LuciusDeVries");
            RegisterNpcTurn("colleague-line", "Colleague");

            _orchestrator.CancelLiveTurnForNpc("LuciusDeVries", "Replaced");

            Assert.IsTrue(_orchestrator.HasLiveTurnForNpc("Colleague"),
                "Replace on one NPC must never silence a bystander.");
        }

        [Test]
        public void CancellingAnIdleNpcIsHarmless()
        {
            Assert.IsNull(_orchestrator.CancelLiveTurnForNpc("LuciusDeVries", "Replaced"));
            Assert.IsEmpty(_freedNpcs, "Nothing was occupied, so nothing was freed.");
        }

        [Test]
        public void TheNpcIsReportedFreeWhenItsLineFinishesPlaying()
        {
            RegisterNpcTurn("lucius-line", "LuciusDeVries");

            _orchestrator.NotifyTurnAudioFinished("lucius-line");

            CollectionAssert.AreEqual(new[] { "LuciusDeVries" }, _freedNpcs,
                "A queued line waits for this signal; without it the queued line never plays.");
        }

        [Test]
        public void TheNpcIsReportedFreeWhenItsTurnIsCancelled()
        {
            RegisterNpcTurn("lucius-line", "LuciusDeVries");

            _orchestrator.CancelLiveTurnForNpc("LuciusDeVries", "Replaced");

            CollectionAssert.AreEqual(new[] { "LuciusDeVries" }, _freedNpcs);
        }

        [Test]
        public void TheNpcIsReportedFreeWhenItsTurnCompletesWithoutAudio()
        {
            RegisterNpcTurn("lucius-line", "LuciusDeVries");

            _orchestrator.CompleteSession("lucius-line");

            CollectionAssert.AreEqual(new[] { "LuciusDeVries" }, _freedNpcs,
                "A turn that never produced audio is freed by its completion, not by playback ending.");
        }

        /// <summary>
        /// Playback ends first (NPC freed), then the backend completes the same turn. That second
        /// release must not report the NPC free again: a second report would start a second queued
        /// line on top of the first.
        /// </summary>
        [Test]
        public void TheNpcIsReportedFreeOnlyOncePerTurn()
        {
            RegisterNpcTurn("lucius-line", "LuciusDeVries");

            _orchestrator.NotifyTurnAudioFinished("lucius-line");
            _orchestrator.CompleteSession("lucius-line");

            CollectionAssert.AreEqual(new[] { "LuciusDeVries" }, _freedNpcs);
        }

        [Test]
        public void AnotherNpcFinishingDoesNotReportThisOneFree()
        {
            RegisterNpcTurn("lucius-line", "LuciusDeVries");
            RegisterNpcTurn("colleague-line", "Colleague");

            _orchestrator.NotifyTurnAudioFinished("colleague-line");

            CollectionAssert.AreEqual(new[] { "Colleague" }, _freedNpcs);
        }

        private bool RegisterNpcTurn(string requestId, string npcId)
        {
            var method = typeof(RequestOrchestrator).GetMethod("RegisterLiveSession", PrivateInstance);
            Assert.IsNotNull(method, "RegisterLiveSession not found on RequestOrchestrator");
            return (bool)method.Invoke(_orchestrator,
                new object[] { new ConversationSession(npcId, requestId, npcId) });
        }

        private void SetMicSession(string requestId, string npcId)
        {
            var method = typeof(RequestOrchestrator).GetMethod("SetMicSession", PrivateInstance);
            Assert.IsNotNull(method, "SetMicSession not found on RequestOrchestrator");
            method.Invoke(_orchestrator, new object[] { new ConversationSession(npcId, requestId, npcId) });
        }
    }
}
