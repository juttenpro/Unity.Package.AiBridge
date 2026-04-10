using NUnit.Framework;
using Tsc.AIBridge.Audio.Interruption;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: The interruption decision logic must be a pure, testable
    /// function that can be verified without instantiating a full Unity scene, WebSocket
    /// connection, or NPC client. Before v1.6.16 this logic lived inside a coroutine and
    /// was impossible to unit-test, which is how multiple bugs survived into production:
    ///
    ///   1. Near-end detection was dead code — it used the same persistence threshold as
    ///      the normal path, so detecting that the NPC was almost done speaking did nothing.
    ///   2. NPC micro-pauses (breathing, commas) reset the overlap timer to zero, making
    ///      interruption extremely difficult because the user had to catch a full 0.4s
    ///      overlap inside a single NPC phrase.
    ///
    /// WHY: Users reported that interrupting NPCs "requires almost shouting". Investigation
    ///      showed the volume threshold was only part of the story — the overlap timer
    ///      logic itself was fragile. These tests lock in the corrected behavior as a pure
    ///      function so regressions surface immediately.
    /// WHAT: The new public helpers on InterruptionManager are pure methods that take
    ///      state in, return state out. They are the single source of truth for
    ///      "should we interrupt?" and "how do we update the overlap timer?".
    /// HOW: Each test constructs a scenario (overlap so far, user speaking y/n, NPC speaking
    ///      y/n, NPC responding y/n, near-end y/n) and asserts on the decision.
    ///
    /// SUCCESS CRITERIA:
    /// - Near-end mode interrupts at a smaller fraction of persistenceTime (default 25%)
    /// - Brief NPC pauses within the pause-tolerance window do NOT reset the overlap timer
    /// - Real NPC silence (beyond pause tolerance) does reset the overlap timer
    /// - User stopping speech always resets the overlap timer
    /// - allowInterruption = false blocks normal interruption but not near-end
    ///
    /// BUSINESS IMPACT:
    /// - Failing = Users can't interrupt during natural pauses → fights the conversation flow
    /// - Failing = Near-end detection is meaningless → late-response interruptions frustrate users
    /// - Failing = Regression untested → these exact bugs can reappear in future refactors
    /// </summary>
    [TestFixture]
    public class InterruptionLogicTests
    {
        private const float PersistenceTime = 0.4f;
        private const float NearEndMultiplier = 0.25f; // Near-end interrupts at 25% of persistence
        private const float NpcPauseTolerance = 0.3f;  // 300ms matches SpeechPauseThreshold
        private const float DeltaTime = 0.016f;        // ~60 fps frame

        #region ShouldInterrupt — Bug 2 fix

        /// <summary>
        /// BUSINESS REQUIREMENT: When overlap has not reached persistenceTime, no
        /// interruption is fired in the normal path.
        /// </summary>
        [Test]
        public void ShouldInterrupt_BelowPersistenceTime_ReturnsFalse()
        {
            var result = InterruptionManager.ShouldInterrupt(
                overlapTimer: 0.2f,
                persistenceTime: PersistenceTime,
                isNearEnd: false,
                allowInterruption: true,
                nearEndPersistenceMultiplier: NearEndMultiplier);

            Assert.IsFalse(result);
        }

        /// <summary>
        /// BUSINESS REQUIREMENT: Once overlap reaches persistenceTime and interruption is
        /// allowed, the decision flips to true.
        /// </summary>
        [Test]
        public void ShouldInterrupt_AtOrAbovePersistenceTime_ReturnsTrue()
        {
            var result = InterruptionManager.ShouldInterrupt(
                overlapTimer: 0.4f,
                persistenceTime: PersistenceTime,
                isNearEnd: false,
                allowInterruption: true,
                nearEndPersistenceMultiplier: NearEndMultiplier);

            Assert.IsTrue(result);
        }

        /// <summary>
        /// BUSINESS REQUIREMENT: When allowInterruption is false on the persona, normal
        /// interruption is blocked no matter how long the user talks.
        /// </summary>
        [Test]
        public void ShouldInterrupt_AllowInterruptionFalse_BlocksNormalInterruption()
        {
            var result = InterruptionManager.ShouldInterrupt(
                overlapTimer: 2.0f,
                persistenceTime: PersistenceTime,
                isNearEnd: false,
                allowInterruption: false,
                nearEndPersistenceMultiplier: NearEndMultiplier);

            Assert.IsFalse(result);
        }

        /// <summary>
        /// BUSINESS REQUIREMENT: Near-end detection (NPC stream complete + buffer nearly
        /// drained) must interrupt faster than the normal path. This is the core of the
        /// "near-end was dead code" fix. At 25% of persistenceTime (100ms at 0.4s default),
        /// a small overlap during the tail end of an NPC response should trigger interrupt.
        /// </summary>
        [Test]
        public void ShouldInterrupt_NearEnd_InterruptsAtQuarterPersistence()
        {
            // 0.1s overlap = 25% of 0.4s persistence
            var result = InterruptionManager.ShouldInterrupt(
                overlapTimer: 0.1f,
                persistenceTime: PersistenceTime,
                isNearEnd: true,
                allowInterruption: true,
                nearEndPersistenceMultiplier: NearEndMultiplier);

            Assert.IsTrue(result,
                "Near-end mode must interrupt with only 25% of normal persistence time");
        }

        /// <summary>
        /// BUSINESS REQUIREMENT: Near-end mode should interrupt even when allowInterruption
        /// is false on the persona. Rationale: if the NPC is already finishing, blocking
        /// interruption adds no value and just frustrates the user.
        /// </summary>
        [Test]
        public void ShouldInterrupt_NearEndOverridesAllowInterruption()
        {
            var result = InterruptionManager.ShouldInterrupt(
                overlapTimer: 0.15f,
                persistenceTime: PersistenceTime,
                isNearEnd: true,
                allowInterruption: false, // persona blocks normal interruption
                nearEndPersistenceMultiplier: NearEndMultiplier);

            Assert.IsTrue(result,
                "Near-end should still allow interruption even when persona blocks normal interruption");
        }

        /// <summary>
        /// BUSINESS REQUIREMENT: Near-end with insufficient overlap still returns false.
        /// Regression check for the "always interrupt near-end" anti-pattern.
        /// </summary>
        [Test]
        public void ShouldInterrupt_NearEndBelowReducedThreshold_ReturnsFalse()
        {
            var result = InterruptionManager.ShouldInterrupt(
                overlapTimer: 0.05f, // below 0.1s near-end threshold
                persistenceTime: PersistenceTime,
                isNearEnd: true,
                allowInterruption: true,
                nearEndPersistenceMultiplier: NearEndMultiplier);

            Assert.IsFalse(result);
        }

        #endregion

        #region UpdateOverlapTimer — Bug 3 fix

        /// <summary>
        /// BUSINESS REQUIREMENT: When the user is speaking AND the NPC is producing actual
        /// audible speech, the overlap timer accumulates normally.
        /// </summary>
        [Test]
        public void UpdateOverlapTimer_BothSpeaking_AccumulatesTime()
        {
            var result = InterruptionManager.UpdateOverlapTimer(
                currentOverlap: 0.1f,
                currentNpcPauseAccumulator: 0f,
                userSpeaking: true,
                npcActuallySpeaking: true,
                npcResponding: true,
                deltaTime: DeltaTime,
                npcPauseTolerance: NpcPauseTolerance);

            Assert.AreEqual(0.1f + DeltaTime, result.OverlapTimer, 0.0001f);
            Assert.AreEqual(0f, result.NpcPauseAccumulator, 0.0001f,
                "Pause accumulator resets when NPC is actually producing speech");
        }

        /// <summary>
        /// BUSINESS REQUIREMENT: A brief NPC pause within an active response must NOT reset
        /// the overlap timer. This is the core of the Bug 3 fix. Users should be able to
        /// interrupt even when the NPC is taking breaths between words.
        /// </summary>
        [Test]
        public void UpdateOverlapTimer_BriefNpcPause_DoesNotResetOverlap()
        {
            // Step 1: both speaking, overlap = 0.2s
            var step1 = InterruptionManager.UpdateOverlapTimer(
                currentOverlap: 0.2f,
                currentNpcPauseAccumulator: 0f,
                userSpeaking: true,
                npcActuallySpeaking: true,
                npcResponding: true,
                deltaTime: DeltaTime,
                npcPauseTolerance: NpcPauseTolerance);

            // Step 2: NPC takes a 100ms pause (shorter than 300ms tolerance).
            // User is still talking, response is still active.
            // Accumulate 6 frames = ~96ms of NPC silence.
            var overlap = step1.OverlapTimer;
            var pauseAccum = step1.NpcPauseAccumulator;
            for (var i = 0; i < 6; i++)
            {
                var step = InterruptionManager.UpdateOverlapTimer(
                    currentOverlap: overlap,
                    currentNpcPauseAccumulator: pauseAccum,
                    userSpeaking: true,
                    npcActuallySpeaking: false, // brief silence
                    npcResponding: true,         // response still active
                    deltaTime: DeltaTime,
                    npcPauseTolerance: NpcPauseTolerance);
                overlap = step.OverlapTimer;
                pauseAccum = step.NpcPauseAccumulator;
            }

            // Overlap must have grown, not reset. We started at 0.2 + DeltaTime and added 6 more frames.
            var expected = 0.2f + DeltaTime * 7;
            Assert.AreEqual(expected, overlap, 0.0001f,
                "Brief NPC pauses within pause tolerance must count toward overlap");
            Assert.Greater(pauseAccum, 0f);
            Assert.Less(pauseAccum, NpcPauseTolerance);
        }

        /// <summary>
        /// BUSINESS REQUIREMENT: A prolonged NPC silence (beyond pause tolerance) while the
        /// user is still talking must reset the overlap timer. This prevents false
        /// interruption when the user continues after the NPC has stopped for real.
        /// </summary>
        [Test]
        public void UpdateOverlapTimer_ProlongedNpcSilence_ResetsOverlap()
        {
            // Accumulate frames of NPC silence, stopping exactly at the moment the helper
            // resets. Running past the reset would cause the timer to start accumulating
            // again on the next tick (which is correct behavior but not what this test
            // verifies).
            var overlap = 0.3f;
            var pauseAccum = 0f;
            bool resetObserved = false;
            for (var i = 0; i < 25; i++)
            {
                var step = InterruptionManager.UpdateOverlapTimer(
                    currentOverlap: overlap,
                    currentNpcPauseAccumulator: pauseAccum,
                    userSpeaking: true,
                    npcActuallySpeaking: false,
                    npcResponding: true,
                    deltaTime: DeltaTime,
                    npcPauseTolerance: NpcPauseTolerance);

                // A reset manifests as both fields returning to zero after having been nonzero.
                if (overlap > 0f && step.OverlapTimer == 0f && step.NpcPauseAccumulator == 0f)
                {
                    resetObserved = true;
                    overlap = step.OverlapTimer;
                    pauseAccum = step.NpcPauseAccumulator;
                    break;
                }

                overlap = step.OverlapTimer;
                pauseAccum = step.NpcPauseAccumulator;
            }

            Assert.IsTrue(resetObserved,
                "Overlap must reset to zero once NPC silence exceeds pause tolerance");
            Assert.AreEqual(0f, overlap, 0.0001f);
            Assert.AreEqual(0f, pauseAccum, 0.0001f);
        }

        /// <summary>
        /// BUSINESS REQUIREMENT: If the user stops speaking at any point, the overlap timer
        /// resets immediately. Interruption requires continuous user speech through the NPC.
        /// </summary>
        [Test]
        public void UpdateOverlapTimer_UserStopsSpeaking_ResetsOverlap()
        {
            var result = InterruptionManager.UpdateOverlapTimer(
                currentOverlap: 0.3f,
                currentNpcPauseAccumulator: 0.1f,
                userSpeaking: false,
                npcActuallySpeaking: true,
                npcResponding: true,
                deltaTime: DeltaTime,
                npcPauseTolerance: NpcPauseTolerance);

            Assert.AreEqual(0f, result.OverlapTimer);
            Assert.AreEqual(0f, result.NpcPauseAccumulator);
        }

        /// <summary>
        /// BUSINESS REQUIREMENT: Once the NPC response ends entirely (IsTalking = false),
        /// the overlap state resets. Any interruption must be detected before this point
        /// via the near-end path.
        /// </summary>
        [Test]
        public void UpdateOverlapTimer_NpcResponseEnds_ResetsOverlap()
        {
            var result = InterruptionManager.UpdateOverlapTimer(
                currentOverlap: 0.3f,
                currentNpcPauseAccumulator: 0.1f,
                userSpeaking: true,
                npcActuallySpeaking: false,
                npcResponding: false, // NPC no longer in active response
                deltaTime: DeltaTime,
                npcPauseTolerance: NpcPauseTolerance);

            Assert.AreEqual(0f, result.OverlapTimer);
            Assert.AreEqual(0f, result.NpcPauseAccumulator);
        }

        /// <summary>
        /// BUSINESS REQUIREMENT: After NPC resumes speaking following a brief pause, the
        /// pause accumulator must reset to zero so that a subsequent pause gets a fresh
        /// tolerance window.
        /// </summary>
        [Test]
        public void UpdateOverlapTimer_NpcResumesAfterBriefPause_ResetsPauseAccumulator()
        {
            // First: in a brief pause
            var step1 = InterruptionManager.UpdateOverlapTimer(
                currentOverlap: 0.2f,
                currentNpcPauseAccumulator: 0.1f,
                userSpeaking: true,
                npcActuallySpeaking: false,
                npcResponding: true,
                deltaTime: DeltaTime,
                npcPauseTolerance: NpcPauseTolerance);

            // Then: NPC resumes speaking
            var step2 = InterruptionManager.UpdateOverlapTimer(
                currentOverlap: step1.OverlapTimer,
                currentNpcPauseAccumulator: step1.NpcPauseAccumulator,
                userSpeaking: true,
                npcActuallySpeaking: true,
                npcResponding: true,
                deltaTime: DeltaTime,
                npcPauseTolerance: NpcPauseTolerance);

            Assert.AreEqual(0f, step2.NpcPauseAccumulator, 0.0001f,
                "Pause accumulator must reset when NPC resumes producing speech");
            Assert.Greater(step2.OverlapTimer, step1.OverlapTimer,
                "Overlap timer must continue accumulating");
        }

        #endregion

        #region Default persistence fallback — Bug 4 fix

        /// <summary>
        /// BUSINESS REQUIREMENT: When no active NPC configuration is available (edge case
        /// during scene load, disposed persona, etc.), the interruption system must fall
        /// back to a value that matches the PersonaSO default (0.4s), not the previous
        /// 1.5s which made interruption 3.75x harder in edge cases.
        /// </summary>
        [Test]
        public void DefaultPersistenceFallback_MatchesPersonaSoDefault()
        {
            Assert.AreEqual(0.4f, InterruptionManager.DefaultPersistenceTimeFallback, 0.0001f,
                "Fallback persistence must match the PersonaSO.persistenceTime default of 0.4s");
        }

        #endregion
    }
}
