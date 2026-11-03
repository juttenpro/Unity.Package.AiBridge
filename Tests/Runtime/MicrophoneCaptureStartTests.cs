using System.Collections;
using NUnit.Framework;
using Tsc.AIBridge.Audio.Capture;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Tsc.AIBridge.Tests.Runtime
{
    /// <summary>
    /// BUSINESS REQUIREMENT: one start of the microphone gives one recording, and a stop always leaves it off.
    ///
    /// WHY: Assistant Platform on a Pixel 9 Pro XL, 2026-11-03: the patient talked and the app heard nothing. The app
    ///      enabled the capture and called StartCapture; the capture's own Start() called it again a few frames later.
    ///      IsCapturing only turns true once the first samples arrive, and on the phone the microphone takes ~100 ms
    ///      to wake, so the second call passed. Android's recording log shows the first recording stopped 20 ms after
    ///      it began and a new one starting; in another run the second Microphone.Start failed with error 80. In the
    ///      Editor the microphone is ready before the second call, which is why it never showed there.
    /// WHAT: StartCapture while a start is warming up does nothing; StopCapture during a start cancels it.
    /// HOW:  The real MicrophoneCapture with the Editor's microphone. Two calls in one frame stand in for the slow
    ///       phone, so the test does not depend on how fast this machine's microphone wakes. The component is disabled
    ///       before its Start() runs, so only the test calls StartCapture.
    /// NOT COVERED: Android's native recorder; whether the phone's audio then reaches the VAD.
    /// </summary>
    public class MicrophoneCaptureStartTests
    {
        const float WaitForCaptureSec = 4f;

        GameObject _gameObject;
        MicrophoneCapture _capture;
        int _captureStarts;

        [SetUp]
        public void SetUp()
        {
            if (Microphone.devices.Length == 0)
                Assert.Ignore("This machine has no microphone.");

            _gameObject = new GameObject("MicrophoneCaptureStartTests");
            _capture = _gameObject.AddComponent<MicrophoneCapture>();
            _capture.enabled = false;
            _captureStarts = 0;
            _capture.OnCaptureStarted += () => _captureStarts++;
        }

        [TearDown]
        public void TearDown()
        {
            if (_gameObject != null)
                Object.Destroy(_gameObject);
        }

        [UnityTest]
        public IEnumerator StartCapture_TwiceBeforeTheMicrophoneIsReady_StartsItOnce()
        {
            _capture.StartCapture();
            _capture.StartCapture();

            yield return WaitUntilCapturingOrTimeout();
            // Room for a second start to report itself, as it did on the phone.
            yield return new WaitForSecondsRealtime(1f);

            Assert.IsTrue(_capture.IsCapturing, "The microphone did not start at all.");
            Assert.AreEqual(1, _captureStarts,
                "The microphone was started twice: on Android the second start tears down the first recording, and the app hears silence.");
        }

        [UnityTest]
        public IEnumerator StopCapture_WhileStarting_LeavesTheMicrophoneOff()
        {
            _capture.StartCapture();
            _capture.StopCapture();

            yield return new WaitForSecondsRealtime(WaitForCaptureSec);

            Assert.IsFalse(_capture.IsCapturing,
                "A start that was still warming up switched the microphone on after the stop: it runs outside the conversation.");
            Assert.AreEqual(0, _captureStarts);
            Assert.IsFalse(Microphone.IsRecording(_capture.SelectedDevice), "The device still records after the stop.");
        }

        IEnumerator WaitUntilCapturingOrTimeout()
        {
            var end = Time.realtimeSinceStartup + WaitForCaptureSec;
            while (!_capture.IsCapturing && Time.realtimeSinceStartup < end)
                yield return null;
        }
    }
}
