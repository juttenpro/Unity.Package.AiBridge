using NUnit.Framework;
using Tsc.AIBridge.Audio.Playback;
using Tsc.AIBridge.Audio.Processing;
using Tsc.AIBridge.Handlers;
using UnityEngine;
using UnityEngine.TestTools;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: when incoming TTS audio can no longer reach playback, production logs must
    /// say so.
    ///
    /// WHY: AudioMessageHandler opens a stream only on the FIRST OGG header of a turn
    /// (_receivedStreamCount == 1). Later headers are treated as continuations, which is correct while a
    /// stream is open — providers split one reaction across several OGG containers. But if the counter
    /// was not reset between turns, the next reaction's header lands in that same branch with no stream
    /// open, nothing ever calls StartAudioStream, and every byte is discarded by AudioStreamProcessor as
    /// "after stream end". The NPC is silent while the log shows a perfectly good LLM response.
    ///
    /// This state was reported on 2026-08-20 ("coach has no audio after closing and reopening the orb",
    /// 186 KB discarded across three reactions) and could not be diagnosed afterwards: the branch that
    /// decides it only logged under enableVerboseLogging, so the session log showed the consequence
    /// (Dropped ... bytes) and never the cause.
    ///
    /// WHAT: that branch must warn — unconditionally, once per turn — and name the state needed to tell
    /// the candidate causes apart: the counter, the requestId, and the two gates that can suppress a
    /// stream start.
    ///
    /// SUCCESS CRITERIA:
    /// - An OGG header with no open stream logs a warning naming the stream counter
    /// - The warning does not repeat for every chunk of the same broken turn
    /// - Normal continuation headers (stream open) stay silent
    ///
    /// BUSINESS IMPACT: without this, every future report of this class costs another round of asking a
    /// colleague to reproduce with verbose logging on.
    /// </summary>
    [TestFixture]
    public class StreamlessAudioDiagnosticsTests
    {
        private GameObject _gameObject;
        private StreamingAudioPlayer _player;
        private AudioStreamProcessor _processor;
        private AudioMessageHandler _handler;

        private static byte[] OggChunk(int payloadBytes = 64)
        {
            var data = new byte[4 + payloadBytes];
            data[0] = 0x4F; // O
            data[1] = 0x67; // g
            data[2] = 0x67; // g
            data[3] = 0x53; // S
            return data;
        }

        [SetUp]
        public void SetUp()
        {
            StreamingAudioPlayer.SetGlobalTestMode(true);

            _gameObject = new GameObject("TestAudioPlayer");
            _gameObject.AddComponent<AudioSource>();
            _player = _gameObject.AddComponent<StreamingAudioPlayer>();
            _processor = new AudioStreamProcessor(_player, isVerboseLogging: false);
            _handler = new AudioMessageHandler("TestPersona", _processor, enableVerboseLogging: false);
        }

        [TearDown]
        public void TearDown()
        {
            if (_gameObject != null)
                Object.DestroyImmediate(_gameObject);

            StreamingAudioPlayer.SetGlobalTestMode(false);
        }

        /// <summary>
        /// The diagnosable state: a turn's header arrives while the counter still carries the previous
        /// turn, so no stream opens and the audio is discarded in silence.
        /// </summary>
        [Test]
        public void OggHeaderWithNoOpenStream_WarnsOnce()
        {
            // First header of the previous turn opens a stream.
            _handler.ProcessBinaryMessage(OggChunk());
            Assert.IsTrue(_processor.IsStreamingAudio, "Precondition: the first header opens a stream.");

            // That stream ends, but the counter is never reset — the bug being diagnosed.
            _processor.EndAudioStream();
            Assert.IsFalse(_processor.IsStreamingAudio, "Precondition: no stream is open any more.");

            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(
                "OGG header #2 arrived with NO open audio stream"));

            _handler.ProcessBinaryMessage(OggChunk());
        }

        /// <summary>
        /// One broken turn keeps sending chunks; the warning must not turn into a per-chunk flood.
        /// </summary>
        [Test]
        public void RepeatedStreamlessHeaders_WarnOnlyOnce()
        {
            _handler.ProcessBinaryMessage(OggChunk());
            _processor.EndAudioStream();

            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(
                "arrived with NO open audio stream"));

            _handler.ProcessBinaryMessage(OggChunk());
            _handler.ProcessBinaryMessage(OggChunk());
            _handler.ProcessBinaryMessage(OggChunk());

            // LogAssert fails the test on any unexpected log, so a second warning would surface here.
        }

        /// <summary>
        /// The normal case must stay quiet: several containers within one reaction, stream still open.
        /// </summary>
        [Test]
        public void ContinuationHeaderWhileStreaming_DoesNotWarn()
        {
            _handler.ProcessBinaryMessage(OggChunk());
            Assert.IsTrue(_processor.IsStreamingAudio);

            _handler.ProcessBinaryMessage(OggChunk());
            _handler.ProcessBinaryMessage(OggChunk());

            Assert.IsTrue(_processor.IsStreamingAudio,
                "Providers split one reaction across containers — that is not an error.");
        }

        /// <summary>
        /// After a reset the next turn is a fresh start, and a later break must be reportable again.
        /// </summary>
        [Test]
        public void AfterReset_TheWarningCanFireAgain()
        {
            _handler.ProcessBinaryMessage(OggChunk());
            _processor.EndAudioStream();

            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(
                "arrived with NO open audio stream"));
            _handler.ProcessBinaryMessage(OggChunk());

            _handler.Reset();

            // Fresh turn: header #1 opens a stream again.
            _handler.ProcessBinaryMessage(OggChunk());
            Assert.IsTrue(_processor.IsStreamingAudio, "Reset makes the next header a first header again.");

            _processor.EndAudioStream();
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(
                "arrived with NO open audio stream"));
            _handler.ProcessBinaryMessage(OggChunk());
        }
    }
}
