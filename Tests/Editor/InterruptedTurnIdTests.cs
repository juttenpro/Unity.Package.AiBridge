using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Tsc.AIBridge.Audio.Interruption;
using Tsc.AIBridge.Core;
using UnityEngine;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: when the player talks over an NPC, the backend must be told to stop the TTS
    /// of THAT NPC's turn.
    ///
    /// WHY: the notification was the last thing in the interruption path still taken from the
    /// orchestrator's current session. Everything else already decides on the NPC that was actually
    /// talked over — its own comment calls the request NPC "a bystander in a room with several speakers".
    /// The current session is about to mean strictly "the player's own microphone turn", which would have
    /// the client silence NPC A locally while telling the backend to stop generating speech for the
    /// player's turn: A keeps being synthesised at cost, and an unrelated turn gets cut.
    ///
    /// Resolution goes through the router, which knows which turn belongs to which NPC. One live turn per
    /// NPC is the enforced invariant, so the lookup is unambiguous. When it cannot be resolved, nothing is
    /// sent — the local stop has already happened, and a wrong id would cut a turn nobody interrupted.
    /// </summary>
    [TestFixture]
    public class InterruptedTurnIdTests
    {
        private readonly List<GameObject> _npcObjects = new List<GameObject>();

        [SetUp]
        public void SetUp()
        {
            NpcMessageRouter.Instance.Reset();
        }

        [TearDown]
        public void TearDown()
        {
            NpcMessageRouter.Instance.Reset();

            foreach (var go in _npcObjects)
                if (go != null)
                    Object.DestroyImmediate(go);
            _npcObjects.Clear();
        }

        [Test]
        public void TheInterruptedNpcsOwnTurnIsResolved()
        {
            var marc = NewNpc("Marc");
            var esra = NewNpc("Esra");
            NpcMessageRouter.Instance.RegisterNpc(marc);
            NpcMessageRouter.Instance.RegisterNpc(esra);
            NpcMessageRouter.Instance.SetActiveRequest("marc-turn", "Marc");
            NpcMessageRouter.Instance.SetActiveRequest("esra-turn", "Esra");

            Assert.AreEqual("marc-turn", InterruptionManager.ResolveInterruptedTurnId(marc),
                "Talking over Marc must stop Marc's turn.");
            Assert.AreEqual("esra-turn", InterruptionManager.ResolveInterruptedTurnId(esra),
                "And talking over Esra must stop Esra's — not whichever turn started last.");
        }

        [Test]
        public void AnNpcWithNoLiveTurnResolvesToNothing()
        {
            // Nothing to stop. The caller must send nothing rather than fall back to another turn.
            var marc = NewNpc("Marc");
            NpcMessageRouter.Instance.RegisterNpc(marc);

            Assert.IsNull(InterruptionManager.ResolveInterruptedTurnId(marc));
        }

        [Test]
        public void AnotherNpcsLiveTurnIsNeverBorrowed()
        {
            // The precise failure this replaces: one NPC has a turn, the one being interrupted does not.
            var marc = NewNpc("Marc");
            var esra = NewNpc("Esra");
            NpcMessageRouter.Instance.RegisterNpc(marc);
            NpcMessageRouter.Instance.RegisterNpc(esra);
            NpcMessageRouter.Instance.SetActiveRequest("esra-turn", "Esra");

            Assert.IsNull(InterruptionManager.ResolveInterruptedTurnId(marc),
                "Marc has no turn, so nothing may be stopped — certainly not Esra's.");
        }

        [Test]
        public void NoNpcResolvesToNothing()
        {
            Assert.IsNull(InterruptionManager.ResolveInterruptedTurnId(null));
        }

        private SimpleNpcClient NewNpc(string name)
        {
            var go = new GameObject(name);
            _npcObjects.Add(go);
            var client = go.AddComponent<SimpleNpcClient>();
            var nameField = typeof(SimpleNpcClient).GetField("npcName",
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
            Assert.IsNotNull(nameField, "SimpleNpcClient.npcName not found — the stub cannot be named");
            nameField.SetValue(client, name);
            return client;
        }
    }
}
