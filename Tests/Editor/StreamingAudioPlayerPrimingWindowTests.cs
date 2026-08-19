using NUnit.Framework;
using Tsc.AIBridge.Audio.Playback;
using UnityEngine;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: Callers that ask "is a streaming turn still open?" in order to decide
    /// whether scripted audio may take the AudioSource must not be told "no" while a stream is
    /// filling its priming buffer.
    ///
    /// WHY: A stream becomes open at StartStream(), but playback only starts once PRIMING_BUFFER
    /// worth of samples has arrived (~250ms). During that window AudioSource.isPlaying is false,
    /// so IsPlaybackActive (= _isStreamActive &amp;&amp; _cachedIsPlaying) reads FALSE even though the
    /// stream is very much alive. NpcAudioPlayer used to gate both its Queue-mode wait and its
    /// Replace-mode "stop the stream" on IsPlaybackActive. A scripted reaction that slipped through
    /// that window assigned its own AudioClip to the shared AudioSource on top of the incoming
    /// stream. AudioFilterRelay.OnAudioFilterRead then sees a non-dummy clip while streaming is
    /// active, drops into its unity-gain spatial fallback (spatial weights forced to 1.0f instead of
    /// the real sub-1.0 distance/pan attenuation) and plays the whole utterance unattenuated —
    /// reported from the field (Leefstijlgesprekken intro, VR and Mobile) as "the coach suddenly
    /// answers at double volume, the next sentence is normal again", with the scripted line itself
    /// inaudible.
    ///
    /// WHAT: The two flags are NOT interchangeable. IsStreamActive must report an open stream from
    /// StartStream onwards, including while the priming buffer fills and IsPlaybackActive is false.
    ///
    /// HOW: Drive the public API the way the pipeline does (StartStream, then a sub-threshold
    /// AddAudioData) and assert on both flags. Deliberately asserts the DISCREPANCY rather than a
    /// single value: the defect was choosing the wrong one of the two, so the regression guard is
    /// that they genuinely differ in this window.
    ///
    /// SUCCESS CRITERIA:
    /// - StartStream ⇒ IsStreamActive == true
    /// - In the same state, with no running audio system, IsPlaybackActive == false
    /// - Buffered-but-not-yet-playing audio does not flip IsStreamActive off
    ///
    /// BUSINESS IMPACT: Keeps the scripted-vs-streaming arbitration honest during the first 250ms
    /// of every AI turn. Without it, any reaction queued at the moment the backend starts speaking
    /// can be silently swallowed and the AI reply played at roughly double volume.
    /// </summary>
    [TestFixture]
    public class StreamingAudioPlayerPrimingWindowTests
    {
        private GameObject _testGameObject;
        private StreamingAudioPlayer _player;

        [SetUp]
        public void SetUp()
        {
            _testGameObject = new GameObject("TestPrimingWindowPlayer");
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
        public void StartStream_BeforeAnyAudioArrives_ReportsStreamActiveButNotPlaybackActive()
        {
            _player.StartStream(48000);

            Assert.IsTrue(_player.IsStreamActive,
                "StartStream opens the stream — IsStreamActive is the flag callers must gate on.");
            Assert.IsFalse(_player.IsPlaybackActive,
                "IsPlaybackActive also requires AudioSource.isPlaying, which is false until the " +
                "priming buffer has filled. Gating scripted playback on it lets a reaction grab " +
                "the AudioSource on top of a live stream.");
        }

        [Test]
        public void StartStream_WithSubThresholdAudioBuffered_StillReportsStreamActive()
        {
            _player.StartStream(48000);

            // ~20ms at 48kHz: real TTS chunk size, far below the 250ms priming threshold, so
            // playback has not started yet. This is the exact window the field bug slipped through.
            _player.AddAudioData(new float[960]);

            Assert.IsTrue(_player.HasBufferedAudio,
                "Precondition: the sub-threshold chunk is buffered, so the stream is genuinely alive.");
            Assert.IsTrue(_player.IsStreamActive,
                "Audio in flight must keep the stream reported as open.");
            Assert.IsFalse(_player.IsPlaybackActive,
                "REGRESSION GUARD: this is the window in which the two flags disagree. Queue-mode " +
                "and Replace-mode arbitration in NpcAudioPlayer must use IsStreamActive; using " +
                "IsPlaybackActive here produced the 'one utterance at double volume' field report.");
        }

        [Test]
        public void EndStream_AfterPrimingWindow_ClosesStreamSoQueuedScriptedAudioProceeds()
        {
            _player.StartStream(48000);
            _player.AddAudioData(new float[960]);
            Assert.IsTrue(_player.IsStreamActive, "Precondition: stream open.");

            _player.EndStream();

            Assert.IsFalse(_player.IsStreamActive,
                "Gating on IsStreamActive must not become a new deadlock: once the turn ends, " +
                "queued scripted reactions have to be released.");
        }
    }
}
