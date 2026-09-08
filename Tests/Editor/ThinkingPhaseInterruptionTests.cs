using System.Reflection;
using NUnit.Framework;
using Tsc.AIBridge.Audio.Interruption;
using Tsc.AIBridge.Core;
using UnityEngine;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: whether an NPC may be interrupted is one decision, and it holds in both
    /// phases of a turn — while the NPC is speaking AND while it is still thinking.
    ///
    /// WHY: the overlap monitor can only judge a press that overlaps AUDIO. Between "the request went
    /// out" and "the first chunk plays" there are seconds of LLM and TTS latency in which the NPC is
    /// visibly thinking and the player may well cut in. That phase used to walk straight past this
    /// manager: the client sent a plain PlayerStartsTalking with IsPlayerInterruption=false, nothing
    /// cancelled the answer already on its way, and the NPC then delivered it over the player's new
    /// turn. From the player's seat: "I cannot interrupt her while she is thinking" (session log
    /// 2026-09-08 07:31, where the press matured 222 ms before her audio started).
    ///
    /// So AllowInterruption now decides in both phases, and the persona owns it. The deliberateness
    /// gate differs because it has to: overlap persistence in the audible phase, the client's hold
    /// window in the thinking phase, since there is no audio to measure overlap against.
    /// </summary>
    [TestFixture]
    public class ThinkingPhaseInterruptionTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

        private GameObject _managerObject;
        private InterruptionManager _manager;
        private GameObject _npcObject;

        [SetUp]
        public void SetUp()
        {
            _managerObject = new GameObject("TestInterruptionManager");
            _manager = _managerObject.AddComponent<InterruptionManager>();

            _npcObject = new GameObject("Marc");
            var npc = _npcObject.AddComponent<SimpleNpcClient>();
            typeof(SimpleNpcClient)
                .GetField("npcName", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public)
                .SetValue(npc, "Marc");
            _npc = npc;
        }

        private SimpleNpcClient _npc;

        [TearDown]
        public void TearDown()
        {
            if (_npcObject != null)
                Object.DestroyImmediate(_npcObject);
            if (_managerObject != null)
                Object.DestroyImmediate(_managerObject);
        }

        [Test]
        public void AnInterruptiblePersonaCanBeInterruptedWhileThinking()
        {
            _manager.SetAddressedNpc(_npc, new InterruptionTarget(allowInterruption: true, persistenceTime: 0.4f));

            var raised = 0;
            _manager.OnInterruption += () => raised++;

            Assert.IsTrue(_manager.TryInterruptWhileThinking(),
                "There is no audio to overlap with, so this phase has to be decided on policy alone.");
            Assert.AreEqual(1, raised,
                "OnInterruption is the whole contract: it is what sets IsPlayerInterruption=true and " +
                "sends PlayerStartsTalking. Without it the RuleSystem never learns this was an " +
                "interruption and the answer already on its way is delivered over the new turn.");
        }

        [Test]
        public void ANonInterruptiblePersonaCannotBeInterruptedWhileThinking()
        {
            _manager.SetAddressedNpc(_npc, new InterruptionTarget(allowInterruption: false, persistenceTime: 0.4f));

            var raised = 0;
            _manager.OnInterruption += () => raised++;

            Assert.IsFalse(_manager.TryInterruptWhileThinking(),
                "AllowInterruption has to mean the same thing in both phases, or content configures " +
                "something that only half applies.");
            Assert.AreEqual(0, raised, "And nothing may be raised, so the caller can drop the press.");
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
    }
}
