using System.Collections.Generic;
using NUnit.Framework;
using Tsc.AIBridge.Input;
using Tsc.AIBridge.Messages;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: The vocal-delivery baseline is derived from ONE player's voice and must
    /// never be used to score another player. It also must survive the attempts within a lesson, because
    /// the server needs a minimum number of observations before a z-score means anything.
    ///
    /// WHY: those two requirements pull in opposite directions. Before v1.28.0 the baseline was a field on
    /// <see cref="SpeechInputHandler"/>, destroyed by every LoadSceneMode.Single scene load — safe across
    /// players by accident, but useless as a measurement (field data 2026-08-03: all-zero z-scores
    /// alternating with absurd ones computed against a two-sample reference). Moving it to a static store
    /// fixes the measurement but removes that accidental safety, so the scope rules below are the ONLY
    /// thing standing between player A's voice and player B's score. On a shared school VR headset,
    /// "next student does the same lesson" is the normal path, not an edge case.
    ///
    /// WHAT: validates <see cref="ProsodyBaselineStore"/> scope behaviour — a lesson change drops the
    /// baseline, the same lesson keeps it, an empty lessonId (AI coach) leaves scope untouched, and
    /// <see cref="ProsodyBaselineStore.Reset"/> clears both the baseline AND the remembered lesson so the
    /// next player starts clean even in the very same lesson.
    ///
    /// HOW: the store is static, so every test resets it in set-up and tear-down for isolation.
    ///
    /// SUCCESS CRITERIA:
    /// - Different lessonId → baseline dropped
    /// - Same lessonId → baseline kept (this is what makes the measurement usable)
    /// - Null/empty lessonId → baseline kept, scope unchanged
    /// - Reset() → baseline dropped and the lesson scope forgotten
    /// - State never null, even when assigned null
    ///
    /// BUSINESS IMPACT:
    /// - Falen van de Reset-test = speler B wordt beoordeeld tegen de stem van speler A. Dat is data
    ///   bleeding tussen gebruikers op één device, en het maakt de score onverdedigbaar.
    /// - Falen van de same-lesson-test = de baseline haalt nooit genoeg observaties en de hele
    ///   vocal-delivery meting levert onzin op (de bug die 1.28.0 juist repareert).
    /// </summary>
    [TestFixture]
    public class ProsodyBaselineStoreTests
    {
        [SetUp]
        [TearDown]
        public void ClearStore() => ProsodyBaselineStore.Reset();

        /// <summary>A baseline with enough accumulated data to be worth protecting.</summary>
        private static ProsodyBaselineState AccumulatedBaseline() => new ProsodyBaselineState
        {
            Features = new Dictionary<string, WelfordState>
            {
                { "loudness", new WelfordState { Count = 24, Mean = -18.4, M2 = 91.2 } }
            }
        };

        private static bool IsEmpty(ProsodyBaselineState state) => state.Features.Count == 0;

        [Test]
        public void EnsureLessonScope_DifferentLesson_DropsBaseline()
        {
            ProsodyBaselineStore.EnsureLessonScope("lesson-a");
            ProsodyBaselineStore.State = AccumulatedBaseline();

            ProsodyBaselineStore.EnsureLessonScope("lesson-b");

            Assert.IsTrue(IsEmpty(ProsodyBaselineStore.State),
                "A lesson change must drop the baseline — a reference from another lesson is not comparable.");
        }

        [Test]
        public void EnsureLessonScope_SameLesson_KeepsBaseline()
        {
            ProsodyBaselineStore.EnsureLessonScope("lesson-a");
            ProsodyBaselineStore.State = AccumulatedBaseline();

            // Every attempt re-enters SessionStart with the same lesson. This is exactly the case that
            // used to wipe the baseline (scene load destroyed the holder) and made the measurement useless.
            ProsodyBaselineStore.EnsureLessonScope("lesson-a");

            Assert.IsFalse(IsEmpty(ProsodyBaselineStore.State),
                "Re-entering the same lesson must keep the baseline, otherwise it never accumulates.");
        }

        [Test]
        public void EnsureLessonScope_EmptyLessonId_KeepsBaseline()
        {
            ProsodyBaselineStore.EnsureLessonScope("lesson-a");
            ProsodyBaselineStore.State = AccumulatedBaseline();

            // AI-coach conversations carry no lesson; they must not wipe a running lesson's reference.
            ProsodyBaselineStore.EnsureLessonScope(null);
            ProsodyBaselineStore.EnsureLessonScope(string.Empty);

            Assert.IsFalse(IsEmpty(ProsodyBaselineStore.State),
                "An unknown lessonId must leave the scope untouched, not reset it.");
        }

        [Test]
        public void Reset_ThenSameLesson_DropsBaseline()
        {
            // Player A works through a lesson and accumulates a reference.
            ProsodyBaselineStore.EnsureLessonScope("lesson-a");
            ProsodyBaselineStore.State = AccumulatedBaseline();

            // Logout. TrainingGlobals.LogOff calls this.
            ProsodyBaselineStore.Reset();

            // Player B logs in on the same device and picks THE SAME lesson. Without Reset() clearing the
            // remembered lesson too, the scope check would see no change and hand A's voice to B.
            ProsodyBaselineStore.State = AccumulatedBaseline();
            ProsodyBaselineStore.EnsureLessonScope("lesson-a");

            Assert.IsTrue(IsEmpty(ProsodyBaselineStore.State),
                "After a logout the next player must start from an empty baseline, even in the same lesson.");
        }

        [Test]
        public void State_AssignedNull_YieldsEmptyBaseline()
        {
            ProsodyBaselineStore.State = null;

            Assert.IsNotNull(ProsodyBaselineStore.State,
                "State is read on every SessionStart build; null would be a NullReferenceException on the hot path.");
            Assert.IsTrue(IsEmpty(ProsodyBaselineStore.State));
        }

        [Test]
        public void AdoptServerCopy_ReplacesStateWithTheServersRunningStats()
        {
            var fromServer = AccumulatedBaseline();

            ProsodyBaselineStore.AdoptServerCopy(fromServer);

            Assert.AreSame(fromServer, ProsodyBaselineStore.State,
                "The server's folded state is what the next SessionStart must carry.");
        }

        /// <summary>
        /// The freeze is client-owned: the rule node can set it while a turn is already in flight, and
        /// the copy coming back reflects only the state we SENT. A plain assignment would silently drop
        /// it, so the reference would keep absorbing the escalation it is supposed to be measured against.
        /// </summary>
        [Test]
        public void AdoptServerCopy_LocalFreezeSetDuringTheTurn_SurvivesTheServerCopy()
        {
            ProsodyBaselineStore.State.Frozen = true;

            var fromServer = AccumulatedBaseline();
            fromServer.Frozen = false; // the server echoes the pre-freeze state it received

            ProsodyBaselineStore.AdoptServerCopy(fromServer);

            Assert.IsTrue(ProsodyBaselineStore.State.Frozen,
                "A freeze applied mid-turn must not be undone by the reply to that same turn.");
        }

        [Test]
        public void AdoptServerCopy_Null_KeepsTheExistingBaseline()
        {
            var accumulated = AccumulatedBaseline();
            ProsodyBaselineStore.State = accumulated;

            ProsodyBaselineStore.AdoptServerCopy(null);

            Assert.AreSame(accumulated, ProsodyBaselineStore.State,
                "A backend that sends no baseline must not wipe a usable one.");
        }
    }
}
