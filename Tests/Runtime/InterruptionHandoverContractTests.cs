using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using Tsc.AIBridge.Audio.Capture;
using Tsc.AIBridge.Audio.Interruption;
using Tsc.AIBridge.Core;
using Tsc.AIBridge.Input;
using UnityEngine;
using UnityEngine.TestTools;

namespace Tsc.AIBridge.Tests.Runtime
{
    /// <summary>
    /// BUSINESS REQUIREMENT: a learner who talks over a speaking NPC must be able to cut it off.
    ///
    /// WHY: AIBridgeRulesHandler withholds PlayerStartsTalking the moment it sees the addressed NPC
    /// speaking, on the promise that the overlap monitor either approves the interruption or the NPC
    /// falls silent. If the monitor never watches, that promise is broken in the one direction nobody
    /// notices from a unit test: the turn is not lost with an error, it is merely delayed until the NPC
    /// has finished — which is exactly "I cannot interrupt him".
    ///
    /// WHAT: that the InterruptionManager actually approves an interruption when a press overlaps an
    /// NPC that is speaking, for both orders in which the press and the addressed NPC arrive.
    ///
    /// HOW: real SpeechInputHandler, real VAD (fixed threshold, fed real loud frames through the
    /// IAudioCaptureProvider seam), real InterruptionManager, real coroutine, real frames. The only
    /// things faked are the microphone and the NPC's audio player.
    ///
    /// SUCCESS CRITERIA:
    /// - overlap lasting longer than the persona's persistence time fires OnInterruption
    /// - and the NPC that was talked over is the one whose audio stops
    ///
    /// BUSINESS IMPACT: the de-escalation training scores "did the learner interrupt" on this path.
    /// Without it every learner scores identically, whether they waited or shouted over the NPC.
    ///
    /// NOT COVERED: real microphone input, real streamed NPC audio and the adaptive VAD. Those need a
    /// play session — green here does not prove interruption works in the app.
    /// </summary>
    public class InterruptionHandoverContractTests
    {
        private const float PersistenceTime = 0.4f;

        private GameObject _player;
        private SpeechInputHandler _speech;
        private InterruptionManager _manager;
        private TestCapture _capture;

        private GameObject _speakingNpcObject;
        private TestNpc _speakingNpc;
        private GameObject _silentNpcObject;
        private TestNpc _silentNpc;

        [SetUp]
        public void SetUp()
        {
            LogAssert.ignoreFailingMessages = true;

            _player = new GameObject("Player");
            _player.SetActive(false);

            _capture = _player.AddComponent<TestCapture>();
            _speech = _player.AddComponent<SpeechInputHandler>();
            _manager = _player.AddComponent<InterruptionManager>();

            // A fixed VAD threshold makes "is the user speaking" a decision about the samples this test
            // feeds, not about a calibration history it cannot control.
            SetPrivate(_speech, "useAdaptiveVAD", false);
            SetPrivate(_speech, "fixedVadThreshold", 0.02f);
            SetPrivate(_manager, "speechInputHandler", _speech);

            _speakingNpcObject = new GameObject("Lucius");
            _speakingNpcObject.SetActive(false);
            _speakingNpc = _speakingNpcObject.AddComponent<TestNpc>();
            _speakingNpc.Name = "Lucius";
            _speakingNpc.IsTalking = true;

            _silentNpcObject = new GameObject("Sanne");
            _silentNpcObject.SetActive(false);
            _silentNpc = _silentNpcObject.AddComponent<TestNpc>();
            _silentNpc.Name = "Sanne";
            _silentNpc.IsTalking = false;

            _player.SetActive(true);
        }

        [TearDown]
        public void TearDown()
        {
            if (_player != null) UnityEngine.Object.DestroyImmediate(_player);
            if (_speakingNpcObject != null) UnityEngine.Object.DestroyImmediate(_speakingNpcObject);
            if (_silentNpcObject != null) UnityEngine.Object.DestroyImmediate(_silentNpcObject);
            LogAssert.ignoreFailingMessages = false;
        }

        /// <summary>
        /// The control: the manager already knows which NPC is being talked over when the press arrives.
        /// If this one fails, the overlap monitor itself is broken and the ordering test below says
        /// nothing.
        /// </summary>
        [UnityTest]
        public IEnumerator APressOverAKnownSpeakingNpc_IsApprovedWithinItsPersistenceTime()
        {
            var approved = false;
            _manager.OnInterruption += () => approved = true;

            _manager.SetAddressedNpc(_speakingNpc, new InterruptionTarget(true, PersistenceTime));

            yield return Press();
            yield return TalkOverTheNpcFor(PersistenceTime * 3f);

            Assert.IsTrue(approved,
                "The learner talked over a speaking NPC for three times its persistence time and the " +
                "interruption was never approved - in the app the NPC talks on and the learner's turn " +
                "only lands once it has finished.");
            Assert.IsFalse(_speakingNpc.IsTalking, "The NPC that was talked over should have been silenced.");
        }

        /// <summary>
        /// Production order. AIBridgeRulesHandler pushes the addressed NPC from its OWN handler for
        /// SpeechInputHandler.OnRecordingStarted, so whether it arrives before or after the manager's
        /// handler is decided by Unity's script order - which neither class pins. Until that push lands
        /// the manager still holds the NPC from the previous press, and in a room with two speakers that
        /// one is silent.
        /// </summary>
        [UnityTest]
        public IEnumerator APressWhoseAddressedNpcArrivesJustAfterIt_IsApprovedToo()
        {
            var approved = false;
            _manager.OnInterruption += () => approved = true;

            // The previous press addressed Sanne; she has long since stopped talking.
            _manager.SetAddressedNpc(_silentNpc, new InterruptionTarget(true, PersistenceTime));

            yield return Press();

            // ... and only now does the rules handler get round to naming the NPC actually being
            // talked over.
            _manager.SetAddressedNpc(_speakingNpc, new InterruptionTarget(true, PersistenceTime));

            yield return TalkOverTheNpcFor(PersistenceTime * 3f);

            Assert.IsTrue(approved,
                "The addressed NPC arrived one call after the press and the interruption was never " +
                "approved - the learner talks over a speaking NPC and nothing stops it.");
            Assert.IsFalse(_speakingNpc.IsTalking, "The NPC that was talked over should have been silenced.");
        }

        /// <summary>
        /// The window AIBridgeRulesHandler calls "audibly talking" is wider than the one the monitor
        /// calls "talking": the handler also closes its gate on IsReceivingResponse, which is true while
        /// a turn streams in before its first audio frame plays. A press in that window is handed to the
        /// monitor while NpcClientBase.IsTalking is still false. Nothing revisits that decision once the
        /// audio does start, so this failure needs no particular script order to happen.
        /// </summary>
        [UnityTest]
        public IEnumerator APressJustBeforeTheNpcsAudioStarts_IsApprovedOnceItDoes()
        {
            var approved = false;
            _manager.OnInterruption += () => approved = true;

            // The turn is streaming in; no audio has reached the speakers yet.
            _speakingNpc.IsTalking = false;
            _manager.SetAddressedNpc(_speakingNpc, new InterruptionTarget(true, PersistenceTime));

            yield return Press();

            // The first chunk plays a moment later and the learner is still talking over it.
            _speakingNpc.IsTalking = true;

            yield return TalkOverTheNpcFor(PersistenceTime * 3f);

            Assert.IsTrue(approved,
                "The press landed in the gap between the turn arriving and its audio starting, and the " +
                "interruption was never approved - the NPC speaks its whole answer over the learner.");
            Assert.IsFalse(_speakingNpc.IsTalking, "The NPC that was talked over should have been silenced.");
        }

        /// <summary>
        /// The talk button goes down: recording is live and the handler announces it, exactly as
        /// SpeechInputHandler.StartRecording does, without dragging the Opus encoder into the test.
        /// </summary>
        private IEnumerator Press()
        {
            SetPrivate(_speech, "_isPttPressed", true);
            SetPrivate(_speech, "_pttPressTime", Time.time);
            SetPrivate(_speech, "_isRecording", true);
            RaiseRecordingStarted();
            yield return null;
        }

        /// <summary>
        /// Feeds the real VAD real frames at roughly the microphone's rate for the given time, so the
        /// overlap the monitor measures is the overlap of actual speech.
        /// </summary>
        private IEnumerator TalkOverTheNpcFor(float seconds)
        {
            var loud = new float[512];
            for (var i = 0; i < loud.Length; i++)
                loud[i] = Mathf.Sin(i * 0.3f) * 0.35f;

            var until = Time.time + seconds;
            while (Time.time < until)
            {
                _capture.Emit(loud);
                yield return null;
            }
        }

        private void RaiseRecordingStarted()
        {
            var field = typeof(SpeechInputHandler)
                .GetField("OnRecordingStarted", BindingFlags.NonPublic | BindingFlags.Instance);
            var handler = field?.GetValue(_speech) as Action;
            handler?.Invoke();
        }

        private static void SetPrivate(object target, string field, object value)
        {
            var info = target.GetType()
                .GetField(field, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(info, $"Field '{field}' no longer exists on {target.GetType().Name}");
            info.SetValue(target, value);
        }

        /// <summary>A microphone the test drives, through the seam SpeechInputHandler already resolves.</summary>
        private class TestCapture : MonoBehaviour, IAudioCaptureProvider
        {
            public event Action OnCaptureStarted;
            public event Action OnCaptureStopped;
            public event Action<float[]> OnAudioDataAvailable;
            public event Action<string> OnError;

            public bool IsCapturing => true;
            public int SampleRate => 16000;
            public int Channels => 1;
            public float CurrentVolume { get; private set; }
            public string SelectedDevice => "test";

            public void Emit(float[] samples)
            {
                CurrentVolume = 0.35f;
                OnAudioDataAvailable?.Invoke(samples);
            }

            public void StartCapture() => OnCaptureStarted?.Invoke();
            public void StopCapture() => OnCaptureStopped?.Invoke();
            public bool SelectDevice(string deviceName) => true;
            public string[] GetAvailableDevices() => new[] { "test" };

            private void ReportError(string message) => OnError?.Invoke(message);
        }

        /// <summary>
        /// An NPC with no StreamingAudioPlayer, which is the documented fallback: the monitor then reads
        /// IsTalking as "producing audible speech".
        /// </summary>
        private class TestNpc : NpcClientBase
        {
            public string Name = "Test";
            public override string NpcName => Name;
            protected override void ValidateConfiguration() { }
        }
    }
}
