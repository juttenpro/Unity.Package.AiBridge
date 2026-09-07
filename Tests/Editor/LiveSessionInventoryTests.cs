using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Tsc.AIBridge.Core;
using UnityEngine;
using UnityEngine.TestTools;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: every path that ends a turn must drop it from the live set. Nothing else
    /// depends on that yet — the point of pinning it now is that two later changes do, and both fail
    /// SILENTLY if the inventory is incomplete.
    ///
    /// WHY: the turn watchdog is about to stop asking "is this the current turn?" and start asking "is
    /// this turn still live?". A turn that ends through a path that forgets to release it then looks
    /// healthy forever, and the watchdog fails it ~120 seconds later — raising sttFailed into whatever
    /// turn is running by then. Per-turn teardown has the same dependency.
    ///
    /// Two exits were missing from the first design of this refactor, and both are covered here: a
    /// same-NPC re-press that DISPLACES the tracked session without cancelling the backend (ten rapid
    /// presses are one continuous session by design), and the session-mismatch abandon in
    /// ProcessAudioRequest. The mismatch path lives inside a coroutine and is verified by inspection
    /// rather than here; it is reworked into a recoverable per-turn failure in a later step.
    /// </summary>
    [TestFixture]
    public class LiveSessionInventoryTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

        private GameObject _orchestratorObject;
        private RequestOrchestrator _orchestrator;

        [SetUp]
        public void SetUp()
        {
            _orchestratorObject = new GameObject("TestOrchestrator");
            _orchestrator = _orchestratorObject.AddComponent<RequestOrchestrator>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_orchestratorObject != null)
                Object.DestroyImmediate(_orchestratorObject);
        }

        [Test]
        public void AnUnknownTurnIsNotLive()
        {
            Assert.IsFalse(_orchestrator.IsTurnLive("never-started"));
            Assert.IsFalse(_orchestrator.IsTurnLive(null));
            Assert.IsFalse(_orchestrator.IsTurnLive(string.Empty));
        }

        [Test]
        public void TrackingATurnMakesItLive()
        {
            Track("turn-1");

            Assert.IsTrue(_orchestrator.IsTurnLive("turn-1"));
        }

        [Test]
        public void CompleteSession_ReleasesIt()
        {
            Track("turn-1");

            _orchestrator.CompleteSession("turn-1");

            Assert.IsFalse(_orchestrator.IsTurnLive("turn-1"));
        }

        [Test]
        public void CancelCurrentSession_ReleasesIt()
        {
            Track("turn-1");

            _orchestrator.CancelCurrentSession("test");

            Assert.IsFalse(_orchestrator.IsTurnLive("turn-1"));
        }

        [Test]
        public void AbortActiveTurn_ReleasesIt()
        {
            Track("turn-1");
            SetField("_isRequestActive", true);

            LogAssert.Expect(LogType.Warning,
                new System.Text.RegularExpressions.Regex(@"\[RequestOrchestrator\] Active turn aborted"));
            Invoke("AbortActiveTurn", "test abort");

            Assert.IsFalse(_orchestrator.IsTurnLive("turn-1"));
        }

        [Test]
        public void AbortActiveTurn_ReleasesAStaleTurnEvenWithNoArmedRequest()
        {
            // The turn already ended for the RuleSystem but its session lingered; recovery must still
            // drop it, or it stays "live" for the rest of the lesson.
            Track("turn-1");

            Invoke("AbortActiveTurn", "socket dropped");

            Assert.IsFalse(_orchestrator.IsTurnLive("turn-1"));
        }

        [Test]
        public void FailUnresponsiveTurn_ReleasesIt()
        {
            Track("turn-1");

            LogAssert.Expect(LogType.Warning,
                new System.Text.RegularExpressions.Regex("No backend response for turn"));
            Invoke("FailUnresponsiveTurn", "turn-1");

            Assert.IsFalse(_orchestrator.IsTurnLive("turn-1"));
        }

        [Test]
        public void ConversationCompleted_ReleasesTheTurnThatCompleted()
        {
            Track("turn-1");

            Invoke("HandleConversationCompleted", "turn-1", false);

            Assert.IsFalse(_orchestrator.IsTurnLive("turn-1"));
        }

        [Test]
        public void ADisplacedTurnIsReleased()
        {
            // The exit that has no exit: a same-NPC re-press replaces the tracked session and sends no
            // backend cancel, so no completion, cancel or failure will ever arrive for the old one.
            Track("turn-1");

            Track("turn-2");

            Assert.IsFalse(_orchestrator.IsTurnLive("turn-1"),
                "The displaced turn must be released here — nothing else will ever visit it.");
            Assert.IsTrue(_orchestrator.IsTurnLive("turn-2"));
            Assert.AreEqual(1, LiveCount(), "A rapid burst of presses must not accumulate live turns.");
        }

        [Test]
        public void ARapidBurstOfPressesLeavesExactlyOneLiveTurn()
        {
            // RequestOrchestratorAudioTests documents ten presses as ONE continuous session. That
            // contract is preserved; what changes is that the nine displaced sessions no longer linger.
            for (var i = 0; i < 10; i++)
                Track($"turn-{i}");

            Assert.AreEqual(1, LiveCount());
            Assert.IsTrue(_orchestrator.IsTurnLive("turn-9"));
        }

        [Test]
        public void ReleasingATurnTwiceIsHarmless()
        {
            Track("turn-1");

            _orchestrator.CompleteSession("turn-1");
            Assert.DoesNotThrow(() => _orchestrator.CompleteSession("turn-1"),
                "A duplicated or late completion must be a no-op, not an exception inside the message pump.");
        }

        // --- helpers ---------------------------------------------------------------------------------

        private void Track(string requestId)
        {
            var method = typeof(RequestOrchestrator).GetMethod("SetMicSession", PrivateInstance);
            Assert.IsNotNull(method, "SetMicSession not found on RequestOrchestrator");
            method.Invoke(_orchestrator, new object[] { new ConversationSession("TestNpc", requestId) });
        }

        private int LiveCount()
        {
            var field = typeof(RequestOrchestrator).GetField("_liveSessions", PrivateInstance);
            Assert.IsNotNull(field, "_liveSessions not found on RequestOrchestrator");
            return ((Dictionary<string, ConversationSession>)field.GetValue(_orchestrator)).Count;
        }

        private void SetField(string fieldName, object value)
        {
            var field = typeof(RequestOrchestrator).GetField(fieldName, PrivateInstance);
            Assert.IsNotNull(field, $"Field '{fieldName}' not found on RequestOrchestrator");
            field.SetValue(_orchestrator, value);
        }

        private void Invoke(string methodName, params object[] args)
        {
            var method = typeof(RequestOrchestrator).GetMethod(methodName, PrivateInstance);
            Assert.IsNotNull(method, $"{methodName} not found on RequestOrchestrator");
            method.Invoke(_orchestrator, args);
        }
    }
}
