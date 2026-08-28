using NUnit.Framework;
using Tsc.AIBridge.Audio.Playback;
using UnityEngine;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: every OnPlaybackStarted is followed by exactly one OnPlaybackComplete or
    /// OnPlaybackInterrupted — no more, no fewer.
    ///
    /// WHY: consumers treat these events as a turn boundary, not as audio bookkeeping. The rule system
    /// maps them onto the ReactionStarted / ReactionFinished inputs that gate scoring, phase changes and
    /// the NPC's turn state, and NpcClientBase toggles IsTalking (which decides whether the push-to-talk
    /// button is treated as an interruption attempt). An unmatched Started therefore leaves the trainee
    /// facing an NPC that the system believes is still speaking: the talk button silently does nothing.
    /// Observed at the HAN test of 2026-08-26 — session 714244 sat dead for 176 seconds, and 714084 and
    /// 714206 show the same unmatched Started without the trainee noticing.
    ///
    /// WHAT: the player has four places that mutate playback state — StartPlayback, StopPlaybackInternal,
    /// ResumePlaybackForLateChunks and StartStream. Only the first two may report, and they must keep the
    /// pairing intact. A late-chunk re-arm is internal audio recovery, not a new turn. A new stream must
    /// close a turn that is still open rather than silently abandoning it.
    ///
    /// HOW: drive the player through each sequence and count the events.
    ///
    /// SUCCESS CRITERIA:
    /// - A normal turn reports exactly one Started and one Finished
    /// - A late-chunk re-arm reports neither
    /// - An already-closed turn is not closed twice
    /// - Opening a new stream closes a turn that was left open
    ///
    /// BUSINESS IMPACT: an unmatched Started blocks the trainee's microphone until the next turn happens
    /// to reset it, and leaves the rule system's IsReactionPending stuck — no scoring, no phase progress.
    /// </summary>
    [TestFixture]
    public class PlaybackReportingContractTests
    {
        private const int SampleRate = 24000;

        // 0.25s priming buffer at 24kHz is the threshold AddAudioData needs before it starts playback.
        private const int SamplesAbovePrimingThreshold = 8000;

        private GameObject _gameObject;
        private StreamingAudioPlayer _player;
        private int _started;
        private int _completed;
        private int _interrupted;

        private int Finished => _completed + _interrupted;

        [SetUp]
        public void SetUp()
        {
            StreamingAudioPlayer.SetGlobalTestMode(true);

            _gameObject = new GameObject("TestAudioPlayer");
            _gameObject.AddComponent<AudioSource>();
            _player = _gameObject.AddComponent<StreamingAudioPlayer>();

            _started = 0;
            _completed = 0;
            _interrupted = 0;
            _player.OnPlaybackStarted.AddListener(() => _started++);
            _player.OnPlaybackComplete.AddListener(() => _completed++);
            _player.OnPlaybackInterrupted.AddListener(() => _interrupted++);
        }

        [TearDown]
        public void TearDown()
        {
            if (_gameObject != null)
                Object.DestroyImmediate(_gameObject);

            StreamingAudioPlayer.SetGlobalTestMode(false);
        }

        /// <summary>
        /// Baseline: an undisturbed turn reports one Started and one Finished.
        /// </summary>
        [Test]
        public void NormalTurn_ReportsOneStartedAndOneFinished()
        {
            PlayOneTurn();

            Assert.AreEqual(1, _started, "Exactly one Started per turn.");
            Assert.AreEqual(1, Finished, "Exactly one Finished per turn.");
        }

        /// <summary>
        /// A late chunk that arrives while the turn is still open (AudioStreamProcessor's recovery path,
        /// no safety-net stop in between) re-arms the audio mechanics. That is internal recovery, not a
        /// new reaction: reporting it would leave the open Started unmatched forever.
        /// </summary>
        [Test]
        public void LateChunkReArmWhileTurnOpen_DoesNotReportASecondStarted()
        {
            _player.StartStream(SampleRate);
            _player.AddAudioData(new float[SamplesAbovePrimingThreshold]);
            Assert.AreEqual(1, _started, "Precondition: a turn is open.");

            _player.ResumePlaybackForLateChunks();
            _player.AddAudioData(new float[SamplesAbovePrimingThreshold]);

            Assert.AreEqual(1, _started,
                "The turn never ended, so the re-arm continues it.");
            Assert.AreEqual(0, Finished, "And it is still open.");
        }

        /// <summary>
        /// THE REGRESSION (Agressietraining, 24-27 Aug 2026; HAN session 714244 before it).
        ///
        /// The client safety-net stops playback while the TTS provider is still sending — a chunk-rate
        /// dip, not an ending. That stop closes the turn, NpcClientBase defers teardown and re-arms so
        /// the rest of the audio still plays, and the next chunk then opened a SECOND turn. Nothing ever
        /// closed that one: the trainee cannot speak while the system believes the NPC is talking, so no
        /// next turn arrives to close it either. The log goes quiet with IsNPCTalking stuck on true and
        /// the talk button dead — 110s in "stopt met praten", 176s in HAN 714244.
        ///
        /// A late-chunk re-arm is the tail of the turn that just played, never a new one.
        /// </summary>
        [Test]
        public void SafetyNetStopThenLateChunkReArm_ReportsOnlyTheOneTurn()
        {
            PlayOneTurn();
            Assert.AreEqual(1, _started, "Precondition: one turn played.");
            Assert.AreEqual(1, Finished, "Precondition: the safety-net closed it.");

            _player.ResumePlaybackForLateChunks();
            _player.AddAudioData(new float[SamplesAbovePrimingThreshold]);

            Assert.AreEqual(1, _started,
                "The late audio belongs to the turn that just ended. Opening a second turn here strands " +
                "it forever: it can only be closed by a next turn, which the trainee can no longer start.");
            Assert.AreEqual(1, Finished, "And no second ending either.");
        }

        /// <summary>
        /// After a genuine new stream the continuation no longer applies — the next turn must report
        /// its own start, or the NPC's next reaction is invisible to the rule system.
        /// </summary>
        [Test]
        public void NewStreamAfterALateChunkReArm_ReportsItsOwnStart()
        {
            PlayOneTurn();
            _player.ResumePlaybackForLateChunks();

            _player.StartStream(SampleRate);
            _player.AddAudioData(new float[SamplesAbovePrimingThreshold]);

            Assert.AreEqual(2, _started, "A real new stream is a new turn.");
        }

        /// <summary>
        /// The race seen in 714084 and 714206: the trainee starts the next turn before the player's
        /// auto-complete heuristic gets around to closing the previous one. StartStream resets the
        /// playback flags, so without this the previous Started is abandoned and never matched.
        /// </summary>
        [Test]
        public void NewStreamWhileTurnStillOpen_ClosesThePreviousTurn()
        {
            _player.StartStream(SampleRate);
            _player.AddAudioData(new float[SamplesAbovePrimingThreshold]);
            Assert.AreEqual(1, _started, "Precondition: a turn is open.");
            Assert.AreEqual(0, Finished, "Precondition: it was never closed.");

            _player.StartStream(SampleRate);

            Assert.AreEqual(1, Finished,
                "Opening a new stream abandons the open turn. It must be closed first, or the rule " +
                "system keeps waiting for a ReactionFinished that can never arrive.");
        }

        /// <summary>
        /// The same abandonment reached the other way: EndStream clears the stream flag without ending
        /// playback, so StartStream's "was a stream active?" branch does not run and the open turn would
        /// slip through. The next turn must still report its own start.
        /// </summary>
        [Test]
        public void NewStreamAfterEndStreamWithTurnStillOpen_ClosesItAndReportsTheNextTurn()
        {
            _player.StartStream(SampleRate);
            _player.AddAudioData(new float[SamplesAbovePrimingThreshold]);
            _player.EndStream();
            Assert.AreEqual(0, Finished, "Precondition: EndStream does not end playback.");

            _player.StartStream(SampleRate);
            Assert.AreEqual(1, Finished, "The abandoned turn must be closed.");

            _player.AddAudioData(new float[SamplesAbovePrimingThreshold]);

            Assert.AreEqual(2, _started,
                "The new turn reports its own start — suppressing it would silence the next reaction.");
            Assert.AreEqual(1, Finished, "And it is still open.");
        }

        /// <summary>
        /// An interruption is a legitimate turn ending and must still report exactly once.
        /// </summary>
        [Test]
        public void Interruption_ClosesTheTurnExactlyOnce()
        {
            _player.StartStream(SampleRate);
            _player.AddAudioData(new float[SamplesAbovePrimingThreshold]);

            _player.StopPlayback(wasInterrupted: true);
            _player.StopPlayback(wasInterrupted: true);

            Assert.AreEqual(1, _interrupted, "One interruption ends the turn once.");
            Assert.AreEqual(0, _completed, "An interrupted turn must not also report a natural end.");
        }

        /// <summary>
        /// Stopping a player that never started must stay silent — otherwise teardown of an idle NPC
        /// reports a reaction that never happened.
        /// </summary>
        [Test]
        public void StopWithoutAnyPlayback_ReportsNothing()
        {
            _player.StartStream(SampleRate);

            _player.StopPlayback(wasInterrupted: false);

            Assert.AreEqual(0, _started);
            Assert.AreEqual(0, Finished, "No audio ever played, so there is no turn to close.");
        }

        private void PlayOneTurn()
        {
            _player.StartStream(SampleRate);
            _player.AddAudioData(new float[SamplesAbovePrimingThreshold]);
            _player.StopPlayback(wasInterrupted: false);
        }
    }
}
