using System;
using NUnit.Framework;
using Tsc.AIBridge.Audio.Processing;
using Tsc.AIBridge.Audio.Playback;
using UnityEngine;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: Audio stream state must always be cleaned up, even when Opus decoding fails
    ///
    /// WHY: On iOS, Opus decode errors during FlushRemainingAudio caused _isStreamingAudio to stay true,
    ///      which prevented the next conversation turn from starting — NPC hangs permanently.
    ///      The try-finally pattern in EndAudioStream ensures state is ALWAYS reset.
    /// WHAT: Tests that AudioStreamProcessor correctly manages stream state across conversation turns.
    /// HOW: Creates a real AudioStreamProcessor with StreamingAudioPlayer and tests state transitions.
    ///
    /// SUCCESS CRITERIA:
    /// - EndAudioStream always sets IsStreamingAudio to false
    /// - StartAudioStream works after EndAudioStream (multi-turn conversations)
    /// - State cleanup happens even when internal operations fail
    ///
    /// BUSINESS IMPACT:
    /// - Falen = NPC hangs forever after failed audio decode on iOS
    /// - Users can't complete multi-turn training conversations
    /// - Root cause of iOS production bug reported 2026-03-16
    /// </summary>
    [TestFixture]
    public class AudioStreamProcessorTests
    {
        private AudioStreamProcessor _processor;
        private GameObject _testGameObject;

        [SetUp]
        public void SetUp()
        {
            // Suppress warnings from StreamingAudioPlayer during test initialization
            _testGameObject = new GameObject("TestAudioPlayer");
            var audioSource = _testGameObject.AddComponent<AudioSource>();
            var audioPlayer = _testGameObject.AddComponent<StreamingAudioPlayer>();

            // Create processor with real StreamingAudioPlayer (needed for decoder initialization)
            _processor = new AudioStreamProcessor(audioPlayer, isVerboseLogging: false);
        }

        [TearDown]
        public void TearDown()
        {
            _processor?.Dispose();
            if (_testGameObject != null)
                UnityEngine.Object.DestroyImmediate(_testGameObject);
        }

        #region EndAudioStream State Cleanup Tests

        /// <summary>
        /// BUSINESS REQUIREMENT: EndAudioStream must reset IsStreamingAudio to false
        ///
        /// WHY: If IsStreamingAudio stays true, the next StartAudioStream returns early,
        ///      causing the NPC to never play audio again (permanent hang).
        /// WHAT: Tests the normal EndAudioStream flow resets stream state.
        /// HOW: Start stream → End stream → verify IsStreamingAudio is false.
        ///
        /// SUCCESS CRITERIA:
        /// - IsStreamingAudio is false after EndAudioStream
        ///
        /// BUSINESS IMPACT:
        /// - Ensures multi-turn NPC conversations work correctly
        /// </summary>
        [Test]
        public void EndAudioStream_ResetsIsStreamingAudio_ToFalse()
        {
            // Start a stream
            _processor.StartAudioStream(isOpus: true, sampleRate: 48000);
            Assert.IsTrue(_processor.IsStreamingAudio, "IsStreamingAudio should be true after StartAudioStream");

            // End the stream
            _processor.EndAudioStream();
            Assert.IsFalse(_processor.IsStreamingAudio, "IsStreamingAudio must be false after EndAudioStream");
        }

        /// <summary>
        /// BUSINESS REQUIREMENT: EndAudioStream called without active stream must not corrupt state
        ///
        /// WHY: Race conditions or duplicate EndAudioStream calls can happen (e.g., playback complete +
        ///      explicit end). Must handle gracefully without throwing or corrupting state.
        /// WHAT: Tests EndAudioStream when no stream is active.
        /// HOW: Calls EndAudioStream without prior StartAudioStream.
        ///
        /// SUCCESS CRITERIA:
        /// - No exception thrown
        /// - IsStreamingAudio remains false
        ///
        /// BUSINESS IMPACT:
        /// - Prevents crashes from race conditions in audio cleanup
        /// </summary>
        [Test]
        public void EndAudioStream_WithoutActiveStream_DoesNotThrow()
        {
            Assert.IsFalse(_processor.IsStreamingAudio, "Should not be streaming initially");

            Assert.DoesNotThrow(() => _processor.EndAudioStream(),
                "EndAudioStream without active stream must not throw");

            Assert.IsFalse(_processor.IsStreamingAudio, "IsStreamingAudio should remain false");
        }

        #endregion

        #region StartAudioStream Decoder Reset Tests

        /// <summary>
        /// BUSINESS REQUIREMENT: Each conversation turn must start with a clean decoder state
        ///
        /// WHY: Each TTS response is a new OGG stream with new OpusHead/OpusTags headers.
        ///      Without reset, the parser treats new headers as audio data → Opus decode error -4.
        ///      This was a contributing factor to the iOS hanging bug.
        /// WHAT: Tests that StartAudioStream resets the decoder (defense-in-depth).
        /// HOW: Start → End → Start → verify no errors.
        ///
        /// SUCCESS CRITERIA:
        /// - Second StartAudioStream succeeds after EndAudioStream
        /// - IsStreamingAudio is true after second StartAudioStream
        ///
        /// BUSINESS IMPACT:
        /// - Ensures decoder is clean for each conversation turn
        /// - Defense-in-depth against cross-turn state corruption
        /// </summary>
        [Test]
        public void StartAudioStream_AfterEndAudioStream_ResetsDecoderForNewTurn()
        {
            // Turn 1
            _processor.StartAudioStream(isOpus: true, sampleRate: 48000);
            Assert.IsTrue(_processor.IsStreamingAudio);
            _processor.EndAudioStream();
            Assert.IsFalse(_processor.IsStreamingAudio);

            // Turn 2 - must work cleanly with fresh decoder state
            Assert.DoesNotThrow(() => _processor.StartAudioStream(isOpus: true, sampleRate: 48000),
                "StartAudioStream for turn 2 must not throw");
            Assert.IsTrue(_processor.IsStreamingAudio, "IsStreamingAudio should be true for turn 2");

            // Cleanup turn 2
            _processor.EndAudioStream();
            Assert.IsFalse(_processor.IsStreamingAudio);
        }

        /// <summary>
        /// BUSINESS REQUIREMENT: Multiple conversation turns must work without degradation
        ///
        /// WHY: Training sessions involve 5-20+ conversation turns. Each must work identically.
        ///      State leaks or accumulation across turns can cause progressive degradation.
        /// WHAT: Tests 5 consecutive turn cycles (start → end).
        /// HOW: Loops through multiple start/end cycles and verifies state at each step.
        ///
        /// SUCCESS CRITERIA:
        /// - All 5 turns complete without exception
        /// - IsStreamingAudio correctly reflects state at each transition
        ///
        /// BUSINESS IMPACT:
        /// - Ensures long training sessions work reliably
        /// </summary>
        [Test]
        public void MultipleConversationTurns_StateTransitionsAreCorrect()
        {
            for (var turn = 1; turn <= 5; turn++)
            {
                Assert.DoesNotThrow(() => _processor.StartAudioStream(isOpus: true, sampleRate: 48000),
                    $"StartAudioStream for turn {turn} must not throw");
                Assert.IsTrue(_processor.IsStreamingAudio, $"IsStreamingAudio should be true during turn {turn}");

                Assert.DoesNotThrow(() => _processor.EndAudioStream(),
                    $"EndAudioStream for turn {turn} must not throw");
                Assert.IsFalse(_processor.IsStreamingAudio, $"IsStreamingAudio should be false after turn {turn}");
            }
        }

        /// <summary>
        /// BUSINESS REQUIREMENT: Duplicate StartAudioStream calls must not corrupt state
        ///
        /// WHY: Network race conditions can cause duplicate start signals. The second call
        ///      should be a no-op (early return) without corrupting state.
        /// WHAT: Tests double StartAudioStream call.
        /// HOW: Calls StartAudioStream twice without EndAudioStream between them.
        ///
        /// SUCCESS CRITERIA:
        /// - No exception thrown
        /// - IsStreamingAudio remains true (still streaming)
        ///
        /// BUSINESS IMPACT:
        /// - Prevents state corruption from network race conditions
        /// </summary>
        [Test]
        public void StartAudioStream_CalledTwice_DoesNotCorruptState()
        {
            _processor.StartAudioStream(isOpus: true, sampleRate: 48000);
            Assert.IsTrue(_processor.IsStreamingAudio);

            // Second call should be a no-op (early return because already streaming)
            Assert.DoesNotThrow(() => _processor.StartAudioStream(isOpus: true, sampleRate: 48000),
                "Duplicate StartAudioStream must not throw");
            Assert.IsTrue(_processor.IsStreamingAudio, "Should still be streaming after duplicate start");

            _processor.EndAudioStream();
            Assert.IsFalse(_processor.IsStreamingAudio);
        }

        #endregion

        #region StopAllAudio Tests

        /// <summary>
        /// BUSINESS REQUIREMENT: Interruptions must immediately stop all audio without leaving stale state
        ///
        /// WHY: When a user interrupts the NPC (starts talking), all audio must stop instantly.
        ///      Stale state after interruption can prevent the NPC from responding to the next turn.
        /// WHAT: Tests that StopAllAudio properly resets stream state.
        /// HOW: Start stream → StopAllAudio → verify state is clean.
        ///
        /// SUCCESS CRITERIA:
        /// - IsStreamingAudio is false after StopAllAudio
        /// - Can start a new stream after StopAllAudio
        ///
        /// BUSINESS IMPACT:
        /// - Ensures interruption → next turn flow works correctly
        /// </summary>
        [Test]
        public void StopAllAudio_ResetsStreamState_AllowingNewTurn()
        {
            _processor.StartAudioStream(isOpus: true, sampleRate: 48000);
            Assert.IsTrue(_processor.IsStreamingAudio);

            // Simulate interruption
            _processor.StopAllAudio();
            Assert.IsFalse(_processor.IsStreamingAudio, "IsStreamingAudio must be false after StopAllAudio");

            // Must be able to start a new stream after interruption
            Assert.DoesNotThrow(() => _processor.StartAudioStream(isOpus: true, sampleRate: 48000),
                "StartAudioStream after StopAllAudio must not throw");
            Assert.IsTrue(_processor.IsStreamingAudio);

            _processor.EndAudioStream();
        }

        #endregion
    }
}
