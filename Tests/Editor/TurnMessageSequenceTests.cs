using System.Collections.Generic;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using Tsc.AIBridge.Core;
using Tsc.AIBridge.WebSocket;
using UnityEngine;
using UnityEngine.TestTools;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: a turn's messages keep reaching their NPC until that turn's audio is
    /// actually finished — in the order the backend really sends them.
    ///
    /// WHY THIS FIXTURE EXISTS: v5.6.1 fixed a regression that 464 passing tests had missed, and it was
    /// missed for a structural reason worth stating. Every other orchestrator test asserts a state
    /// transition in isolation: call this method, check that field. Not one of them knew the ORDER in
    /// which the backend speaks. So when v5.6.0 tore a turn's routing down at `conversationComplete`, the
    /// test written alongside it asserted exactly that — "after completing, the turn is no longer routed"
    /// — and passed. The test and the code shared one unverified premise: that `conversationComplete`
    /// means the turn is done talking. It does not. The backend sends it about 200 ms after the FIRST
    /// audio chunk.
    ///
    /// The cost of that premise, from the session log of 2026-09-08 06:59: the rest of the turn's audio
    /// and its `AudioStreamEnd` arrived with no handler and were dropped, the NpcClient never closed the
    /// stream, the NPC stayed "talking" with a 0.00 s buffer, the player's next talk-button press was
    /// classified as an interruption attempt and discarded without ever reaching the RuleSystem, and the
    /// turn only ended 15 seconds later on the playback safety net.
    ///
    /// WHAT IS DIFFERENT HERE: these tests drive the real <see cref="WebSocketClient"/> routing and the
    /// real <see cref="RequestOrchestrator"/> completion path with a realistic message SEQUENCE, framed
    /// on the wire the way the backend frames it — including the binary audio header, so the format is
    /// pinned too. Nothing asserts an internal field the code happens to write; every assertion is about
    /// what did or did not arrive at the NPC.
    ///
    /// WHAT IT STILL CANNOT SEE: real backend timing, the 15-second safety net, and audio actually
    /// decoding. The recording client records delivery and does not run the audio pipeline. A play
    /// session is still the only proof that a turn works end to end.
    /// </summary>
    [TestFixture]
    public class TurnMessageSequenceTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
        private const byte AudioDataMarker = 0xAD;

        private GameObject _orchestratorObject;
        private RequestOrchestrator _orchestrator;
        private GameObject _socketObject;
        private WebSocketClient _socket;
        private readonly List<GameObject> _npcObjects = new List<GameObject>();

        [SetUp]
        public void SetUp()
        {
            _orchestratorObject = new GameObject("TestOrchestrator");
            _orchestrator = _orchestratorObject.AddComponent<RequestOrchestrator>();

            // EditMode AddComponent runs no Awake, so the singleton the metadata handler looks up and the
            // socket the orchestrator resolves both have to be installed by hand.
            typeof(RequestOrchestrator).GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static)
                .SetValue(null, _orchestrator);
            typeof(RequestOrchestrator).GetField("_isQuitting", BindingFlags.NonPublic | BindingFlags.Static)
                .SetValue(null, false);

            _socketObject = new GameObject("TestWebSocketClient");
            _socket = _socketObject.AddComponent<WebSocketClient>();
            typeof(RequestOrchestrator).GetField("_webSocketClient", PrivateInstance)
                .SetValue(_orchestrator, _socket);
        }

        [TearDown]
        public void TearDown()
        {
            if (NpcMessageRouter.HasInstance)
            {
                ClearRouterTable("_activeNpcsByRequestId");
                ClearRouterTable("_npcsByName");
            }

            typeof(RequestOrchestrator).GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static)
                .SetValue(null, null);

            foreach (var go in _npcObjects)
                if (go != null)
                    Object.DestroyImmediate(go);
            _npcObjects.Clear();

            if (_socketObject != null)
                Object.DestroyImmediate(_socketObject);
            if (_orchestratorObject != null)
                Object.DestroyImmediate(_orchestratorObject);
        }

        [Test]
        public void AudioAndStreamEndAfterConversationCompleteStillReachTheNpc()
        {
            // THE SEQUENCE THE BACKEND ACTUALLY SENDS. Every step is a real frame through real routing.
            var marc = StartTurn("marc-turn", "Marc");

            DeliverAudio("marc-turn", 1, 2, 3);
            Assert.AreEqual(1, marc.AudioChunks.Count, "precondition: the first chunk routes normally");

            DeliverText(CompleteJson("marc-turn"));
            Assert.IsFalse(_orchestrator.IsTurnLive("marc-turn"),
                "the turn's BOOKKEEPING is over at conversationComplete — that part was right");

            // ...and the backend keeps talking, because completion refers to the LLM/TTS request, not to
            // the audio still on its way.
            DeliverAudio("marc-turn", 4, 5, 6);
            DeliverText(StreamEndJson("marc-turn"));

            Assert.AreEqual(2, marc.AudioChunks.Count,
                "Audio after conversationComplete must still reach the NPC. Dropping it does not merely " +
                "lose sound: WebSocketClient logs a Debug.LogError for unroutable audio, and " +
                "ErrorHandler.Classify turns an unmatched error into the restart popup — one dropped " +
                "chunk ends the lesson.");
            CollectionAssert.AreEqual(new byte[] { 4, 5, 6 }, marc.AudioChunks[1]);
            Assert.IsTrue(marc.SawStreamEnd,
                "AudioStreamEnd must still reach the NPC. Without it the client never closes the stream, " +
                "so the NPC stays 'talking' with an empty buffer and the player's next press is read as " +
                "an interruption attempt and silently discarded (session log 2026-09-08 06:59).");
        }

        [Test]
        public void AnAbandonedTurnKeepsDeliveringWhenContentAsksTheAnswerToFinish()
        {
            // PlayerTurnsAwayPolicy.LetAnswerFinish: the turn is released LOCALLY and the backend is
            // deliberately not cancelled, so the whole answer still has to arrive and play. This is the
            // OR-scenario requirement, and tearing the routing down made it impossible.
            var marc = StartTurn("marc-turn", "Marc", asMicrophoneTurn: true);

            _orchestrator.CancelCurrentSession("player turned to Esra", cancelBackendAnswer: false);
            Assert.IsFalse(_orchestrator.IsTurnLive("marc-turn"));

            DeliverAudio("marc-turn", 7, 8);
            DeliverText(StreamEndJson("marc-turn"));

            Assert.AreEqual(1, marc.AudioChunks.Count, "Marc still finishes his sentence.");
            Assert.IsTrue(marc.SawStreamEnd);
        }

        [Test]
        public void TwoNpcsTalkingAtOnceNeverReceiveEachOthersFrames()
        {
            // The whole point of the plan: an OR team talking over each other. Interleaved on purpose,
            // and one turn completes while the other is still streaming.
            var marc = StartTurn("marc-turn", "Marc");
            var esra = StartTurn("esra-turn", "Esra");

            DeliverAudio("marc-turn", 1);
            DeliverAudio("esra-turn", 2);
            DeliverText(CompleteJson("marc-turn"));
            DeliverAudio("esra-turn", 3);
            DeliverAudio("marc-turn", 4);
            DeliverText(StreamEndJson("esra-turn"));

            CollectionAssert.AreEqual(new byte[] { 1 }, marc.AudioChunks[0]);
            CollectionAssert.AreEqual(new byte[] { 4 }, marc.AudioChunks[1]);
            Assert.AreEqual(2, marc.AudioChunks.Count, "Marc heard only Marc's frames.");
            Assert.IsFalse(marc.SawStreamEnd, "Esra's stream end is not Marc's.");

            CollectionAssert.AreEqual(new byte[] { 2 }, esra.AudioChunks[0]);
            CollectionAssert.AreEqual(new byte[] { 3 }, esra.AudioChunks[1]);
            Assert.AreEqual(2, esra.AudioChunks.Count);
            Assert.IsTrue(esra.SawStreamEnd);

            Assert.IsFalse(_orchestrator.IsTurnLive("marc-turn"));
            Assert.IsTrue(_orchestrator.IsTurnLive("esra-turn"),
                "Marc's completion released Marc's turn and nothing else.");
        }

        [Test]
        public void OneNpcsCompletionDoesNotSilenceAnothersStream()
        {
            // The narrow version of the above, stated as its own requirement: the failure mode is that a
            // completion for turn A drops the routing of turn B.
            StartTurn("marc-turn", "Marc");
            var esra = StartTurn("esra-turn", "Esra");

            DeliverText(CompleteJson("marc-turn"));
            DeliverAudio("esra-turn", 9);

            Assert.AreEqual(1, esra.AudioChunks.Count);
        }

        [Test]
        public void UnroutableAudioIsLoud()
        {
            // The other half of the contract: audio nobody can place must NOT pass silently. This is the
            // error that would have ended the lesson above, so its presence is deliberate — a future
            // change that starts dropping audio has to trip over this test.
            LogAssert.Expect(LogType.Error,
                new System.Text.RegularExpressions.Regex("No NPC handler registered for RequestId"));

            DeliverAudio("nobody-registered-this", 1);
        }

        // --- driving the wire ------------------------------------------------------------------------

        /// <summary>
        /// Registers a turn the way ProcessTextRequest / ProcessAudioRequest do: live set, completion
        /// subscription, router entry, socket handler.
        /// </summary>
        private RecordingNpcClient StartTurn(string requestId, string npcId, bool asMicrophoneTurn = false)
        {
            var go = new GameObject(npcId);
            _npcObjects.Add(go);
            var client = go.AddComponent<RecordingNpcClient>();

            var nameField = typeof(SimpleNpcClient).GetField("npcName",
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
            Assert.IsNotNull(nameField, "SimpleNpcClient.npcName not found");
            nameField.SetValue(client, npcId);
            client._metadataHandler = new ConversationMetadataHandler(npcId, new LatencyTracker(npcId));

            var session = new ConversationSession(npcId, requestId, npcId);
            if (asMicrophoneTurn)
            {
                Invoke("SetMicSession", session);
            }
            else
            {
                var accepted = (bool)typeof(RequestOrchestrator)
                    .GetMethod("RegisterLiveSession", PrivateInstance)
                    .Invoke(_orchestrator, new object[] { session });
                Assert.IsTrue(accepted, $"turn {requestId} should have been accepted");
            }

            Invoke("RegisterConversationCompletionHandler", client);
            NpcMessageRouter.Instance.RegisterNpc(client);
            NpcMessageRouter.Instance.SetActiveRequest(requestId, npcId);
            _socket.RegisterNpc(requestId, client);

            return client;
        }

        /// <summary>Feeds one binary audio frame in, framed exactly as BinaryAudioWrapper expects.</summary>
        private void DeliverAudio(string requestId, params byte[] audio)
        {
            var id = Encoding.UTF8.GetBytes(requestId);
            var frame = new byte[2 + id.Length + audio.Length];
            frame[0] = AudioDataMarker;
            frame[1] = (byte)id.Length;
            id.CopyTo(frame, 2);
            audio.CopyTo(frame, 2 + id.Length);

            InvokeOnSocket("HandleBinaryMessage", frame);
        }

        /// <summary>
        /// Feeds one text frame in, and hands it to the addressed NPC's metadata handler as the real
        /// NpcClient does — that is what raises OnConversationComplete to the orchestrator.
        /// </summary>
        private void DeliverText(string json)
        {
            InvokeOnSocket("HandleTextMessage", json);

            foreach (var go in _npcObjects)
            {
                if (go == null)
                    continue;
                var client = go.GetComponent<RecordingNpcClient>();
                if (client != null && client.TextMessages.Contains(json))
                    client.MetadataHandler?.ProcessMessage(json);
            }
        }

        private static string CompleteJson(string requestId) =>
            "{\"type\":\"conversationComplete\",\"requestId\":\"" + requestId + "\"}";

        private static string StreamEndJson(string requestId) =>
            "{\"type\":\"AudioStreamEnd\",\"requestId\":\"" + requestId + "\"}";

        private void Invoke(string methodName, params object[] args)
        {
            var method = typeof(RequestOrchestrator).GetMethod(methodName, PrivateInstance);
            Assert.IsNotNull(method, $"{methodName} not found on RequestOrchestrator");
            method.Invoke(_orchestrator, args);
        }

        private void InvokeOnSocket(string methodName, params object[] args)
        {
            var method = typeof(WebSocketClient).GetMethod(methodName, PrivateInstance);
            Assert.IsNotNull(method, $"{methodName} not found on WebSocketClient");
            method.Invoke(_socket, args);
        }

        private static void ClearRouterTable(string fieldName)
        {
            var field = typeof(NpcMessageRouter).GetField(fieldName, PrivateInstance);
            if (field?.GetValue(NpcMessageRouter.Instance) is System.Collections.IDictionary table)
                table.Clear();
        }
    }

    /// <summary>
    /// A real NpcClient that records what the routing delivered instead of playing it. Deliberately does
    /// not call base: these tests are about DELIVERY, and running the audio pipeline in EditMode would
    /// only add noise. What arrived is the entire question.
    ///
    /// Top level rather than nested inside the fixture: AddComponent on a nested private MonoBehaviour is
    /// not reliably supported.
    /// </summary>
    internal sealed class RecordingNpcClient : SimpleNpcClient
    {
        public readonly List<byte[]> AudioChunks = new List<byte[]>();
        public readonly List<string> TextMessages = new List<string>();

        public bool SawStreamEnd { get; private set; }

        public override void OnBinaryMessage(byte[] data) => AudioChunks.Add(data);

        public override void OnTextMessage(string json)
        {
            TextMessages.Add(json);
            if (json.Contains("\"type\":\"AudioStreamEnd\""))
                SawStreamEnd = true;
        }
    }
}
