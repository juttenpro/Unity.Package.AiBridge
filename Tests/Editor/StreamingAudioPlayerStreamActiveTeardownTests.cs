using NUnit.Framework;
using Tsc.AIBridge.Audio.Playback;
using UnityEngine;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: After a stream is torn down, the player must report the stream as
    /// no longer active — even when the teardown happened via the safety-net deferred path.
    ///
    /// WHY: The deferred-teardown path (NpcClientBase, introduced 1.17.4) re-arms the player with
    /// ResumePlaybackForLateChunks() so late TTS chunks can still play within the defer window.
    /// That call sets _isStreamActive = true. When the defer ends via its hard timeout (no late
    /// chunks, no server AudioStreamEnd), teardown runs AudioStreamProcessor.EndAudioStream() →
    /// StreamingAudioPlayer.EndStream(). Before 1.20.1, EndStream() did NOT clear _isStreamActive,
    /// and StopPlaybackInternal (the only other place that clears it) does not run on this path —
    /// so _isStreamActive stayed true forever. Because AudioFilterRelay keeps a looping dummy clip
    /// playing (AudioSource.isPlaying == true), IsPlaybackActive (= _isStreamActive &amp;&amp;
    /// _cachedIsPlaying) then stuck true. Downstream, NpcAudioPlayer's Queue-mode scripted
    /// reactions wait on IsPlaybackActive and never played: no audio, no ReactionStarted, until the
    /// next turn's StartStream reset the state. This was the reported "scripted reaction not played"
    /// bug in placebo / verdovende prik.
    ///
    /// WHAT: EndStream() closes the stream (IsStreamActive == false), the normal-teardown case does
    /// too, and a genuine late chunk after teardown can still re-arm the stream so recovery is not
    /// regressed.
    ///
    /// HOW: Drive the public API exactly as the defer path does and assert on the IsStreamActive
    /// seam (which, unlike IsPlaybackActive, does not depend on a running audio system).
    ///
    /// SUCCESS CRITERIA:
    /// - StartStream → ResumePlaybackForLateChunks → EndStream ⇒ IsStreamActive == false
    /// - StartStream → EndStream ⇒ IsStreamActive == false
    /// - EndStream → ResumePlaybackForLateChunks ⇒ IsStreamActive == true (recovery intact)
    ///
    /// BUSINESS IMPACT: IsPlaybackActive becomes reliable again, so queued scripted reactions are
    /// always eventually played. Fixes the production regression where the trainer's scripted line
    /// was silently skipped after an AI streaming turn that ended via the safety-net defer.
    /// </summary>
    [TestFixture]
    public class StreamingAudioPlayerStreamActiveTeardownTests
    {
        private GameObject _testGameObject;
        private StreamingAudioPlayer _player;

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
        public void EndStream_AfterDeferReArm_ClearsStreamActive()
        {
            // Reproduce the defer path: stream starts, then the defer re-arms it for late chunks.
            _player.StartStream(48000);
            _player.ResumePlaybackForLateChunks();
            Assert.IsTrue(_player.IsStreamActive,
                "Precondition: ResumePlaybackForLateChunks must re-arm the stream so late chunks can play.");

            // The defer's hard timeout fires the deferred teardown → EndAudioStream → EndStream.
            _player.EndStream();

            Assert.IsFalse(_player.IsStreamActive,
                "REGRESSION FIX (1.20.1): EndStream on the deferred-teardown path must close the " +
                "stream. Leaving _isStreamActive true stuck IsPlaybackActive true (looping dummy " +
                "clip) and hung Queue-mode scripted reactions — the 'scripted reaction not played' bug.");
        }

        [Test]
        public void EndStream_AfterStartStream_ClearsStreamActive()
        {
            _player.StartStream(48000);
            Assert.IsTrue(_player.IsStreamActive, "Precondition: StartStream marks the stream active.");

            _player.EndStream();

            Assert.IsFalse(_player.IsStreamActive,
                "EndStream means the stream is complete — it must not stay reported as active.");
        }

        [Test]
        public void ResumePlaybackForLateChunks_AfterEndStream_ReArmsStream()
        {
            // Recovery contract: a genuine late chunk arriving AFTER teardown must still play.
            _player.StartStream(48000);
            _player.EndStream();
            Assert.IsFalse(_player.IsStreamActive, "Precondition: stream closed by EndStream.");

            _player.ResumePlaybackForLateChunks();

            Assert.IsTrue(_player.IsStreamActive,
                "Late-chunk recovery must not be regressed by the EndStream fix: a late chunk after " +
                "teardown re-arms the stream.");
        }
    }
}
