using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Tsc.AIBridge.Core;
using Tsc.AIBridge.WebSocket;
using UnityEngine;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: when a turn completes, the orchestrator has to hear about it — for every
    /// NPC that has a turn, not just for the most recent one.
    ///
    /// WHY: each NpcClient owns its own ConversationMetadataHandler, and the orchestrator kept a single
    /// subscription slot that it re-pointed on every turn start, actively unsubscribing the previous NPC.
    /// With turns live on two NPCs, one NPC's conversationComplete therefore reached nobody: its session
    /// was never released, and nothing but the 120-second watchdog would ever close it — which after the
    /// watchdog becomes liveness-based means failing a turn that had actually succeeded. No amount of
    /// keying by RequestId can repair a notification that was never delivered.
    ///
    /// Cancelling the microphone's turn also used to drop the subscriptions, which says nothing about
    /// the other NPCs whose turns are still running. Subscriptions now end when the NpcClient is
    /// destroyed, and all of them when the orchestrator is.
    /// </summary>
    [TestFixture]
    public class CompletionSubscriptionTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

        private GameObject _orchestratorObject;
        private RequestOrchestrator _orchestrator;
        private readonly List<GameObject> _npcObjects = new List<GameObject>();

        [SetUp]
        public void SetUp()
        {
            _orchestratorObject = new GameObject("TestOrchestrator");
            _orchestrator = _orchestratorObject.AddComponent<RequestOrchestrator>();

            // EditMode AddComponent does not run Awake's singleton assignment; install it explicitly so
            // RequestOrchestrator.HasInstance is true inside the metadata handler.
            typeof(RequestOrchestrator).GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static)
                .SetValue(null, _orchestrator);
            typeof(RequestOrchestrator).GetField("_isQuitting", BindingFlags.NonPublic | BindingFlags.Static)
                .SetValue(null, false);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _npcObjects)
                if (go != null)
                    Object.DestroyImmediate(go);
            _npcObjects.Clear();

            typeof(RequestOrchestrator).GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static)
                .SetValue(null, null);

            if (_orchestratorObject != null)
                Object.DestroyImmediate(_orchestratorObject);
        }

        [Test]
        public void BothNpcsCompletionsReachTheOrchestrator()
        {
            // The failure this replaces, stated directly: Esra's completion used to reach nobody because
            // registering Marc's turn had unsubscribed her.
            var marc = NewNpc("Marc");
            var esra = NewNpc("Esra");
            Register(marc);
            Register(esra);
            GiveMicTurn("marc-turn", "Marc");
            RegisterNpcTurn("esra-turn", "Esra");

            esra.MetadataHandler.ProcessMessage(CompleteJson("esra-turn"));
            Assert.IsFalse(_orchestrator.IsTurnLive("esra-turn"),
                "Esra's own completion must release Esra's turn.");
            Assert.IsTrue(_orchestrator.IsTurnLive("marc-turn"), "And leave Marc's alone.");

            marc.MetadataHandler.ProcessMessage(CompleteJson("marc-turn"));
            Assert.IsFalse(_orchestrator.IsTurnLive("marc-turn"));
        }

        [Test]
        public void RegisteringASecondNpcKeepsTheFirstSubscribed()
        {
            var marc = NewNpc("Marc");
            var esra = NewNpc("Esra");

            Register(marc);
            Register(esra);

            Assert.AreEqual(2, SubscriptionCount(),
                "Both NPCs must stay subscribed; the single slot dropped the first one here.");
        }

        [Test]
        public void RegisteringTheSameNpcTwiceDoesNotDuplicate()
        {
            var marc = NewNpc("Marc");

            Register(marc);
            Register(marc);

            Assert.AreEqual(1, SubscriptionCount(), "Same-NPC retries must not accumulate subscriptions.");
        }

        [Test]
        public void CancellingTheMicrophoneTurnKeepsSubscriptions()
        {
            // Cancelling the player's own turn says nothing about the other NPCs still speaking.
            var marc = NewNpc("Marc");
            var esra = NewNpc("Esra");
            Register(marc);
            Register(esra);
            GiveMicTurn("marc-turn", "Marc");

            _orchestrator.CancelCurrentSession("test");

            Assert.AreEqual(2, SubscriptionCount(),
                "Dropping subscriptions here is how the other NPCs' completions went unheard.");
        }

        [Test]
        public void ADestroyedNpcIsPrunedOnTheNextRegistration()
        {
            var marc = NewNpc("Marc");
            Register(marc);
            Assert.AreEqual(1, SubscriptionCount());

            Object.DestroyImmediate(marc.gameObject);
            _npcObjects.Clear();

            Register(NewNpc("Esra"));

            Assert.AreEqual(1, SubscriptionCount(),
                "A destroyed NpcClient must not stay pinned alive by this dictionary for the whole scene.");
        }

        [Test]
        public void EverySubscriptionIsDroppedWhenTheOrchestratorGoesAway()
        {
            Register(NewNpc("Marc"));
            Register(NewNpc("Esra"));

            Invoke("UnregisterConversationCompletionHandler");

            Assert.AreEqual(0, SubscriptionCount());
        }

        // --- helpers ---------------------------------------------------------------------------------

        private SimpleNpcClient NewNpc(string name)
        {
            var go = new GameObject(name);
            _npcObjects.Add(go);
            var client = go.AddComponent<SimpleNpcClient>();

            var nameField = typeof(SimpleNpcClient).GetField("npcName",
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
            Assert.IsNotNull(nameField, "SimpleNpcClient.npcName not found");
            nameField.SetValue(client, name);

            // EditMode AddComponent does not run Awake, so the handler the orchestrator subscribes to
            // has to be installed explicitly. Internal field, visible to this assembly.
            client._metadataHandler = new ConversationMetadataHandler(name, new LatencyTracker(name));
            return client;
        }

        private static string CompleteJson(string requestId) =>
            "{\"type\":\"conversationComplete\",\"requestId\":\"" + requestId + "\"}";

        private void Register(NpcClientBase client) => Invoke("RegisterConversationCompletionHandler", client);

        private void GiveMicTurn(string requestId, string npcId) =>
            Invoke("SetMicSession", new ConversationSession(npcId, requestId, npcId));

        private void RegisterNpcTurn(string requestId, string npcId) =>
            Invoke("RegisterLiveSession", new ConversationSession(npcId, requestId, npcId));

        private int SubscriptionCount()
        {
            var field = typeof(RequestOrchestrator).GetField("_completionSubscriptions", PrivateInstance);
            Assert.IsNotNull(field, "_completionSubscriptions not found on RequestOrchestrator");
            return ((Dictionary<NpcClientBase, ConversationMetadataHandler>)field.GetValue(_orchestrator)).Count;
        }

        private void Invoke(string methodName, params object[] args)
        {
            var method = typeof(RequestOrchestrator).GetMethod(methodName, PrivateInstance);
            Assert.IsNotNull(method, $"{methodName} not found on RequestOrchestrator");
            method.Invoke(_orchestrator, args);
        }
    }
}
