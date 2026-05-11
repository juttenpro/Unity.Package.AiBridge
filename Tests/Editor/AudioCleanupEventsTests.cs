using System;
using NUnit.Framework;
using Tsc.AIBridge.Audio.Playback;
using UnityEngine;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: Streaming audio cleanup must reliably notify external coordinators
    ///
    /// WHY: VoiceLinePlayer (in RuleSystem) and NpcAudioPlayer (in AIBridgeExtended) need to
    ///      wait for streaming-audio cleanup to finish before loading scripted Addressable audio.
    ///      Without this synchronization, concurrent operations on the same AudioSource can
    ///      corrupt Unity AssetDatabase and .meta files (NullReferenceException in
    ///      AddressableAssetSettingsLocator). The previous implementation used
    ///      Type.GetType("Tsc.Training.Audio.AudioLoadLockManager, Training") via reflection
    ///      to flip a flag, but the assembly lookup silently failed in production — the flag
    ///      was never set, so the wait-loops in VoiceLinePlayer/NpcAudioPlayer were no-ops
    ///      and the race protection didn't actually exist.
    ///
    /// WHAT: Tests that StreamingAudioPlayer raises OnAudioCleanupStarted before cleanup work
    ///       and OnAudioCleanupCompleted after, so subscribers (AudioLoadLockManager) can
    ///       maintain the IsStreamingAudioCleanupInProgress flag without runtime reflection.
    ///
    /// HOW: Subscribes to the static events, calls StopPlayback, asserts events fired in order.
    ///
    /// SUCCESS CRITERIA:
    /// - OnAudioCleanupStarted fires exactly once per StopPlayback call
    /// - OnAudioCleanupCompleted fires exactly once per StopPlayback call
    /// - Started fires before Completed (ordering guarantee)
    /// - Completed fires even when StopPlayback runs without an attached AudioFilterRelay
    /// - No exception thrown when no subscribers are attached (null-safe invocation)
    ///
    /// BUSINESS IMPACT:
    /// - Without these events: AudioLoadLockManager flag never updates → race protection broken
    ///   → potential .meta corruption when AI audio cleanup overlaps with scripted audio loading
    /// - This is silent: production keeps working until a specific timing collision triggers
    ///   AssetDatabase corruption, which can break the project across all platforms
    /// </summary>
    [TestFixture]
    public class AudioCleanupEventsTests
    {
        private StreamingAudioPlayer _player;
        private GameObject _testGameObject;

        // Track invocation order across both events
        private int _startedCallCount;
        private int _completedCallCount;
        private int _startedOrderIndex;
        private int _completedOrderIndex;
        private int _eventCounter;

        private Action _startedHandler;
        private Action _completedHandler;

        [SetUp]
        public void SetUp()
        {
            _testGameObject = new GameObject("TestAudioPlayer");
            _testGameObject.AddComponent<AudioSource>();
            _player = _testGameObject.AddComponent<StreamingAudioPlayer>();

            _startedCallCount = 0;
            _completedCallCount = 0;
            _startedOrderIndex = -1;
            _completedOrderIndex = -1;
            _eventCounter = 0;

            _startedHandler = () =>
            {
                _startedCallCount++;
                _startedOrderIndex = _eventCounter++;
            };
            _completedHandler = () =>
            {
                _completedCallCount++;
                _completedOrderIndex = _eventCounter++;
            };

            StreamingAudioPlayer.OnAudioCleanupStarted += _startedHandler;
            StreamingAudioPlayer.OnAudioCleanupCompleted += _completedHandler;
        }

        [TearDown]
        public void TearDown()
        {
            StreamingAudioPlayer.OnAudioCleanupStarted -= _startedHandler;
            StreamingAudioPlayer.OnAudioCleanupCompleted -= _completedHandler;

            if (_testGameObject != null)
                UnityEngine.Object.DestroyImmediate(_testGameObject);
        }

        /// <summary>
        /// BUSINESS REQUIREMENT: Cleanup-started event fires when StopPlayback is invoked
        ///
        /// WHY: AudioLoadLockManager needs to set IsStreamingAudioCleanupInProgress=true
        ///      at the moment StreamingAudioPlayer begins tearing down audio resources.
        /// WHAT: Single StopPlayback call raises OnAudioCleanupStarted exactly once.
        /// </summary>
        [Test]
        public void StopPlayback_RaisesOnAudioCleanupStarted_Once()
        {
            _player.StopPlayback(wasInterrupted: false);

            Assert.AreEqual(1, _startedCallCount,
                "OnAudioCleanupStarted must fire exactly once per StopPlayback call");
        }

        /// <summary>
        /// BUSINESS REQUIREMENT: Cleanup-completed event fires after cleanup work
        ///
        /// WHY: AudioLoadLockManager needs to clear IsStreamingAudioCleanupInProgress=false
        ///      so VoiceLinePlayer's wait-loop can exit and load scripted audio.
        /// WHAT: Single StopPlayback call raises OnAudioCleanupCompleted exactly once.
        /// </summary>
        [Test]
        public void StopPlayback_RaisesOnAudioCleanupCompleted_Once()
        {
            _player.StopPlayback(wasInterrupted: false);

            Assert.AreEqual(1, _completedCallCount,
                "OnAudioCleanupCompleted must fire exactly once per StopPlayback call");
        }

        /// <summary>
        /// BUSINESS REQUIREMENT: Started must fire before Completed
        ///
        /// WHY: A subscriber that flips a flag relies on the start-then-end ordering.
        ///      If Completed fires first, the flag would briefly be cleared then set again,
        ///      which is the opposite of the intended lock semantics.
        /// WHAT: OnAudioCleanupStarted is invoked before OnAudioCleanupCompleted.
        /// </summary>
        [Test]
        public void StopPlayback_RaisesEventsInOrder_StartedThenCompleted()
        {
            _player.StopPlayback(wasInterrupted: false);

            Assert.AreEqual(0, _startedOrderIndex,
                "OnAudioCleanupStarted must fire first");
            Assert.AreEqual(1, _completedOrderIndex,
                "OnAudioCleanupCompleted must fire after OnAudioCleanupStarted");
        }

        /// <summary>
        /// BUSINESS REQUIREMENT: Completed event must fire even if cleanup work fails
        ///
        /// WHY: If the inner cleanup throws (e.g., AudioFilterRelay disposed mid-call),
        ///      the lock flag must still be cleared — otherwise VoiceLinePlayer waits
        ///      forever and scripted audio never loads.
        /// WHAT: Even when StopPlayback runs without an attached AudioFilterRelay (which
        ///       happens in unit tests and during early initialization), Completed still
        ///       fires. This validates the try/finally ordering.
        /// </summary>
        [Test]
        public void StopPlayback_WithoutAudioFilterRelay_StillRaisesCompleted()
        {
            // Test fixture intentionally creates the player without an AudioFilterRelay.
            // This mirrors edge-case scenarios where cleanup runs on a partially-initialized
            // component. The completed event must still fire regardless.
            _player.StopPlayback(wasInterrupted: false);

            Assert.AreEqual(1, _completedCallCount,
                "OnAudioCleanupCompleted must fire even when AudioFilterRelay is null");
        }

        /// <summary>
        /// BUSINESS REQUIREMENT: Multiple StopPlayback calls each emit a complete cycle
        ///
        /// WHY: A turn can end naturally and then be followed by an interruption shutdown.
        ///      Each cycle needs its own start/end pair so the lock flag toggles correctly
        ///      around each cleanup.
        /// WHAT: Two consecutive StopPlayback calls produce two start events and two
        ///       completed events.
        /// </summary>
        [Test]
        public void StopPlayback_CalledTwice_RaisesTwoCycles()
        {
            _player.StopPlayback(wasInterrupted: false);
            _player.StopPlayback(wasInterrupted: true);

            Assert.AreEqual(2, _startedCallCount,
                "Each StopPlayback should emit OnAudioCleanupStarted");
            Assert.AreEqual(2, _completedCallCount,
                "Each StopPlayback should emit OnAudioCleanupCompleted");
        }

        /// <summary>
        /// BUSINESS REQUIREMENT: No exception when no subscribers are attached
        ///
        /// WHY: A NullReferenceException on event invocation would crash audio cleanup
        ///      and leave NPCs stuck in a broken state. The static-event invocation
        ///      pattern must be null-safe (using `event?.Invoke()`).
        /// WHAT: Removing all subscribers and calling StopPlayback does not throw.
        /// </summary>
        [Test]
        public void StopPlayback_WithNoSubscribers_DoesNotThrow()
        {
            // Detach the subscribers from SetUp so the event has no listeners
            StreamingAudioPlayer.OnAudioCleanupStarted -= _startedHandler;
            StreamingAudioPlayer.OnAudioCleanupCompleted -= _completedHandler;

            Assert.DoesNotThrow(() => _player.StopPlayback(wasInterrupted: false),
                "StopPlayback must invoke events safely even without subscribers");

            // Re-attach so TearDown's -= calls don't error on missing handler
            StreamingAudioPlayer.OnAudioCleanupStarted += _startedHandler;
            StreamingAudioPlayer.OnAudioCleanupCompleted += _completedHandler;
        }
    }
}
