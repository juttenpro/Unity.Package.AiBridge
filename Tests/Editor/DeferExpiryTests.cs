using NUnit.Framework;
using Tsc.AIBridge.Core;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: A safety-net-deferred audio teardown must always end —
    /// either when the server signals end-of-stream, or after a hard timeout.
    ///
    /// WHY: v1.17.3 introduced "defer teardown until server signals end" to fix the
    /// 2026-05-18 11:48 truncation. But the production session 13:56–14:00 showed the
    /// defer had no end condition: when the server signal arrived 2s after the defer
    /// (13:58:45.675), nobody listened. The next user turn's chunks came in but
    /// AudioMessageHandler treated them as "Additional OGG header" (counter never reset),
    /// the player was in zombie state (`_forceStop=true` from StopPlayback), and nothing
    /// played — the entire next turn was silent.
    ///
    /// WHAT: <see cref="DeferExpiry.ShouldEndDefer"/> encodes the policy: end defer when
    /// the server signal arrives OR a hard timeout elapses. Pure function, fully
    /// unit-testable.
    ///
    /// SUCCESS CRITERIA:
    /// - elapsed=0,  serverStreamEnd=false → false (still waiting)
    /// - elapsed=0,  serverStreamEnd=true  → true  (server confirmed end, expire immediately)
    /// - elapsed=3s, serverStreamEnd=false → false (still within max-defer window)
    /// - elapsed=5s, serverStreamEnd=false → true  (hard timeout)
    /// - elapsed=10s, serverStreamEnd=false → true (timeout long past)
    /// - elapsed=3s, serverStreamEnd=true  → true  (signal wins over timeout)
    ///
    /// BUSINESS IMPACT:
    /// - With this decision wired into NpcClientBase, the defer state is guaranteed to
    ///   resolve into a clean teardown within MaxDeferSeconds — the "stuck after defer"
    ///   bug from 2026-05-18 13:58 cannot recur
    /// - Server-signal path resolves defer immediately when the late chunks would have
    ///   finished anyway; timeout path acts as a server-crash safety net
    /// </summary>
    [TestFixture]
    public class DeferExpiryTests
    {
        private const float MaxDefer = 5f;

        /// <summary>
        /// Just after defer started — no server signal yet and no time has passed.
        /// Must wait: late chunks may still arrive.
        /// </summary>
        [Test]
        public void NoSignal_NoTimeElapsed_KeepsWaiting()
        {
            Assert.IsFalse(DeferExpiry.ShouldEndDefer(elapsedSeconds: 0f, serverStreamEnd: false, maxDeferSeconds: MaxDefer));
        }

        /// <summary>
        /// Server signal arrived — end defer immediately, run clean teardown.
        /// This is the happy path: late chunks completed, server confirmed stream end.
        /// </summary>
        [Test]
        public void ServerSignal_EndsDeferImmediately()
        {
            Assert.IsTrue(DeferExpiry.ShouldEndDefer(elapsedSeconds: 0f, serverStreamEnd: true, maxDeferSeconds: MaxDefer));
        }

        /// <summary>
        /// Halfway through the max-defer window — still waiting for server signal.
        /// </summary>
        [Test]
        public void PartialTimeNoSignal_KeepsWaiting()
        {
            Assert.IsFalse(DeferExpiry.ShouldEndDefer(elapsedSeconds: 3f, serverStreamEnd: false, maxDeferSeconds: MaxDefer));
        }

        /// <summary>
        /// Server signal during the wait — end defer (signal beats time).
        /// </summary>
        [Test]
        public void ServerSignalDuringWait_EndsDefer()
        {
            Assert.IsTrue(DeferExpiry.ShouldEndDefer(elapsedSeconds: 3f, serverStreamEnd: true, maxDeferSeconds: MaxDefer));
        }

        /// <summary>
        /// Hard timeout — server signal never came (e.g., backend crashed).
        /// Force teardown to prevent the "next turn forever stuck" bug.
        /// </summary>
        [Test]
        public void HardTimeout_NoSignal_EndsDefer()
        {
            Assert.IsTrue(DeferExpiry.ShouldEndDefer(elapsedSeconds: 5f, serverStreamEnd: false, maxDeferSeconds: MaxDefer));
        }

        /// <summary>
        /// Way past the timeout — same behaviour, still ends.
        /// </summary>
        [Test]
        public void LongPastTimeout_EndsDefer()
        {
            Assert.IsTrue(DeferExpiry.ShouldEndDefer(elapsedSeconds: 30f, serverStreamEnd: false, maxDeferSeconds: MaxDefer));
        }

        /// <summary>
        /// Default constant should be 5 seconds — pinned for review predictability.
        /// </summary>
        [Test]
        public void DefaultMaxDeferSeconds_Is5()
        {
            Assert.AreEqual(5f, DeferExpiry.DefaultMaxDeferSeconds);
        }

        /// <summary>
        /// Different MaxDeferSeconds value used — function honours the parameter.
        /// </summary>
        [Test]
        public void CustomMaxDeferSeconds_HonouredByDecision()
        {
            // At 2s with maxDefer=1.5s → past timeout
            Assert.IsTrue(DeferExpiry.ShouldEndDefer(elapsedSeconds: 2f, serverStreamEnd: false, maxDeferSeconds: 1.5f));
            // At 2s with maxDefer=10s → still waiting
            Assert.IsFalse(DeferExpiry.ShouldEndDefer(elapsedSeconds: 2f, serverStreamEnd: false, maxDeferSeconds: 10f));
        }
    }
}
