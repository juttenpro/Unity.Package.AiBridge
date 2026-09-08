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
    /// WHAT: `ReleaseLiveSession` ends only the BOOKKEEPING. Routing — both tables — is ended by
    /// `ReleaseTurnRouting`, and only when the turn's audio can no longer arrive: its playback finished,
    /// or it was cancelled with a backend cancel, timed out, or died with the socket.
    ///
    /// This fixture had it the other way round twice, and both times the test was the thing that made the
    /// mistake look correct. v5.6.0 tore both tables down at `conversationComplete` and every second turn
    /// died; v5.6.1 fixed the socket handler and kept the router clear on the reasoning "that is where it
    /// already was" — which was the same wrong premise, and it is why an approved interruption has never
    /// reached the backend since v5.2.1. `conversationComplete` arrives ~200 ms after the FIRST audio
    /// chunk. It is not the end of anything the NPC is doing.
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
        public void CompletingATurnEndsItsBookkeepingAndNothingElse()
        {
            // The turn is no longer live, and both routing tables stay: the NPC is still speaking.
            StartTurn("esra-turn", "Esra");

            _orchestrator.CompleteSession("esra-turn");

            Assert.IsFalse(_orchestrator.IsTurnLive("esra-turn"));
            AssertStillRouted("esra-turn", "Esra");
        }

        [Test]
        public void RoutingEndsWhenTheBackendIsDoneAndTheAudioHasPlayed()
        {
            // Both halves have to have happened. Completion first here, then playback.
            StartTurn("esra-turn", "Esra");

            _orchestrator.CompleteSession("esra-turn");
            AssertStillRouted("esra-turn", "Esra");

            _orchestrator.NotifyTurnAudioFinished("esra-turn");

            AssertFullyTornDown("esra-turn");
        }

        [Test]
        public void PlaybackFinishingAloneIsNotEnough()
        {
            // THE 2026-09-08 09:14 FAILURE, stated as a requirement. A same-NPC re-press displaces the
            // previous turn WITHOUT a backend cancel, and the new request resets that NPC's decoder — so
            // the displaced turn's playback "finishes" at once while the backend is still streaming it.
            // Releasing the routing there left hundreds of chunks unroutable, and unroutable audio was a
            // fatal error, so the lesson ended.
            StartTurn("marc-turn", "Marc");

            _orchestrator.NotifyTurnAudioFinished("marc-turn");

            AssertStillRouted("marc-turn", "Marc");
        }

        [Test]
        public void RoutingEndsWhenPlaybackFinishesAfterTheCompletionAlreadyArrived()
        {
            // The other order, which is the normal one: completion lands ~200 ms into the speech and
            // playback ends seconds later.
            StartTurn("esra-turn", "Esra");
            _orchestrator.NotifyTurnAudioFinished("esra-turn");
            AssertStillRouted("esra-turn", "Esra");

            _orchestrator.CompleteSession("esra-turn");

            AssertFullyTornDown("esra-turn");
        }

        [Test]
        public void AnExplicitCancelStopsRoutingWithoutWaitingForPlayback()
        {
            // The backend was told to stop, so there is nothing left to wait for.
            StartTurn("esra-turn", "Esra");

            _orchestrator.ForceReleaseTurnRouting("esra-turn");

            // Routing only — this method says nothing about the bookkeeping, which its callers release
            // themselves.
            Assert.IsFalse(IsRoutedByWebSocket("esra-turn"));
            Assert.IsFalse(IsRoutedByRouter("esra-turn"));
        }

        [Test]
        public void CancellingWithABackendCancelStopsRoutingIt()
        {
            // The backend was told to stop, so no audio can still arrive and both tables can go.
            GiveMicTurn("mic-turn", "Marc");
            RegisterRouting("mic-turn", "Marc");

            // A backend cancel on a socket that was never connected (no Awake in EditMode) logs a
            // [Recoverable] connection error. That is the harness, not the product.
            LogAssert.ignoreFailingMessages = true;
            _orchestrator.CancelCurrentSession("test", cancelBackendAnswer: true);
            LogAssert.ignoreFailingMessages = false;

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
        public void ADisplacedMicrophoneTurnKeepsItsRouting()
        {
            // Ten rapid push-to-talk presses on the same NPC are one continuous session by design, and
            // the displaced turn gets NO backend cancel — so its answer may still be on its way. Dropping
            // its routing would drop audio, and unroutable binary audio ends the lesson.
            //
            // The cost is a bounded leak: if that answer never plays, its two routing entries stay for
            // the rest of the lesson. One per displaced press, against a dropped chunk killing the
            // session — see Concurrent-Turns-Plan step 13.
            GiveMicTurn("press-1", "Marc");
            RegisterRouting("press-1", "Marc");

            GiveMicTurn("press-2", "Marc");

            Assert.IsFalse(_orchestrator.IsTurnLive("press-1"), "its bookkeeping is gone");
            Assert.IsTrue(IsRoutedByWebSocket("press-1"),
                "but an answer that arrives must still be playable rather than fatal");
        }

        [Test]
        public void ReleasingATurnThatWasNeverRoutedIsHarmless()
        {
            // Every path can discover the same dead turn, so this runs more than once per turn.
            Invoke("ReleaseLiveSession", "never-existed");
            Invoke("ReleaseLiveSession", (string)null);
            _orchestrator.NotifyTurnAudioFinished("never-existed");
            _orchestrator.NotifyTurnAudioFinished(null);
            _orchestrator.ForceReleaseTurnRouting("never-existed");
            _orchestrator.ForceReleaseTurnRouting(null);

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
                $"NpcMessageRouter must stop resolving {requestId} once the audio is done.");
            Assert.IsFalse(IsRoutedByWebSocket(requestId),
                $"WebSocketClient must stop routing {requestId} once the audio is done, or its handler " +
                "table grows one entry per turn for the whole lesson.");
        }

        private void AssertStillRouted(string requestId, string npcName)
        {
            Assert.IsTrue(IsRoutedByWebSocket(requestId),
                $"The rest of {requestId}'s audio and its audioStreamEnd still route through this handler.");
            Assert.AreEqual(requestId, NpcMessageRouter.Instance.GetActiveRequestForNpc(npcName),
                "And the router must still be able to name the turn this NPC is playing — that is what " +
                "InterruptionManager.ResolveInterruptedTurnId asks in order to stop the interrupted " +
                "turn's TTS on the backend.");
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
