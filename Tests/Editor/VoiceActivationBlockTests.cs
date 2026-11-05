using System;
using System.Collections.Generic;
using NUnit.Framework;
using Tsc.AIBridge.Audio.Capture;
using Tsc.AIBridge.Audio.VAD;
using Tsc.AIBridge.Input;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: on an open microphone the NPC's voice through the speaker never starts a recording, and the
    /// user's voice right after the NPC stops starts one at once.
    ///
    /// WHY: Assistant Platform on a OnePlus 9, 2026-11-05. ShouldBlockRecording was only checked for push-to-talk, so voice
    /// activation started recording on the NPC's echo. That recording stayed open into the user's turn: in 9 of 10 turns a
    /// recording that began on the NPC's voice ended 0.3-1.0 s after the turn began, and a quick answer inside it was
    /// swallowed (no new start until 0.5 s of silence).
    /// WHAT: a SpeechInputHandler with the noise-floor VAD, driven through a stub capture in 20 ms frames, in the order of a
    /// turn: blocked with echo, then unblocked with ResetVoiceActivation while the user already speaks.
    /// NOT COVERED: the real speaker latency (the app's echo guard covers that), push-to-talk, the encoder output.
    /// </summary>
    [TestFixture]
    public class VoiceActivationBlockTests
    {
        GameObject go;
        StubCapture capture;
        SpeechInputHandler handler;
        Func<bool> previousHook;
        bool blocked;
        double clock;
        readonly List<double> starts = new();

        [SetUp]
        public void SetUp()
        {
            // NUnit reuses one fixture instance for all tests: every field starts over here.
            blocked = false;
            clock = 0;
            starts.Clear();
            previousHook = SpeechInputHandler.ShouldBlockRecording;
            SpeechInputHandler.ShouldBlockRecording = () => blocked;
            go = new GameObject("VoiceActivationBlockTests");
            capture = go.AddComponent<StubCapture>();
            handler = go.AddComponent<SpeechInputHandler>();
            var serialized = new SerializedObject(handler);
            serialized.FindProperty("useVoiceActivation").boolValue = true;
            serialized.FindProperty("useNoiseFloorVAD").boolValue = true;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            handler.InitializeForTesting();
            handler.OnRecordingStarted += () => starts.Add(clock);
            // A quiet room before the NPC speaks: the noise-floor VAD measures the room over 5 s.
            Feed(0.0003f, 5.0);
        }

        /// <summary>The NPC speaks: recording blocked, and (as the app does) her voice kept out of the room noise.</summary>
        void NpcSpeaks(double seconds)
        {
            blocked = true;
            handler.SetNpcSpeaking(true);
            Feed(0.03f, seconds);
        }

        /// <summary>The user's turn: unblocked, the VAD learns again, voice activation starts over.</summary>
        double UsersTurn()
        {
            blocked = false;
            handler.SetNpcSpeaking(false);
            handler.ResetVoiceActivation();
            return clock;
        }

        [TearDown]
        public void TearDown()
        {
            SpeechInputHandler.ShouldBlockRecording = previousHook;
            Object.DestroyImmediate(go);
        }

        void Feed(float amplitude, double seconds)
        {
            var frame = new float[320];
            for (var i = 0; i < frame.Length; i++) frame[i] = i % 2 == 0 ? amplitude : -amplitude;
            for (var t = 0.0; t < seconds - 1e-9; t += 0.02)
            {
                capture.Push(frame);
                clock += 0.02;
            }
        }

        [Test]
        public void NpcEchoWhileBlocked_StartsNoRecording()
        {
            NpcSpeaks(1.0);

            Assert.That(starts, Is.Empty, "The NPC's voice through the speaker started a recording while recording was blocked.");
            Assert.That(handler.IsRecording, Is.False);
        }

        [Test]
        public void UserSpeaksRightAfterTheNpc_StartsWithin300Ms()
        {
            NpcSpeaks(1.0);

            var turnBegins = UsersTurn();
            Feed(0.012f, 1.0);

            var inTurn = starts.FindAll(t => t >= turnBegins);
            Assert.That(inTurn, Has.Count.EqualTo(1),
                "No new recording start in the user's turn: the recording that began on the NPC's voice swallowed the answer. Starts: " + string.Join(", ", starts));
            Assert.That(inTurn[0] - turnBegins, Is.LessThanOrEqualTo(0.3),
                "The user's answer right after the NPC did not start a recording in time: the start of the answer is swallowed.");
        }

        [Test]
        public void Reset_EndsARecordingHeldByVoiceActivation()
        {
            Feed(0.03f, 0.5);
            Assert.That(handler.IsRecording, Is.True, "Precondition: voice activation started recording.");

            handler.ResetVoiceActivation();

            Assert.That(handler.IsRecording, Is.False, "A recording held by voice activation survived the reset.");
        }

        [Test]
        public void FrameMeasured_ReportsTheSmoothedLevelAndTheThreshold()
        {
            var processor = (NoiseFloorVADProcessor)handler.VadManager.Processor;
            float smoothed = 0, threshold = 0, seconds = 0;
            processor.FrameMeasured += (s, t, d) => (smoothed, threshold, seconds) = (s, t, d);

            Feed(0.01f, 0.1);
            Feed(0.0f, 0.02);

            Assert.That(smoothed, Is.EqualTo(0.008f).Within(1e-5f), "Not the mean of the last 5 frames: (4 x 0.01 + 0) / 5.");
            Assert.That(threshold, Is.EqualTo(NoiseFloorVADProcessor.DefaultMinimumThreshold).Within(1e-6f));
            Assert.That(seconds, Is.EqualTo(0.02f).Within(1e-6f));
        }

#pragma warning disable 67
        private sealed class StubCapture : MonoBehaviour, IAudioCaptureProvider
        {
            public event Action OnCaptureStarted;
            public event Action OnCaptureStopped;
            public event Action<float[]> OnAudioDataAvailable;
            public event Action<string> OnError;
            public bool IsCapturing => true;
            public int SampleRate => 16000;
            public int Channels => 1;
            public float CurrentVolume => 0f;
            public string SelectedDevice => "Stub device";
            public void StartCapture() { }
            public void StopCapture() { }
            public bool SelectDevice(string deviceName) => true;
            public string[] GetAvailableDevices() => new[] { "Stub device" };
            public void Push(float[] frame) => OnAudioDataAvailable?.Invoke(frame);
        }
#pragma warning restore 67
    }
}
