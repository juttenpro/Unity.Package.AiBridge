using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Tsc.AIBridge.Core;
using Tsc.AIBridge.Messages;
using UnityEngine;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: a queued turn must go on the wire with the payload it was STARTED with —
    /// its own messages, its own voice settings, its own context-cache name — no matter what turns start
    /// while it waits.
    ///
    /// WHY: the queue releases as soon as a request is sent, not when the turn ends, so a second turn can
    /// begin while the first is still being assembled. Every per-turn value used to be read off shared
    /// fields (`_currentConversationRequest`, `_activeNpcConfig`, `_activeNpcClient`) at process time. A
    /// character-speaks-first turn for Esra starting during the player's turn with Marc therefore put
    /// Esra's system prompt, chat history, voice and Gemini context-cache name into Marc's SessionStart.
    /// In a medical training product that is a data-isolation failure, not a wrong answer: one persona's
    /// conversation leaks into another's.
    ///
    /// These tests run in EditMode on purpose. The queue-draining coroutine does not tick here, so both
    /// turns stay queued and their captured payloads can be compared directly — which is exactly the
    /// property at stake.
    /// </summary>
    [TestFixture]
    public class ConcurrentTurnPayloadTests
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
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _npcObjects)
                if (go != null)
                    Object.DestroyImmediate(go);
            _npcObjects.Clear();

            if (_orchestratorObject != null)
                Object.DestroyImmediate(_orchestratorObject);
        }

        [Test]
        public void EachQueuedTurnKeepsItsOwnPayload()
        {
            var marc = NewNpc("Marc");
            var esra = NewNpc("Esra");
            SetProvider(new TwoNpcProvider(marc, esra));

            _orchestrator.StartConversationRequest(TurnFor("Marc", "marc-turn", "marc-cache", 0.11f,
                "Marc's system prompt"));
            _orchestrator.StartConversationRequest(TurnFor("Esra", "esra-turn", "esra-cache", 0.99f,
                "Esra's system prompt"));

            var queued = QueuedAudioRequests();
            Assert.AreEqual(2, queued.Count, "Both turns must still be queued in EditMode.");

            var first = queued[0];
            Assert.AreEqual("marc-turn", Field<string>(first, "RequestId"));
            Assert.AreEqual("marc-cache", Field<ConversationRequest>(first, "Request").ContextCacheName,
                "Marc's turn must carry Marc's context-cache name. Reading it off a shared field at " +
                "process time handed Esra's cached system prompt to Marc.");
            Assert.AreEqual(0.11f, Field<ConversationRequest>(first, "Request").TtsStability, 0.0001f,
                "Marc's turn must carry Marc's voice settings.");
            Assert.AreEqual("Marc", Field<NpcClientBase>(first, "NpcClient").NpcName,
                "Marc's turn must carry Marc's client — the one its handler is registered for.");

            var messages = Field<List<ChatMessage>>(first, "Messages");
            Assert.IsNotNull(messages, "The history is snapshotted at start, not fetched at send time.");
            CollectionAssert.IsNotEmpty(messages);
            Assert.AreEqual("Marc's system prompt", messages[0].Content,
                "Marc's turn must carry Marc's messages, not the persona that started a turn after him.");
        }

        [Test]
        public void ASecondTurnDoesNotRewriteTheFirstOnesPayload()
        {
            // The same property stated as the failure it prevents: whatever Esra's turn sets up, Marc's
            // already-queued record must be untouched.
            var marc = NewNpc("Marc");
            var esra = NewNpc("Esra");
            SetProvider(new TwoNpcProvider(marc, esra));

            _orchestrator.StartConversationRequest(TurnFor("Marc", "marc-turn", "marc-cache", 0.11f, "Marc"));
            var beforeSecondTurn = Field<ConversationRequest>(QueuedAudioRequests()[0], "Request");

            _orchestrator.StartConversationRequest(TurnFor("Esra", "esra-turn", "esra-cache", 0.99f, "Esra"));

            Assert.AreSame(beforeSecondTurn, Field<ConversationRequest>(QueuedAudioRequests()[0], "Request"),
                "Marc's queued record must still point at Marc's own request object.");
            Assert.AreEqual("marc-cache", beforeSecondTurn.ContextCacheName);
        }

        [Test]
        public void AProviderMissLeavesNoHalfWrittenState()
        {
            // The shared fields used to be written BEFORE the client lookup, so a miss left
            // _activeNpcConfig pointing at an NPC with no client behind it.
            SetProvider(new NullReturningProvider());

            UnityEngine.TestTools.LogAssert.Expect(LogType.Error,
                new System.Text.RegularExpressions.Regex("returned null for NPC ID"));

            _orchestrator.StartConversationRequest(TurnFor("Ghost", "ghost-turn", null, 0.5f, "Ghost"));

            Assert.IsNull(GetField<INpcConfiguration>("_activeNpcConfig"),
                "A turn that never started must not leave the active NPC pointing at it.");
            Assert.AreEqual(0, QueuedAudioRequests().Count, "Nothing may be queued for a turn that failed to start.");
        }

        // --- helpers ---------------------------------------------------------------------------------

        private static ConversationRequest TurnFor(string npcId, string requestId, string cacheName,
            float stability, string systemPromptLine)
        {
            return new ConversationRequest
            {
                NpcId = npcId,
                RequestId = requestId,
                ContextCacheName = cacheName,
                TtsStability = stability,
                Messages = new List<ChatMessage>
                {
                    new ChatMessage { Role = "system", Content = systemPromptLine }
                },
            };
        }

        private SimpleNpcClient NewNpc(string name)
        {
            var go = new GameObject(name);
            _npcObjects.Add(go);
            var client = go.AddComponent<SimpleNpcClient>();
            var nameField = typeof(SimpleNpcClient).GetField("npcName",
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
            nameField?.SetValue(client, name);
            return client;
        }

        private void SetProvider(INpcProvider provider)
        {
            var field = typeof(RequestOrchestrator).GetField("_npcProvider", PrivateInstance);
            Assert.IsNotNull(field, "_npcProvider not found on RequestOrchestrator");
            field.SetValue(_orchestrator, provider);
        }

        private List<object> QueuedAudioRequests()
        {
            var field = typeof(RequestOrchestrator).GetField("_audioRequestQueue", PrivateInstance);
            Assert.IsNotNull(field, "_audioRequestQueue not found on RequestOrchestrator");
            var result = new List<object>();
            foreach (var item in (System.Collections.IEnumerable)field.GetValue(_orchestrator))
                result.Add(item);
            return result;
        }

        private static T Field<T>(object target, string name)
        {
            var field = target.GetType().GetField(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, $"Field '{name}' not found on {target.GetType().Name}");
            return (T)field.GetValue(target);
        }

        private T GetField<T>(string name)
        {
            var field = typeof(RequestOrchestrator).GetField(name, PrivateInstance);
            Assert.IsNotNull(field, $"Field '{name}' not found on RequestOrchestrator");
            return (T)field.GetValue(_orchestrator);
        }

        private class TwoNpcProvider : INpcProvider
        {
            private readonly Dictionary<string, NpcClientBase> _byId;

            public TwoNpcProvider(NpcClientBase marc, NpcClientBase esra)
            {
                _byId = new Dictionary<string, NpcClientBase> { { "Marc", marc }, { "Esra", esra } };
            }

            public NpcClientBase GetNpcClient(string npcId) => _byId.TryGetValue(npcId, out var c) ? c : null;

            public INpcConfiguration GetNpcConfiguration(string npcId) => null;
        }

        private class NullReturningProvider : INpcProvider
        {
            public NpcClientBase GetNpcClient(string npcId) => null;

            public INpcConfiguration GetNpcConfiguration(string npcId) => null;
        }
    }
}
