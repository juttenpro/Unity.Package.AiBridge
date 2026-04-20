using NUnit.Framework;
using Tsc.AIBridge.Audio.Playback;
using UnityEngine;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: Audio pause/resume must distinguish between pause sources so that
    /// an OS-level focus loss (e.g., Quest Home button short press) can auto-resume on focus
    /// return without conflicting with an external training pause (e.g., PauseManager).
    ///
    /// WHY: A production bug was reported where pressing the Quest Home button during NPC
    /// speech stopped the audio and never resumed. Root cause: the boolean
    /// "_isPausedByExternalSource" flag was unconditionally set by NpcAudioPlayer.PausePlayback()
    /// regardless of who called it. When OnApplicationFocus(false) triggered a pause, the flag
    /// was set as if PauseManager had paused; on focus return, OnApplicationFocus(true) checked
    /// the flag and refused to resume, leaving the pipeline permanently stuck. The NPC body
    /// animation kept running (Animancer is independent), but no audio ever came back and the
    /// WebSocket pipeline was frozen because ResumeStream was never sent to the backend.
    ///
    /// WHAT: Validates that the new PauseReason enum correctly tracks WHO paused, so that
    /// each pause source can resume independently, and overlapping pauses (e.g., OS + external)
    /// keep audio paused until ALL sources resume.
    ///
    /// HOW: Creates StreamingAudioPlayer instances and exercises Pause/Resume with different
    /// reasons, asserting on PipelineState transitions and IsPausedForReason queries.
    ///
    /// SUCCESS CRITERIA:
    /// - Pause with OsFocusLoss + Resume with OsFocusLoss round-trips correctly (bug fix)
    /// - External pause is not cleared by an OS-level resume (preserved invariant)
    /// - OS pause is not cleared by an External resume (symmetric invariant)
    /// - Overlapping pauses stay paused until all sources resume
    /// - Default parameter value is External (backwards compatibility for existing callers)
    ///
    /// BUSINESS IMPACT:
    /// - Failure = NPC goes silent permanently after a Quest Home press; user must restart
    ///   the training session. Hard-to-diagnose user-facing freeze.
    /// - Affects ALL VR courses using AI streaming audio (RokendeVrouw, Coach_Alex, etc.).
    /// </summary>
    [TestFixture]
    public class PauseReasonTests
    {
        private StreamingAudioPlayer _player;
        private GameObject _testGameObject;

        [SetUp]
        public void SetUp()
        {
            _testGameObject = new GameObject("TestAudioPlayer");
            _testGameObject.AddComponent<AudioSource>();
            _player = _testGameObject.AddComponent<StreamingAudioPlayer>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_testGameObject != null)
                Object.DestroyImmediate(_testGameObject);
        }

        #region Single-Reason Pause/Resume

        /// <summary>
        /// BUSINESS REQUIREMENT: Pause with OsFocusLoss must record the reason so a matching
        /// resume can find it.
        ///
        /// WHY: Without reason tracking, resume can't tell which source is asking to un-pause.
        /// WHAT: After PausePlayback(OsFocusLoss) the player reports paused for that reason.
        /// </summary>
        [Test]
        public void PausePlayback_WithOsFocusLoss_RecordsReason()
        {
            _player.StartStream(48000);

            _player.PausePlayback(PauseReason.OsFocusLoss);

            Assert.AreEqual(AudioPipelineState.Paused, _player.PipelineState);
            Assert.IsTrue(_player.IsPausedForReason(PauseReason.OsFocusLoss),
                "Player must track OsFocusLoss as the pause source.");
        }

        /// <summary>
        /// BUSINESS REQUIREMENT: Resume with matching reason must actually resume.
        ///
        /// WHY: This is the Home-button-press bug fix. Home press pauses with OsFocusLoss,
        /// return pauses with OsFocusLoss, audio must resume.
        /// WHAT: After Pause(OsFocusLoss) + Resume(OsFocusLoss) the player is no longer paused.
        /// </summary>
        [Test]
        public void ResumePlayback_WithOsFocusLoss_AfterOsFocusLossPause_ActuallyResumes()
        {
            _player.StartStream(48000);
            _player.PausePlayback(PauseReason.OsFocusLoss);
            Assert.AreEqual(AudioPipelineState.Paused, _player.PipelineState);

            _player.ResumePlayback(PauseReason.OsFocusLoss);

            Assert.AreEqual(AudioPipelineState.Streaming, _player.PipelineState);
            Assert.IsFalse(_player.IsPausedForReason(PauseReason.OsFocusLoss));
        }

        /// <summary>
        /// BUSINESS REQUIREMENT: Resume with non-matching reason must NOT un-pause.
        ///
        /// WHY: If PauseManager paused (External) and the OS later sends a focus-regain event,
        /// the OS resume must not override the external pause. The training UI stays paused
        /// until the user explicitly un-pauses.
        /// WHAT: Pause(External) then Resume(OsFocusLoss) leaves player paused.
        /// </summary>
        [Test]
        public void ResumePlayback_WithOsFocusLoss_AfterExternalPause_StaysPaused()
        {
            _player.StartStream(48000);
            _player.PausePlayback(PauseReason.External);

            _player.ResumePlayback(PauseReason.OsFocusLoss);

            Assert.AreEqual(AudioPipelineState.Paused, _player.PipelineState,
                "OsFocusLoss resume must not clear an External pause.");
            Assert.IsTrue(_player.IsPausedForReason(PauseReason.External));
        }

        /// <summary>
        /// BUSINESS REQUIREMENT: Resume with External after OS-only pause must NOT un-pause.
        ///
        /// WHY: Symmetric to the previous test: if a stray External resume arrives while the
        /// OS has paused us, it must not silently un-pause.
        /// WHAT: Pause(OsFocusLoss) then Resume(External) leaves player paused.
        /// </summary>
        [Test]
        public void ResumePlayback_WithExternal_AfterOsFocusLossPause_StaysPaused()
        {
            _player.StartStream(48000);
            _player.PausePlayback(PauseReason.OsFocusLoss);

            _player.ResumePlayback(PauseReason.External);

            Assert.AreEqual(AudioPipelineState.Paused, _player.PipelineState);
            Assert.IsTrue(_player.IsPausedForReason(PauseReason.OsFocusLoss));
        }

        #endregion

        #region Overlapping Pauses

        /// <summary>
        /// BUSINESS REQUIREMENT: Two pause sources stacked must keep audio paused until both
        /// release.
        ///
        /// WHY: In realistic sessions a training pause (External) can coincide with an OS
        /// focus loss (user briefly takes the headset off while the pause menu is open).
        /// Releasing only one reason must not make the NPC suddenly speak.
        /// WHAT: Pause(External) + Pause(OsFocusLoss), Resume(External) leaves paused, Resume
        /// (OsFocusLoss) finally resumes.
        /// </summary>
        [Test]
        public void OverlappingPauses_BothMustReleaseBeforeResume()
        {
            _player.StartStream(48000);

            _player.PausePlayback(PauseReason.External);
            _player.PausePlayback(PauseReason.OsFocusLoss);

            Assert.AreEqual(AudioPipelineState.Paused, _player.PipelineState);
            Assert.IsTrue(_player.IsPausedForReason(PauseReason.External));
            Assert.IsTrue(_player.IsPausedForReason(PauseReason.OsFocusLoss));

            _player.ResumePlayback(PauseReason.External);
            Assert.AreEqual(AudioPipelineState.Paused, _player.PipelineState,
                "Only one of two pause sources released — must stay paused.");

            _player.ResumePlayback(PauseReason.OsFocusLoss);
            Assert.AreEqual(AudioPipelineState.Streaming, _player.PipelineState,
                "All pause sources released — must resume.");
        }

        /// <summary>
        /// BUSINESS REQUIREMENT: Pausing the same reason twice must be idempotent.
        ///
        /// WHY: A duplicated pause event (e.g., Unity firing OnApplicationFocus(false) twice
        /// on a flaky OS boundary) must not require two matching resumes to un-pause.
        /// WHAT: Pause(OsFocusLoss) twice, then Resume(OsFocusLoss) once, fully resumes.
        /// </summary>
        [Test]
        public void PausePlayback_SameReasonTwice_RequiresOneResume()
        {
            _player.StartStream(48000);

            _player.PausePlayback(PauseReason.OsFocusLoss);
            _player.PausePlayback(PauseReason.OsFocusLoss);

            _player.ResumePlayback(PauseReason.OsFocusLoss);

            Assert.AreEqual(AudioPipelineState.Streaming, _player.PipelineState,
                "Duplicate pause for the same reason must not require two resumes.");
        }

        #endregion

        #region Backwards Compatibility

        /// <summary>
        /// BUSINESS REQUIREMENT: Existing callers without a reason argument behave as External.
        ///
        /// WHY: RequestOrchestrator, NpcClient.PauseAudio, and OnEnable all call
        /// PausePlayback() without arguments. They represent training-level pauses, so the
        /// default reason must be External to preserve legacy semantics (no auto-resume from
        /// OS events).
        /// WHAT: Pause() without a reason + Resume(OsFocusLoss) leaves the player paused.
        /// </summary>
        [Test]
        public void PausePlayback_WithoutReason_DefaultsToExternal()
        {
            _player.StartStream(48000);

            _player.PausePlayback();

            Assert.AreEqual(AudioPipelineState.Paused, _player.PipelineState);
            Assert.IsTrue(_player.IsPausedForReason(PauseReason.External),
                "Default pause reason must be External for backwards compatibility.");
        }

        /// <summary>
        /// BUSINESS REQUIREMENT: Existing resume callers without a reason match the default pause.
        ///
        /// WHY: RequestOrchestrator and NpcClient call ResumePlayback() without arguments after
        /// PausePlayback(). Both must round-trip through the default reason.
        /// WHAT: Pause() + Resume() round-trips without leaving residual pause state.
        /// </summary>
        [Test]
        public void ResumePlayback_WithoutReason_ResumesDefaultExternalPause()
        {
            _player.StartStream(48000);
            _player.PausePlayback();

            _player.ResumePlayback();

            Assert.AreEqual(AudioPipelineState.Streaming, _player.PipelineState);
        }

        #endregion

        #region Shutdown Guard

        /// <summary>
        /// BUSINESS REQUIREMENT: Shutdown blocks all pause/resume regardless of reason.
        ///
        /// WHY: Once the pipeline is shut down (orb closed, NPC deactivated), no pause or
        /// resume call from any source should flip it out of ShuttingDown. Only ResetPipeline
        /// can do that.
        /// WHAT: After RequestShutdown, PausePlayback(OsFocusLoss) and ResumePlayback(any)
        /// leave the pipeline in ShuttingDown.
        /// </summary>
        [Test]
        public void PausePlayback_AnyReason_IgnoredDuringShutdown()
        {
            _player.RequestShutdown();

            _player.PausePlayback(PauseReason.OsFocusLoss);
            _player.PausePlayback(PauseReason.External);
            _player.PausePlayback(PauseReason.OsApplicationPause);

            Assert.AreEqual(AudioPipelineState.ShuttingDown, _player.PipelineState);
        }

        [Test]
        public void ResumePlayback_AnyReason_IgnoredDuringShutdown()
        {
            _player.RequestShutdown();

            _player.ResumePlayback(PauseReason.OsFocusLoss);
            _player.ResumePlayback(PauseReason.External);
            _player.ResumePlayback(PauseReason.OsApplicationPause);

            Assert.AreEqual(AudioPipelineState.ShuttingDown, _player.PipelineState);
        }

        #endregion
    }
}
