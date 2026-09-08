using System.Reflection;
using NUnit.Framework;
using Tsc.AIBridge.Audio.Interruption;
using Tsc.AIBridge.Core;
using UnityEngine;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: when the player interrupts an NPC, the backend must be told to stop that
    /// turn's TTS.
    ///
    /// WHY: otherwise the answer is synthesised in full and streamed to a client that has already stopped
    /// playing it — paid for, and never heard. The notification needs the turn's id, and
    /// ResolveInterruptedTurnId gets it from NpcMessageRouter, which knows which turn an NPC is playing.
    ///
    /// WHAT KEPT BREAKING IT: the id was resolved AFTER `StopAudio()`. Stopping the audio raises
    /// playback-interrupted, the client reports that turn's audio as finished, and with the backend
    /// already done — conversationComplete lands about 200 ms into the speech — that releases the turn's
    /// routing. So the lookup ran against a table cleared one millisecond earlier and returned null,
    /// every time (session log 2026-09-08, release at 09:39:45.117 and the failed lookup at .118). Before
    /// that the router entry was cleared at completion, which had the same effect for a different reason.
    ///
    /// Two fixes, one lesson: this id is only resolvable while the turn's routing is up, so resolve it
    /// before anything that might take the routing down.
    /// </summary>
    [TestFixture]
    public class InterruptedTurnNotificationTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

        private GameObject _npcObject;
        private SimpleNpcClient _npc;

        [SetUp]
        public void SetUp()
        {
            _npcObject = new GameObject("Marc");
            _npc = _npcObject.AddComponent<SimpleNpcClient>();
            typeof(SimpleNpcClient)
                .GetField("npcName", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public)
                .SetValue(_npc, "Marc");

            NpcMessageRouter.Instance.RegisterNpc(_npc);
        }

        [TearDown]
        public void TearDown()
        {
            if (NpcMessageRouter.HasInstance)
            {
                ClearRouterTable("_activeNpcsByRequestId");
                ClearRouterTable("_npcsByName");
            }

            if (_npcObject != null)
                Object.DestroyImmediate(_npcObject);
        }

        [Test]
        public void TheInterruptedTurnIsResolvableWhileItsRoutingIsUp()
        {
            NpcMessageRouter.Instance.SetActiveRequest("marc-turn", "Marc");

            Assert.AreEqual("marc-turn", InterruptionManager.ResolveInterruptedTurnId(_npc),
                "This is the id the backend notification needs. Without it the interrupted answer is " +
                "synthesised in full and never heard.");
        }

        [Test]
        public void ItIsNotResolvableOnceTheRoutingIsGone()
        {
            // States the dependency rather than the symptom: the id LIVES in the routing table. Anything
            // that clears the routing before this lookup silently loses the notification, which is what
            // happened twice — once by clearing the router at conversationComplete, once by calling
            // StopAudio (which cascades into the release) before resolving.
            NpcMessageRouter.Instance.SetActiveRequest("marc-turn", "Marc");
            NpcMessageRouter.Instance.ClearRequest("marc-turn");

            Assert.IsNull(InterruptionManager.ResolveInterruptedTurnId(_npc));
        }

        [Test]
        public void AnUnknownNpcResolvesToNothingRatherThanGuessing()
        {
            // Sending the wrong id is worse than sending none: it stops a turn that was never interrupted.
            Assert.IsNull(InterruptionManager.ResolveInterruptedTurnId(null));
            Assert.IsNull(InterruptionManager.ResolveInterruptedTurnId(_npc),
                "No active request for Marc, so nothing to name.");
        }

        [Test]
        public void TheIdIsResolvedBeforeStoppingTheAudioTakesTheRoutingDown()
        {
            // THE ORDERING BUG, and the only assertion here that would have caught it. The three tests
            // above exercise the lookup in isolation and stayed green while OnInterruptionDetected called
            // it one step too late — which is the same mistake in test design that let two other
            // regressions ship today.
            //
            // The double reproduces the production cascade: StopAudio raises playback-interrupted, the
            // client reports the audio finished, and the orchestrator releases this turn's routing.
            var go = new GameObject("Marc-cascading");
            try
            {
                var npc = go.AddComponent<RoutingClearingNpcClient>();
                typeof(SimpleNpcClient)
                    .GetField("npcName", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public)
                    .SetValue(npc, "Marc-cascading");
                NpcMessageRouter.Instance.RegisterNpc(npc);
                NpcMessageRouter.Instance.SetActiveRequest("marc-turn", "Marc-cascading");

                var manager = new GameObject("Manager").AddComponent<InterruptionManager>();
                manager.SetAddressedNpc(npc, new InterruptionTarget(true, 0.4f));

                var complained = false;
                Application.LogCallback handler = (condition, _, type) =>
                {
                    if (type == LogType.Warning && condition.Contains("could not resolve"))
                        complained = true;
                };

                Application.logMessageReceived += handler;
                try
                {
                    manager.OnInterruptionDetected();
                }
                finally
                {
                    Application.logMessageReceived -= handler;
                    Object.DestroyImmediate(manager.gameObject);
                }

                Assert.IsTrue(npc.StopAudioWasCalled, "precondition: the cascade ran");
                Assert.IsFalse(complained,
                    "The turn id must be resolved BEFORE StopAudio, because stopping the audio is what " +
                    "takes down the routing this lookup reads. Otherwise the backend is never told and " +
                    "the interrupted answer is synthesised in full, unheard, at cost.");
            }
            finally
            {
                if (go != null)
                    Object.DestroyImmediate(go);
            }
        }

        private static void ClearRouterTable(string fieldName)
        {
            var field = typeof(NpcMessageRouter).GetField(fieldName, PrivateInstance);
            if (field?.GetValue(NpcMessageRouter.Instance) is System.Collections.IDictionary table)
                table.Clear();
        }
    }

    /// <summary>
    /// Stands in for the production cascade: StopAudio raises playback-interrupted, the client reports
    /// the turn's audio as finished, and the orchestrator releases that turn's routing.
    /// </summary>
    internal sealed class RoutingClearingNpcClient : SimpleNpcClient
    {
        public bool StopAudioWasCalled { get; private set; }

        public override void StopAudio()
        {
            StopAudioWasCalled = true;
            NpcMessageRouter.Instance.ClearRequest("marc-turn");
        }
    }
}
