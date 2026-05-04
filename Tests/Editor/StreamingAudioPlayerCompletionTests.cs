using NUnit.Framework;
using Tsc.AIBridge.Audio.Playback;
using UnityEngine;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: Playback completion must be driven by an explicit server signal,
    /// not by a 150ms buffer-drain heuristic.
    ///
    /// WHY: ElevenLabs streams audio per sentence with arbitrary inter-sentence latency. The old
    /// 0.15s buffer-drain timeout fires DURING normal multi-sentence streaming under typical
    /// network jitter. That triggers AudioMessageHandler.Reset → OpusStreamDecoder.Reset, after
    /// which the next mid-stream OGG audio page is misidentified as a new stream and the parser
    /// throws "Invalid OpusHead signature: h..." (the 'h' = 0x68 is the Opus TOC byte of an
    /// audio packet — proof the parser was looking at audio while expecting headers).
    ///
    /// WHAT: The player completes playback when the orchestrator explicitly signals "stream end"
    /// via MarkServerStreamEnd() AND the audio buffer has drained. A long safety-net timeout
    /// (3s) covers the rare server-crash scenario without firing during normal jitter.
    ///
    /// HOW: We exercise the new pure decision function EvaluateAutoComplete(...) directly. It
    /// takes all inputs as parameters so the test does not need to reach into private state or
    /// drive Unity's Update loop.
    ///
    /// SUCCESS CRITERIA:
    /// - Server signal + empty buffer → completes immediately (no waiting)
    /// - No signal + empty buffer + jitter (&lt;3s) → does NOT complete
    /// - No signal + empty buffer + 3s elapsed → completes via safety net
    /// - Server signal + non-empty buffer → does NOT complete (wait for drain)
    /// - StartStream() resets the server-signal flag so a previous turn's flag never leaks
    ///   into the next turn
    ///
    /// BUSINESS IMPACT: Without this fix, every multi-sentence ElevenLabs response with mild
    /// network jitter logs "Failed to parse OpusHead" and produces audible glitches between
    /// sentences. End users hear cuts; the log noise masks real bugs.
    /// </summary>
    [TestFixture]
    public class StreamingAudioPlayerCompletionTests
    {
        private GameObject _testGameObject;
        private StreamingAudioPlayer _player;

        // Inter-sentence gap that the old 150ms timeout misidentified as end-of-stream.
        private const float JitterToleranceSeconds = 0.5f;

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
            {
                UnityEngine.Object.DestroyImmediate(_testGameObject);
            }
        }

        [Test]
        public void EvaluateAutoComplete_ServerSignalAndBufferEmpty_CompletesImmediately()
        {
            _player.MarkServerStreamEnd();

            var shouldComplete = _player.EvaluateAutoComplete(
                isPlaybackStarted: true,
                bufferEmpty: true,
                timeSinceLastData: 0.01f);

            Assert.IsTrue(shouldComplete,
                "Server signal + empty buffer must complete immediately, with no time-based wait");
        }

        [Test]
        public void EvaluateAutoComplete_BufferEmptyWithJitterButNoServerSignal_DoesNotComplete()
        {
            // The bug we're fixing: a 0.5s gap (typical inter-sentence latency under jitter)
            // must NOT be treated as end-of-stream when the server has not signalled it.
            var shouldComplete = _player.EvaluateAutoComplete(
                isPlaybackStarted: true,
                bufferEmpty: true,
                timeSinceLastData: JitterToleranceSeconds);

            Assert.IsFalse(shouldComplete,
                "Without server signal, a 0.5s buffer drain must not complete playback (regression on the OpusHead-parse bug)");
        }

        [Test]
        public void EvaluateAutoComplete_BufferEmptyForLongerThanSafetyNet_CompletesAsFallback()
        {
            // Safety net for the rare server-crash case: even without a signal, eventually we
            // give up and finalize. Threshold is intentionally long enough to never fire
            // during normal streaming. We probe just past the player's configured safety net.
            float safetyNet = _player.SafetyNetCompletionTimeoutSeconds;

            var shouldComplete = _player.EvaluateAutoComplete(
                isPlaybackStarted: true,
                bufferEmpty: true,
                timeSinceLastData: safetyNet + 0.1f);

            Assert.IsTrue(shouldComplete,
                "Safety-net timeout must complete playback when the server signal never arrives");
        }

        [Test]
        public void EvaluateAutoComplete_ServerSignalButBufferNonEmpty_DoesNotComplete()
        {
            // Server told us the stream ended, but we still have audio queued — finish playing
            // it before firing OnPlaybackComplete.
            _player.MarkServerStreamEnd();

            var shouldComplete = _player.EvaluateAutoComplete(
                isPlaybackStarted: true,
                bufferEmpty: false,
                timeSinceLastData: 0.01f);

            Assert.IsFalse(shouldComplete,
                "Server signal alone must not finalize while audio is still queued for playback");
        }

        [Test]
        public void EvaluateAutoComplete_PlaybackNotStarted_DoesNotComplete()
        {
            float safetyNet = _player.SafetyNetCompletionTimeoutSeconds;
            _player.MarkServerStreamEnd();

            var shouldComplete = _player.EvaluateAutoComplete(
                isPlaybackStarted: false,
                bufferEmpty: true,
                timeSinceLastData: safetyNet + 1f);

            Assert.IsFalse(shouldComplete,
                "If playback never started, there is nothing to complete");
        }

        [Test]
        public void StartStream_ClearsServerStreamEndFlag()
        {
            // Without this, a flag from turn N would let turn N+1 finalize prematurely if its
            // first chunk hasn't arrived yet.
            _player.MarkServerStreamEnd();
            Assert.IsTrue(_player.IsServerStreamEnd, "Precondition: flag is set before StartStream");

            _player.StartStream(48000);

            Assert.IsFalse(_player.IsServerStreamEnd,
                "StartStream must clear the server-stream-end flag so a stale signal cannot finalize the next turn");
        }

        [Test]
        public void SafetyNetTimeout_IsAtLeast1Second()
        {
            // The whole point of the architectural fix is that the timeout is no longer
            // aggressive enough to fire during normal inter-sentence jitter. Lock that in
            // with a sanity check.
            Assert.GreaterOrEqual(_player.SafetyNetCompletionTimeoutSeconds, 1.0f,
                "Safety-net timeout must be at least 1s (was 0.15s — the original bug)");
        }
    }
}
