using System.Collections.Generic;
using NUnit.Framework;
using Tsc.AIBridge.Audio.Playback;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// The 5.27.0 members that make the queued audio readable ahead of playback (SampleRate, StreamStarted,
    /// AudioQueued, StreamEnded, PlayedFrames). A lipsync model reads an utterance's audio from AudioQueued,
    /// some 100 ms before it is heard, and matches it to what is heard through PlayedFrames. If the order of
    /// the events or the meaning of PlayedFrames is off, the mouth runs ahead of or behind the voice.
    /// The tests drive FillAudioBuffer directly, as AudioFilterRelay does on the audio thread; they do not
    /// cover the real audio thread's timing or a real network stream.
    /// </summary>
    [TestFixture]
    public class StreamingAudioPlayerAheadOfPlaybackTests
    {
        private const int Rate = 24000;
        private const int Priming = Rate / 4; // the 250 ms priming buffer at this rate
        private GameObject _gameObject;
        private StreamingAudioPlayer _player;
        private List<string> _events;

        [SetUp]
        public void SetUp()
        {
            _gameObject = new GameObject("AheadOfPlaybackPlayer");
            _gameObject.AddComponent<AudioSource>();
            _player = _gameObject.AddComponent<StreamingAudioPlayer>();
            _events = new List<string>();
            _player.StreamStarted += rate => _events.Add("started " + rate);
            _player.AudioQueued += samples => _events.Add("queued " + samples.Length);
            _player.StreamEnded += interrupted => _events.Add("ended " + (interrupted ? "interrupted" : "natural"));
        }

        [TearDown]
        public void TearDown()
        {
            if (_gameObject != null)
            {
                Object.DestroyImmediate(_gameObject);
            }
        }

        private static float[] Samples(int count, float value)
        {
            var samples = new float[count];
            for (var i = 0; i < count; i++)
            {
                samples[i] = value;
            }

            return samples;
        }

        /// <summary>Starts a stream and queues enough audio to pass the priming buffer, so playback runs.</summary>
        private void StartPlaying(float value = 0.5f)
        {
            _player.StartStream(Rate);
            _player.AddAudioData(Samples(Priming + 2048, value));
        }

        [Test]
        public void StartStream_RaisesStreamStarted_WithItsSampleRate()
        {
            _player.StartStream(Rate);

            Assert.That(_events, Is.EqualTo(new[] { "started " + Rate }),
                "The model never learns an utterance began, so it has nothing to put the audio in.");
            Assert.That(_player.SampleRate, Is.EqualTo(Rate),
                "SampleRate must be the stream's rate, or the model reads the audio at the wrong speed.");
        }

        [Test]
        public void AddAudioData_RaisesAudioQueued_AfterStreamStarted_WithEveryChunk()
        {
            _player.StartStream(Rate);
            _player.AddAudioData(new float[960]);
            _player.AddAudioData(new float[480]);

            Assert.That(_events, Is.EqualTo(new[] { "started " + Rate, "queued 960", "queued 480" }),
                "Every queued chunk must reach the model, in order, after the utterance opened.");
        }

        [Test]
        public void AudioQueued_ListenerThatChangesTheArray_DoesNotChangeWhatPlays()
        {
            _player.AudioQueued += samples => System.Array.Clear(samples, 0, samples.Length);
            StartPlaying(0.5f);

            var output = new float[512 * 2];
            _player.FillAudioBuffer(output, 2);

            Assert.That(output[output.Length - 1], Is.EqualTo(0.5f),
                "A listener that writes into the array it was given changed the voice: the samples must be " +
                "queued before AudioQueued is raised.");
        }

        [Test]
        public void PlayedFrames_CountsMonoFramesTakenFromTheQueue_NotStereoOutputSamples()
        {
            StartPlaying();

            _player.FillAudioBuffer(new float[1024 * 2], 2);

            Assert.That(_player.PlayedFrames, Is.EqualTo(1024),
                "1024 stereo frames take 1024 mono samples from the queue. Counting 2048 would put the " +
                "mouth twice as far ahead as the voice.");
        }

        [Test]
        public void PlayedFrames_StopsAtAnUnderrun()
        {
            _player.StartStream(Rate);
            _player.AddAudioData(new float[Priming]);

            _player.FillAudioBuffer(new float[(Priming + 500) * 2], 2);

            Assert.That(_player.PlayedFrames, Is.EqualTo(Priming),
                "Silence filled in during an underrun was counted as played voice: the mouth runs ahead.");
        }

        [Test]
        public void PlayedFrames_StandsStillWhilePaused()
        {
            StartPlaying();
            _player.FillAudioBuffer(new float[256 * 2], 2);

            _player.PausePlayback(PauseReason.External);
            _player.FillAudioBuffer(new float[256 * 2], 2);

            Assert.That(_player.PlayedFrames, Is.EqualTo(256),
                "The voice is silent while paused, so PlayedFrames must not move or the mouth runs on.");
        }

        [Test]
        public void PlayedFrames_ResetsOnStartStream()
        {
            StartPlaying();
            _player.FillAudioBuffer(new float[256 * 2], 2);

            _player.StartStream(Rate);

            Assert.That(_player.PlayedFrames, Is.EqualTo(0),
                "A new utterance must count from zero, or its mouth starts where the previous voice ended.");
        }

        [Test]
        public void StopPlayback_RaisesStreamEnded_OnceWithTheReason()
        {
            StartPlaying();
            _events.Clear();

            _player.StopPlayback(wasInterrupted: true);
            _player.StopPlayback(wasInterrupted: true);

            Assert.That(_events, Is.EqualTo(new[] { "ended interrupted" }),
                "One StreamEnded per StreamStarted: a second stop must not close the utterance again.");
        }

        [Test]
        public void StopPlayback_NaturalEnd_RaisesStreamEndedNatural()
        {
            StartPlaying();
            _events.Clear();

            _player.StopPlayback(wasInterrupted: false);

            Assert.That(_events, Is.EqualTo(new[] { "ended natural" }),
                "A natural end must be reported as such, or the mouth is reset as if interrupted.");
        }

        [Test]
        public void StartStream_OverAnOpenStream_EndsTheOldUtteranceBeforeOpeningTheNew()
        {
            StartPlaying();
            _events.Clear();

            _player.StartStream(Rate);

            Assert.That(_events, Is.EqualTo(new[] { "ended interrupted", "started " + Rate }),
                "The old utterance must close before the new one opens, or the model mixes two voices.");
        }

        [Test]
        public void ResumePlaybackForLateChunks_ReopensAClosedUtterance_WithoutResettingPlayedFrames()
        {
            StartPlaying();
            _player.FillAudioBuffer(new float[256 * 2], 2);
            _player.StopPlayback(wasInterrupted: false);
            _events.Clear();

            _player.ResumePlaybackForLateChunks();

            Assert.That(_events, Is.EqualTo(new[] { "started " + Rate }),
                "Late chunks of a closed utterance must reopen it, or the model drops the rest of the sentence.");
            Assert.That(_player.PlayedFrames, Is.EqualTo(256),
                "Late chunks continue the same utterance: PlayedFrames must keep counting, not restart.");
        }

        [Test]
        public void ResumePlaybackForLateChunks_OnAnOpenUtterance_RaisesNothing()
        {
            StartPlaying();
            _events.Clear();

            _player.ResumePlaybackForLateChunks();

            Assert.That(_events, Is.Empty, "An utterance that is still open must not be opened twice.");
        }

        [Test]
        public void FillAudioBuffer_DoesNotAllocate()
        {
            StartPlaying();
            var output = new float[256 * 2];
            _player.FillAudioBuffer(output, 2); // first call outside the measurement

            Assert.That(() => _player.FillAudioBuffer(output, 2), Is.Not.AllocatingGCMemory(),
                "The audio callback allocated: garbage on the audio thread gives clicks under GC pressure.");
        }
    }
}
