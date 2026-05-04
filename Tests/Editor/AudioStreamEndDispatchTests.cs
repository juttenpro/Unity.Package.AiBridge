using NUnit.Framework;
using Tsc.AIBridge.Audio.Playback;
using Tsc.AIBridge.Audio.Processing;
using Tsc.AIBridge.Handlers;
using Tsc.AIBridge.Messages;
using UnityEngine;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: When the orchestrator sends an AudioStreamEnd JSON message, that
    /// signal must reach the StreamingAudioPlayer so playback completes deterministically
    /// instead of relying on a buffer-drain timeout.
    ///
    /// WHY: Three layers must cooperate (AudioMessageHandler → AudioStreamProcessor →
    /// StreamingAudioPlayer). A break anywhere reverts the player to its 3s safety-net
    /// timeout, masking the regression as "slow but works" — exactly the kind of bug that
    /// hides until production.
    ///
    /// WHAT: Push an AudioStreamEnd through the public surface of AudioMessageHandler and
    /// verify the underlying StreamingAudioPlayer was marked as server-stream-ended.
    ///
    /// SUCCESS CRITERIA:
    /// - AudioMessageHandler.OnAudioStreamEnd(message) propagates to StreamingAudioPlayer
    /// - The player's IsServerStreamEnd flag becomes true after the call
    ///
    /// BUSINESS IMPACT: If the plumbing breaks, the OpusHead-parse bug returns silently — no
    /// new errors, but the heuristic timeout becomes the primary trigger again, and the parser
    /// resets mid-stream under jitter as before.
    /// </summary>
    [TestFixture]
    public class AudioStreamEndDispatchTests
    {
        private GameObject _testGameObject;
        private StreamingAudioPlayer _player;
        private AudioStreamProcessor _processor;
        private AudioMessageHandler _handler;

        [SetUp]
        public void SetUp()
        {
            _testGameObject = new GameObject("TestAudioPlayer");
            _testGameObject.AddComponent<AudioSource>();
            _player = _testGameObject.AddComponent<StreamingAudioPlayer>();
            _processor = new AudioStreamProcessor(_player, isVerboseLogging: false);
            _handler = new AudioMessageHandler("TestPersona", _processor, enableVerboseLogging: false);
        }

        [TearDown]
        public void TearDown()
        {
            _processor?.Dispose();
            if (_testGameObject != null)
            {
                UnityEngine.Object.DestroyImmediate(_testGameObject);
            }
        }

        [Test]
        public void OnAudioStreamEnd_PropagatesServerStreamEndFlag_ToPlayer()
        {
            Assert.IsFalse(_player.IsServerStreamEnd, "Precondition: player should start with the flag cleared");

            var message = new AudioStreamEndMessage
            {
                RequestId = "test-request",
                SentenceCount = 2,
                TotalChunksSent = 12,
                TotalBytesSent = 4096,
                TotalStreamsSent = 2,
                LastSentence = "How are you doing today?",
                WasCancelled = false
            };

            _handler.OnAudioStreamEnd(message);

            Assert.IsTrue(
                _player.IsServerStreamEnd,
                "AudioMessageHandler.OnAudioStreamEnd must propagate through AudioStreamProcessor to the player");
        }

        [Test]
        public void OnAudioStreamEnd_WhenCancelled_StillSetsFlag()
        {
            // The bug we're guarding against would surface as "fine on success but stuck on
            // cancel". Both paths must drive the same flag — Unity treats interruption as
            // end-of-stream too.
            var cancelledMessage = new AudioStreamEndMessage
            {
                RequestId = "test-request",
                SentenceCount = 1,
                TotalChunksSent = 3,
                TotalBytesSent = 800,
                TotalStreamsSent = 1,
                LastSentence = "Cut off mid-",
                WasCancelled = true
            };

            _handler.OnAudioStreamEnd(cancelledMessage);

            Assert.IsTrue(
                _player.IsServerStreamEnd,
                "Cancelled streams end the stream too — the player flag must be set regardless of WasCancelled");
        }
    }
}
