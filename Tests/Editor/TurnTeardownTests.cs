using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Tsc.AIBridge.Core;
using Tsc.AIBridge.WebSocket;
using UnityEngine;
using UnityEngine.TestTools;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: when a turn ends, everything keyed on that turn's RequestId ends with it.
    ///
    /// WHY: starting a turn registers it in three places — the orchestrator's live set, the
    /// NpcMessageRouter (so audio can be paused/resumed for it), and WebSocketClient's handler table
    /// (so its messages reach the right NpcClient). Ending it only ever cleared the first, and cleared
    /// the second on two of the four paths.
    ///
    /// WebSocketClient.UnregisterNpc was never called from the conversation path at all — only
    /// AnalysisService called it. So _npcHandlers grew one entry per turn for a whole lesson, and every
    /// one of them kept routing late audio to an NPC whose turn had already been cancelled or failed.
    /// NpcMessageRouter.ClearRequest was called by the completion and timeout paths but not by
    /// CancelCurrentSession, so an abandoned turn stayed resolvable there as well — and that router is
    /// what NpcAudioPlayer.SendPauseStream / SendResumeStream consult to find a turn's stream.
    ///
    /// WHAT: the router clear lives in ReleaseLiveSession, the one place a turn leaves the live set,
    /// instead of being repeated (and forgotten) at each of its call sites. These tests pin that, per
    /// path.
    ///
    /// The WebSocket handler is deliberately NOT torn down there, and one of these tests exists only to
    /// keep it that way. v5.6.0 did tear it down there and broke every second turn — see
    /// TheWebSocketHandlerOutlivesTheTurnsBookkeeping below.
    /// </summary>
    [TestFixture]
    public class TurnTeardownTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

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

            // EditMode AddComponent runs no Awake, so the collaborator the orchestrator resolves at
            // runtime has to be installed by hand. A bare component is enough: nothing here connects.
            _socketObject = new GameObject("TestWebSocketClient");
            _socket = _socketObject.AddComponent<WebSocketClient>();
            typeof(RequestOrchestrator).GetField("_webSocketClient", PrivateInstance)
                .SetValue(_orchestrator, _socket);
        }

        [TearDown]
        public void TearDown()
        {
            // NpcMessageRouter is a process-wide singleton that outlives the fixture, so its tables have
            // to be emptied or one test's NPCs leak into the next.
            if (NpcMessageRouter.HasInstance)
            {
                ClearRouterTable("_activeNpcsByRequestId");
                ClearRouterTable("_npcsByName");
            }

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
        public void CompletingATurnStopsRoutingIt()
        {
            StartTurn("esra-turn", "Esra");

            _orchestrator.CompleteSession("esra-turn");

            AssertFullyTornDown("esra-turn");
        }

        [Test]
        public void CancellingTheMicrophoneTurnStopsRoutingIt()
        {
            // The path that cleared neither table. An abandoned turn stayed resolvable in the router,
            // which is what NpcAudioPlayer's pause/resume lookup goes through.
            GiveMicTurn("mic-turn", "Marc");
            RegisterRouting("mic-turn", "Marc");

            _orchestrator.CancelCurrentSession("test", cancelBackendAnswer: false);

            AssertFullyTornDown("mic-turn");
        }

        [Test]
        public void ATimedOutTurnStopsRoutingIt()
        {
            StartTurn("esra-turn", "Esra");

            LogAssert.ignoreFailingMessages = true;
            Invoke("FailUnresponsiveTurn", "esra-turn");
            LogAssert.ignoreFailingMessages = false;

            AssertFullyTornDown("esra-turn");
        }

        [Test]
        public void ADroppedSocketStopsRoutingEveryTurn()
        {
            GiveMicTurn("mic-turn", "Marc");
            RegisterRouting("mic-turn", "Marc");
            StartTurn("esra-turn", "Esra");

            LogAssert.ignoreFailingMessages = true;
            Invoke("AbortAllLiveTurns", "socket dropped");
            LogAssert.ignoreFailingMessages = false;

            AssertFullyTornDown("mic-turn");
            AssertFullyTornDown("esra-turn");
        }

        [Test]
        public void ADisplacedMicrophoneTurnStopsRoutingIt()
        {
            // Ten rapid push-to-talk presses on the same NPC are one continuous session by design: the
            // displaced turn gets no backend cancel, so nothing but this releases it.
            GiveMicTurn("press-1", "Marc");
            RegisterRouting("press-1", "Marc");

            GiveMicTurn("press-2", "Marc");

            AssertFullyTornDown("press-1");
        }

        [Test]
        public void ReleasingATurnThatWasNeverRoutedIsHarmless()
        {
            // Every path can discover the same dead turn, so this runs more than once per turn.
            Invoke("ReleaseLiveSession", "never-existed");
            Invoke("ReleaseLiveSession", (string)null);

            Assert.IsFalse(IsRoutedByRouter("never-existed"));
        }

        // --- helpers ---------------------------------------------------------------------------------

        /// <summary>Registers a turn the way ProcessTextRequest does: live set, router, socket.</summary>
        private void StartTurn(string requestId, string npcId)
        {
            var method = typeof(RequestOrchestrator).GetMethod("RegisterLiveSession", PrivateInstance);
            Assert.IsNotNull(method, "RegisterLiveSession not found on RequestOrchestrator");
            var accepted = (bool)method.Invoke(_orchestrator,
                new object[] { new ConversationSession(npcId, requestId, npcId) });
            Assert.IsTrue(accepted, $"Turn {requestId} should have been accepted");

            RegisterRouting(requestId, npcId);
        }

        private void GiveMicTurn(string requestId, string npcId) =>
            Invoke("SetMicSession", new ConversationSession(npcId, requestId, npcId));

        private void RegisterRouting(string requestId, string npcId)
        {
            var go = new GameObject(npcId);
            _npcObjects.Add(go);
            var client = go.AddComponent<SimpleNpcClient>();

            var nameField = typeof(SimpleNpcClient).GetField("npcName",
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
            Assert.IsNotNull(nameField, "SimpleNpcClient.npcName not found");
            nameField.SetValue(client, npcId);

            NpcMessageRouter.Instance.RegisterNpc(client);
            NpcMessageRouter.Instance.SetActiveRequest(requestId, npcId);
            _socket.RegisterNpc(requestId, client);

            Assert.IsTrue(IsRoutedByRouter(requestId), "precondition: the router resolves this turn");
            Assert.IsTrue(IsRoutedByWebSocket(requestId), "precondition: the socket resolves this turn");
        }

        [Test]
        public void TheWebSocketHandlerOutlivesTheTurnsBookkeeping()
        {
            // THE v5.6.0 REGRESSION, stated as a requirement.
            //
            // conversationComplete arrives about 200 ms after the FIRST audio chunk of a turn, not after
            // the last. Releasing the turn there is right for the bookkeeping and wrong for the routing:
            // the rest of that turn's audio and its audioStreamEnd still have to reach the NpcClient.
            // Without them the client never closes the stream, so the NPC stays "talking" with an empty
            // buffer, the player's next press is read as an interruption attempt and silently dropped,
            // and the turn only ends on the 15-second safety net (session log 2026-09-08 06:59).
            //
            // Binary audio with no handler is worse: WebSocketClient logs a Debug.LogError for it, and
            // ErrorHandler.Classify turns an unmatched error into the restart popup — a dropped chunk
            // would end the lesson. And PlayerTurnsAwayPolicy.LetAnswerFinish exists precisely so an
            // answer keeps arriving after its turn was released locally.
            StartTurn("esra-turn", "Esra");

            _orchestrator.CompleteSession("esra-turn");

            Assert.IsFalse(_orchestrator.IsTurnLive("esra-turn"), "the bookkeeping turn is over");
            Assert.IsTrue(IsRoutedByWebSocket("esra-turn"),
                "but its audio and audioStreamEnd must still find their NpcClient. Unregistering here is " +
                "what broke every second turn in v5.6.0.");
        }

        [Test]
        public void ACancelledTurnKeepsItsHandlerToo()
        {
            // Same reason, and one more: with OnPlayerTurnsAway = LetAnswerFinish the backend is
            // deliberately NOT cancelled, so the whole answer still arrives over this handler.
            GiveMicTurn("mic-turn", "Marc");
            RegisterRouting("mic-turn", "Marc");

            _orchestrator.CancelCurrentSession("player turned away", cancelBackendAnswer: false);

            Assert.IsTrue(IsRoutedByWebSocket("mic-turn"));
        }

        private void AssertFullyTornDown(string requestId)
        {
            Assert.IsFalse(_orchestrator.IsTurnLive(requestId), $"{requestId} must not be live");
            Assert.IsFalse(IsRoutedByRouter(requestId),
                $"NpcMessageRouter must stop resolving {requestId} — NpcAudioPlayer's pause/resume lookup " +
                "goes through it and would find a turn that no longer exists.");
        }

        private bool IsRoutedByWebSocket(string requestId)
        {
            var field = typeof(WebSocketClient).GetField("_npcHandlers", PrivateInstance);
            Assert.IsNotNull(field, "_npcHandlers not found on WebSocketClient");
            return ((Dictionary<string, INpcMessageHandler>)field.GetValue(_socket)).ContainsKey(requestId);
        }

        private static bool IsRoutedByRouter(string requestId)
        {
            var field = typeof(NpcMessageRouter).GetField("_activeNpcsByRequestId", PrivateInstance);
            Assert.IsNotNull(field, "_activeNpcsByRequestId not found on NpcMessageRouter");
            return ((Dictionary<string, NpcClientBase>)field.GetValue(NpcMessageRouter.Instance))
                .ContainsKey(requestId);
        }

        private static void ClearRouterTable(string fieldName)
        {
            var field = typeof(NpcMessageRouter).GetField(fieldName, PrivateInstance);
            if (field?.GetValue(NpcMessageRouter.Instance) is System.Collections.IDictionary table)
                table.Clear();
        }

        private void Invoke(string methodName, params object[] args)
        {
            var method = typeof(RequestOrchestrator).GetMethod(methodName, PrivateInstance);
            Assert.IsNotNull(method, $"{methodName} not found on RequestOrchestrator");
            method.Invoke(_orchestrator, args);
        }
    }
}
