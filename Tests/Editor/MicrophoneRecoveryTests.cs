using System;
using NUnit.Framework;
using Tsc.AIBridge.Audio.Capture;
using Tsc.AIBridge.Input;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: a talk press must never report a live recording while the microphone
    /// is not actually capturing.
    ///
    /// WHY: Radboud, 2026-09-03 — on two of three headsets the NPCs stopped answering and nobody
    ///      could tell why. The capture had given up: on Android the device list is empty until
    ///      RECORD_AUDIO is granted, StartCapture() raised "No microphone available" once, and the
    ///      host's ErrorHandler has that exact text on its ignore list (no popup, no Oops report).
    ///      Nothing restarted it afterwards — the resume path was gated on the same device check
    ///      that had just failed. Meanwhile StartRecording() only set a flag and started the
    ///      encoder, so the talk indicator lit up, the RuleSystem logged PlayerStartsTalking, and
    ///      zero bytes reached the backend. The backend answered with an empty transcript and the
    ///      lesson concluded the player had said nothing. Confirmed in session log
    ///      Instrumental_04-09-2026_14-04-31: five consecutive turns, every one empty within 0.1s.
    /// WHAT: StartRecording asks the capture provider to start when it is not capturing, and
    ///       leaves a healthy capture untouched (no restart, which would drop the mic mid-turn).
    /// HOW:  A stub IAudioCaptureProvider on the same GameObject is picked up by Awake instead of
    ///       the real MicrophoneCapture, so the decision is verified without a physical device.
    /// </summary>
    public class MicrophoneRecoveryTests
    {
        private GameObject _gameObject;

        [TearDown]
        public void TearDown()
        {
            if (_gameObject != null)
                Object.DestroyImmediate(_gameObject);
        }

        [Test]
        public void StartRecording_WithStoppedCapture_RestartsTheMicrophone()
        {
            var capture = CreateHandlerWith(isCapturing: false, out var handler);

            LogAssert.Expect(LogType.Warning, MicrophoneRestartWarning);
            handler.StartRecording();

            Assert.AreEqual(1, capture.StartCaptureCalls,
                "A talk press on a stopped microphone must restart it — otherwise the turn is sent silent.");
        }

        [Test]
        public void StartRecording_WithRunningCapture_LeavesTheMicrophoneAlone()
        {
            var capture = CreateHandlerWith(isCapturing: true, out var handler);

            handler.StartRecording();

            Assert.AreEqual(0, capture.StartCaptureCalls,
                "Restarting a healthy microphone would drop the first moment of the turn.");
        }

        /// <summary>
        /// A second press within the same turn must not stack restarts: StartRecording returns
        /// early while already recording, so the microphone is asked once and only once.
        /// </summary>
        [Test]
        public void StartRecording_PressedTwice_RestartsOnlyOnce()
        {
            var capture = CreateHandlerWith(isCapturing: false, out var handler);

            LogAssert.Expect(LogType.Warning, MicrophoneRestartWarning);
            handler.StartRecording();
            handler.StartRecording();

            Assert.AreEqual(1, capture.StartCaptureCalls);
        }

        private const string MicrophoneRestartWarning =
            "[SpeechInputHandler] Microphone was not capturing at talk start - restarting it. " +
            "The first moment of this turn may be missing.";

        private StubAudioCapture CreateHandlerWith(bool isCapturing, out SpeechInputHandler handler)
        {
            _gameObject = new GameObject("SpeechInput");

            // Added before the handler so its Awake resolves this provider instead of creating a
            // real MicrophoneCapture, which would reach for the editor's physical device.
            var capture = _gameObject.AddComponent<StubAudioCapture>();
            capture.IsCapturing = isCapturing;

            handler = _gameObject.AddComponent<SpeechInputHandler>();

            // AddComponent does not run Awake outside play mode, so wire the handler up explicitly.
            handler.InitializeForTesting();
            return capture;
        }

        /// <summary>
        /// Records what the handler asks of the capture provider. The provider's events exist for
        /// the handler to subscribe to in Start() — which does not run in an EditMode test — so
        /// this stub never raises them; that is what the pragma silences.
        /// </summary>
#pragma warning disable 67
        private sealed class StubAudioCapture : MonoBehaviour, IAudioCaptureProvider
        {
            public event Action OnCaptureStarted;
            public event Action OnCaptureStopped;
            public event Action<float[]> OnAudioDataAvailable;
            public event Action<string> OnError;

            public bool IsCapturing { get; set; }
            public int SampleRate => 16000;
            public int Channels => 1;
            public float CurrentVolume => 0f;
            public string SelectedDevice => "Stub device";

            public int StartCaptureCalls { get; private set; }

            public void StartCapture()
            {
                StartCaptureCalls++;
                IsCapturing = true;
            }

            public void StopCapture() => IsCapturing = false;

            public bool SelectDevice(string deviceName) => true;

            public string[] GetAvailableDevices() => new[] { "Stub device" };
        }
#pragma warning restore 67
    }
}
