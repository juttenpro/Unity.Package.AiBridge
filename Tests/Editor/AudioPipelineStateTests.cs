using NUnit.Framework;
using Tsc.AIBridge.Audio.Playback;
using Tsc.AIBridge.Audio.Processing;
using UnityEngine;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: NPC audio must stop immediately when coach orb closes
    ///
    /// WHY: Without a unified pipeline state, multiple methods (StopPlayback, RestoreStreamingMode,
    ///      ProcessAudioQueue) fight over audio state. RestoreStreamingMode unmutes audio that
    ///      ShutdownAudioPipeline just muted. StartStream resets _forceStop allowing WebSocket data
    ///      to restart audio after a deliberate stop. This causes coaches to keep talking for 2-3 seconds
    ///      after the orb closes.
    /// WHAT: Tests that AudioPipelineState correctly manages state transitions and that
    ///       ShuttingDown state blocks all new audio until explicitly reset.
    /// HOW: Creates StreamingAudioPlayer instances and tests state transitions, blocking behavior,
    ///      and pipeline reset flow.
    ///
    /// SUCCESS CRITERIA:
    /// - ShuttingDown blocks StartStream, PausePlayback, ResumePlayback
    /// - RequestShutdown sets _forceStop and clears buffer
    /// - ResetPipeline transitions from ShuttingDown back to Idle
    /// - FillAudioBuffer outputs silence during ShuttingDown
    /// - Normal StopPlayback transitions to Idle (not ShuttingDown) for interruptions
    ///
    /// BUSINESS IMPACT:
    /// - Falen = coach keeps talking after orb closes (2-3s delay, confusing for users)
    /// - Buffered audio accumulates (60s+) blocking PTT triggers for ALL NPCs
    /// - Users can't interact with the training scenario until stale audio drains
    /// </summary>
    [TestFixture]
    public class AudioPipelineStateTests
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

        #region Initial State

        /// <summary>
        /// BUSINESS REQUIREMENT: New NPC starts in Idle state, ready to receive audio
        ///
        /// WHY: NPCs are instantiated before any conversation starts
        /// WHAT: Verify initial state is Idle
        /// </summary>
        [Test]
        public void PipelineState_InitialState_IsIdle()
        {
            Assert.AreEqual(AudioPipelineState.Idle, _player.PipelineState);
        }

        #endregion

        #region RequestShutdown Tests

        /// <summary>
        /// BUSINESS REQUIREMENT: Coach orb close must immediately stop all audio
        ///
        /// WHY: When user closes the orb, coach must stop talking mid-sentence
        /// WHAT: RequestShutdown transitions to ShuttingDown
        /// </summary>
        [Test]
        public void RequestShutdown_TransitionsToShuttingDown()
        {
            _player.RequestShutdown();

            Assert.AreEqual(AudioPipelineState.ShuttingDown, _player.PipelineState);
        }

        /// <summary>
        /// BUSINESS REQUIREMENT: No new audio streams can start after shutdown
        ///
        /// WHY: WebSocket data arriving after shutdown was restarting audio via StartStream → _forceStop=false
        /// WHAT: StartStream is blocked when ShuttingDown
        /// </summary>
        [Test]
        public void StartStream_WhenShuttingDown_IsBlocked()
        {
            _player.RequestShutdown();

            _player.StartStream(48000);

            // Should still be ShuttingDown, not Streaming
            Assert.AreEqual(AudioPipelineState.ShuttingDown, _player.PipelineState);
        }

        /// <summary>
        /// BUSINESS REQUIREMENT: Pause/Resume must not interfere with shutdown
        ///
        /// WHY: PauseManager events could theoretically arrive after shutdown
        /// WHAT: PausePlayback is ignored when ShuttingDown
        /// </summary>
        [Test]
        public void PausePlayback_WhenShuttingDown_IsIgnored()
        {
            _player.RequestShutdown();

            _player.PausePlayback();

            Assert.AreEqual(AudioPipelineState.ShuttingDown, _player.PipelineState);
        }

        /// <summary>
        /// BUSINESS REQUIREMENT: Resume must not restart audio after shutdown
        ///
        /// WHY: ResumePlayback could undo a shutdown if not guarded
        /// WHAT: ResumePlayback is ignored when ShuttingDown
        /// </summary>
        [Test]
        public void ResumePlayback_WhenShuttingDown_IsIgnored()
        {
            _player.RequestShutdown();

            _player.ResumePlayback();

            Assert.AreEqual(AudioPipelineState.ShuttingDown, _player.PipelineState);
        }

        #endregion

        #region ResetPipeline Tests

        /// <summary>
        /// BUSINESS REQUIREMENT: Coach must be able to speak again when orb reopens
        ///
        /// WHY: ResetPipeline is called by OnEnable when coach NPC is reactivated
        /// WHAT: Transitions from ShuttingDown back to Idle
        /// </summary>
        [Test]
        public void ResetPipeline_FromShuttingDown_TransitionsToIdle()
        {
            _player.RequestShutdown();
            Assert.AreEqual(AudioPipelineState.ShuttingDown, _player.PipelineState);

            _player.ResetPipeline();

            Assert.AreEqual(AudioPipelineState.Idle, _player.PipelineState);
        }

        /// <summary>
        /// BUSINESS REQUIREMENT: After reset, new audio streams must work
        ///
        /// WHY: The coach needs to respond when the user asks a question after reopening the orb
        /// WHAT: StartStream works after ResetPipeline
        /// </summary>
        [Test]
        public void StartStream_AfterReset_TransitionsToStreaming()
        {
            _player.RequestShutdown();
            _player.ResetPipeline();

            _player.StartStream(48000);

            Assert.AreEqual(AudioPipelineState.Streaming, _player.PipelineState);
        }

        /// <summary>
        /// BUSINESS REQUIREMENT: ResetPipeline only works from ShuttingDown
        ///
        /// WHY: Calling ResetPipeline from Idle/Streaming would be a no-op
        /// WHAT: No state change when not in ShuttingDown
        /// </summary>
        [Test]
        public void ResetPipeline_FromIdle_NoChange()
        {
            Assert.AreEqual(AudioPipelineState.Idle, _player.PipelineState);

            _player.ResetPipeline();

            Assert.AreEqual(AudioPipelineState.Idle, _player.PipelineState);
        }

        #endregion

        #region Normal Flow Tests

        /// <summary>
        /// BUSINESS REQUIREMENT: Streaming audio must play during normal conversation
        ///
        /// WHY: StartStream is called when TTS audio arrives from backend
        /// WHAT: StartStream transitions from Idle to Streaming
        /// </summary>
        [Test]
        public void StartStream_FromIdle_TransitionsToStreaming()
        {
            _player.StartStream(48000);

            Assert.AreEqual(AudioPipelineState.Streaming, _player.PipelineState);
        }

        /// <summary>
        /// BUSINESS REQUIREMENT: User interruption stops audio but allows next turn
        ///
        /// WHY: InterruptionManager calls StopPlayback, NPC must speak again next turn
        /// WHAT: StopPlayback transitions to Idle (NOT ShuttingDown)
        /// </summary>
        [Test]
        public void StopPlayback_TransitionsToIdle_NotShuttingDown()
        {
            _player.StartStream(48000);

            _player.StopPlayback(wasInterrupted: true);

            Assert.AreEqual(AudioPipelineState.Idle, _player.PipelineState);
        }

        /// <summary>
        /// BUSINESS REQUIREMENT: After interruption, NPC can start new stream
        ///
        /// WHY: Next conversation turn needs to work after user interrupts
        /// WHAT: StartStream works after StopPlayback
        /// </summary>
        [Test]
        public void StartStream_AfterStopPlayback_Works()
        {
            _player.StartStream(48000);
            _player.StopPlayback(wasInterrupted: true);

            _player.StartStream(48000);

            Assert.AreEqual(AudioPipelineState.Streaming, _player.PipelineState);
        }

        #endregion

        #region Pause Flow Tests

        /// <summary>
        /// BUSINESS REQUIREMENT: PauseManager must pause audio during training pause
        ///
        /// WHY: When the coach orb opens, PauseManager.Pause() is called
        /// WHAT: PausePlayback transitions to Paused, preserving previous state
        /// </summary>
        [Test]
        public void PausePlayback_FromStreaming_TransitionsToPaused()
        {
            _player.StartStream(48000);

            _player.PausePlayback();

            Assert.AreEqual(AudioPipelineState.Paused, _player.PipelineState);
        }

        /// <summary>
        /// BUSINESS REQUIREMENT: Audio must resume after pause with no data loss
        ///
        /// WHY: When coach orb closes normally (without shutdown), audio should resume
        /// WHAT: ResumePlayback restores previous state
        /// </summary>
        [Test]
        public void ResumePlayback_RestoresPreviousState()
        {
            _player.StartStream(48000);
            _player.PausePlayback();
            Assert.AreEqual(AudioPipelineState.Paused, _player.PipelineState);

            _player.ResumePlayback();

            Assert.AreEqual(AudioPipelineState.Streaming, _player.PipelineState);
        }

        #endregion

        #region FillAudioBuffer Shutdown Guard

        /// <summary>
        /// BUSINESS REQUIREMENT: No audio output during shutdown
        ///
        /// WHY: Even if buffer has data, FillAudioBuffer must output silence when shut down
        /// WHAT: FillAudioBuffer clears data array when pipeline is ShuttingDown
        /// </summary>
        [Test]
        public void FillAudioBuffer_WhenShuttingDown_OutputsSilence()
        {
            _player.StartStream(48000);

            // Add some audio data to the buffer
            var samples = new float[480];
            for (int i = 0; i < samples.Length; i++)
                samples[i] = 0.5f;
            _player.AddAudioData(samples);

            // Shutdown
            _player.RequestShutdown();

            // FillAudioBuffer should output silence
            var output = new float[480];
            for (int i = 0; i < output.Length; i++)
                output[i] = 1.0f; // Fill with non-zero to verify it gets cleared

            _player.FillAudioBuffer(output, 1);

            // All samples should be zero (silence)
            for (int i = 0; i < output.Length; i++)
            {
                Assert.AreEqual(0f, output[i], $"Sample {i} should be silence during shutdown");
            }
        }

        #endregion

        #region AudioStreamProcessor Integration

        /// <summary>
        /// BUSINESS REQUIREMENT: AudioStreamProcessor must respect pipeline shutdown
        ///
        /// WHY: WebSocket audio chunks arriving after shutdown must be discarded
        /// WHAT: StartAudioStream is blocked when player is ShuttingDown
        /// </summary>
        [Test]
        public void AudioStreamProcessor_StartAudioStream_BlockedWhenShuttingDown()
        {
            var processor = new AudioStreamProcessor(_player, isVerboseLogging: false);

            _player.RequestShutdown();

            // StartAudioStream should not change IsStreamingAudio
            processor.StartAudioStream(true, 48000);

            Assert.IsFalse(processor.IsStreamingAudio,
                "StartAudioStream should be blocked when player pipeline is shut down");

            processor.Dispose();
        }

        #endregion
    }
}
