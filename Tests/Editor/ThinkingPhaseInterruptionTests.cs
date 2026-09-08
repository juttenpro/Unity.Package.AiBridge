using System.Reflection;
using NUnit.Framework;
using Tsc.AIBridge.Audio.Interruption;
using Tsc.AIBridge.Core;
using UnityEngine;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: an interruption is the player ACTUALLY SPEAKING over an NPC that has the
    /// floor — for that persona's persistence time, and only if that persona may be interrupted at all.
    /// That is one rule, and it holds whether the NPC is audible or still thinking.
    ///
    /// WHY THE THINKING PHASE COUNTS: the overlap monitor used to watch only the audible phase. Between
    /// "the request went out" and "the first chunk plays" there are seconds of LLM and TTS latency in
    /// which the NPC visibly has the floor, and a press there walked past this manager entirely: it
    /// started a plain new turn, nothing cancelled the answer already on its way, and the NPC delivered
    /// it over the player's new turn. From the player's seat, "I cannot interrupt her while she is
    /// thinking" (session log 2026-09-08 07:31).
    ///
    /// WHY NOT A HOLD TIMER: v5.7.0 gated the thinking phase on how long the talk button had been held.
    /// That is not how interruption works anywhere else in this system, and it made a SILENT half-second
    /// press cancel an answer. Holding the button proves nothing; speech does. These tests exist to keep
    /// that distinction, which one release already got wrong.
    /// </summary>
    [TestFixture]
    public class ThinkingPhaseInterruptionTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

        private GameObject _managerObject;
        private InterruptionManager _manager;
        private GameObject _npcObject;
        private SimpleNpcClient _npc;

        [SetUp]
        public void SetUp()
        {
            _managerObject = new GameObject("TestInterruptionManager");
            _manager = _managerObject.AddComponent<InterruptionManager>();

            _npcObject = new GameObject("Marc");
            _npc = _npcObject.AddComponent<SimpleNpcClient>();
            typeof(SimpleNpcClient)
                .GetField("npcName", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public)
                .SetValue(_npc, "Marc");
        }

        [TearDown]
        public void TearDown()
        {
            if (_npcObject != null)
                Object.DestroyImmediate(_npcObject);
            if (_managerObject != null)
                Object.DestroyImmediate(_managerObject);
        }

        #region The gate is speech, not a button hold

        [Test]
        public void SilenceNeverAccumulatesOverlapHoweverLongTheButtonIsHeld()
        {
            // Ten seconds of holding the button without saying anything: still nothing. This is the
            // v5.7.0 mistake stated as a requirement — that version would have interrupted after 0.35s.
            var overlap = 0f;
            for (var i = 0; i < 600; i++)
            {
                overlap = InterruptionManager.UpdateOverlapTimer(
                    currentOverlap: overlap, currentNpcPauseAccumulator: 0f,
                    userSpeaking: false, npcActuallySpeaking: true, npcResponding: true,
                    deltaTime: 1f / 60f, npcPauseTolerance: 0.3f).OverlapTimer;
            }

            Assert.AreEqual(0f, overlap,
                "Holding the talk button is not interrupting. A silent hold must never cancel an answer.");
        }

        [Test]
        public void SpeechOverAThinkingNpcAccumulatesUntilPersistenceIsMet()
        {
            // The thinking NPC holds the floor continuously — it is not pausing, it simply has no audio
            // yet — so the caller passes npcActuallySpeaking: true and the player's speech is the gate.
            var overlap = 0f;
            var ticks = 0;
            while (!InterruptionManager.ShouldInterrupt(overlap, persistenceTime: 0.4f, isNearEnd: false,
                       allowInterruption: true, nearEndPersistenceMultiplier: 0.25f))
            {
                overlap = InterruptionManager.UpdateOverlapTimer(
                    overlap, 0f, userSpeaking: true, npcActuallySpeaking: true, npcResponding: true,
                    deltaTime: 1f / 60f, npcPauseTolerance: 0.3f).OverlapTimer;

                if (++ticks > 600)
                    Assert.Fail("Speech over a thinking NPC never reached the persistence threshold.");
            }

            Assert.GreaterOrEqual(overlap, 0.4f);
            Assert.LessOrEqual(ticks, 25, "0.4s at 60fps is 24 frames — the gate is the persona's, not a constant.");
        }

        [Test]
        public void ANonInterruptiblePersonaIsNeverInterruptedHoweverLongThePlayerTalks()
        {
            Assert.IsFalse(InterruptionManager.ShouldInterrupt(overlapTimer: 99f, persistenceTime: 0.4f,
                isNearEnd: false, allowInterruption: false, nearEndPersistenceMultiplier: 0.25f),
                "AllowInterruption has to mean the same thing in both phases, or content configures " +
                "something that only half applies.");
        }

        #endregion

        #region Who has the floor

        [Test]
        public void AThinkingNpcHasTheFloor()
        {
            // The client owns the turn registry, so it installs this. Without it the manager only ever
            // saw the audible phase.
            _manager.SetAddressedNpc(_npc, new InterruptionTarget(allowInterruption: true, persistenceTime: 0.4f));
            Assert.IsFalse(HasFloor(), "Nothing in flight and nothing audible.");

            _manager.IsTurnOwnerAwaitingResponse = () => true;

            Assert.IsTrue(HasFloor(),
                "A turn that has been requested but is not audible yet is still that NPC's turn.");
        }

        [Test]
        public void WithoutTheDelegateOnlyAudibleSpeechCounts()
        {
            // Fail safe rather than fail open: an unwired client must not make every press an
            // interruption attempt.
            _manager.SetAddressedNpc(_npc, new InterruptionTarget(allowInterruption: true, persistenceTime: 0.4f));
            _manager.IsTurnOwnerAwaitingResponse = null;

            Assert.IsFalse(HasFloor());
        }

        [Test]
        public void ThePolicyComesFromTheAddressedNpc()
        {
            // Same precedence as the overlap monitor: the addressed NPC outranks whichever NPC a request
            // was last started for, which in a room with several speakers is a bystander.
            Assert.IsTrue(_manager.IsInterruptionAllowedForTurnOwner(),
                "With nothing addressed the fallback is permissive on purpose — a scene-load timing gap " +
                "must not produce an NPC nobody can interrupt.");

            _manager.SetAddressedNpc(_npc, new InterruptionTarget(allowInterruption: false, persistenceTime: 0.4f));
            Assert.IsFalse(_manager.IsInterruptionAllowedForTurnOwner());

            _manager.SetAddressedNpc(null, InterruptionTarget.None);
            Assert.IsTrue(_manager.IsInterruptionAllowedForTurnOwner(), "Back to the fallback.");
        }

        #endregion

        private bool HasFloor()
        {
            var prop = typeof(InterruptionManager).GetProperty("TurnOwnerHasFloor", PrivateInstance);
            Assert.IsNotNull(prop, "TurnOwnerHasFloor not found on InterruptionManager");
            return (bool)prop.GetValue(_manager);
        }
    }
}
