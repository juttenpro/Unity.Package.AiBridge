using System;
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
    /// BUSINESS REQUIREMENT: on an open-mic phone a normal voice is heard, also right after the NPC spoke, and silence or
    /// a TV alone starts nothing.
    ///
    /// WHY: Assistant Platform on a Pixel, 2026-11-04, "I have to talk far too loudly". Measured (the [Pipeline] log):
    ///      room RMS 0.0003-0.0005, the NPC's echo tail after a listen starts 0.03-0.10, a normal voice 0.010-0.017. The
    ///      DynamicRange VAD stayed at 0.013-0.014 and rose to ~0.02 while the patient spoke, so his normal voice was missed.
    /// WHAT: the noise-floor VAD on those levels: a normal voice after the tail is heard, the tail does not lift the
    ///       threshold, silence starts nothing, a voice over a TV is heard and the TV alone is not, the NPC's own voice
    ///       stays out of the noise floor, a steady noise does lift it.
    /// HOW:  20 ms frames of a constant amplitude (RMS = amplitude) through a VADManager. CreateProcessor is the only
    ///       place the VAD is chosen: pointed at DynamicRange with the Assistant Platform's former settings, the first
    ///       four tests fail on the same numbers (the red proof of 5.21.0).
    /// NOT COVERED: a real TV (music has fewer pauses than this speech-like model), the distance to the phone, other
    ///       phones' microphone gain, real echo.
    /// </summary>
    public class NoiseFloorVadTests
    {
        const float Silence = 0.0005f;
        const float QuietRoom = 0.0003f;
        const float SpeechPause = 0.006f;
        // A TV in the room, speech-like: louder than the room's minimum threshold, with quieter gaps.
        const float TvVoiced = 0.008f;
        const float TvPause = 0.0032f;

        static VADProcessorBase CreateProcessor() => new NoiseFloorVADProcessor("Test");

        static float[] Frame(float amplitude)
        {
            var samples = new float[320];
            for (var i = 0; i < samples.Length; i++) samples[i] = amplitude;
            return samples;
        }

        static bool Feed(VADManager vad, float amplitude, double seconds)
        {
            var started = false;
            var frame = Frame(amplitude);
            for (var i = 0; i < seconds / 0.02; i++) started |= vad.ProcessAudioFrame(frame, 0f);
            return started;
        }

        /// <summary>Voiced 160 ms, pause 80 ms: speech has gaps, and those gaps must not become the noise floor.</summary>
        static bool Speech(VADManager vad, float voiced, float pause, double seconds)
        {
            var started = false;
            var loud = Frame(voiced);
            var quiet = Frame(pause);
            for (var i = 0; i < seconds / 0.02; i++) started |= vad.ProcessAudioFrame(i % 12 < 8 ? loud : quiet, 0f);
            return started;
        }

        /// <summary>The order of run 4 (11:01-11:03): a loud answer, the NPC's reply, then the listen of turn 5 with her tail.</summary>
        static VADManager AfterTheNpcReplied()
        {
            var vad = new VADManager(CreateProcessor());
            vad.SetNpcSpeaking(false);
            Feed(vad, 0.06f, 1.0);
            Feed(vad, Silence, 1.0);
            Speech(vad, 0.046f, 0.008f, 1.0);
            Feed(vad, Silence, 2.0);
            vad.SetNpcSpeaking(true);
            Speech(vad, 0.07f, 0.02f, 10.0);
            vad.SetNpcSpeaking(false);
            Feed(vad, 0.0345f, 0.6);
            Feed(vad, QuietRoom, 9.0);
            return vad;
        }

        [TestCase(0.013f)]
        [TestCase(0.010f)]
        public void NormalVoice_AfterTheNpcsTail_IsHeard(float voice)
        {
            var vad = AfterTheNpcReplied();

            Assert.That(Speech(vad, voice, SpeechPause, 3.0), Is.True,
                $"A normal voice (RMS {voice}) is not heard after the NPC spoke: the patient has to talk far too loudly. Threshold: {vad.CurrentThreshold:F4}");
        }

        [Test]
        public void TheNpcsTail_DoesNotLiftTheThreshold()
        {
            var vad = new VADManager(CreateProcessor());
            var before = vad.CurrentThreshold;

            Feed(vad, 0.10f, 0.5);
            Feed(vad, 0.06f, 0.5);
            Feed(vad, Silence, 2.0);

            Assert.That(vad.CurrentThreshold, Is.EqualTo(before).Within(1e-6f),
                "The NPC's echo right after a listen starts raised the threshold above a normal voice.");
        }

        /// <summary>
        /// The TV was on before the listen started (5 s, the noise window). Switching a TV on during a listen starts a turn
        /// until the window has learned it; that is not covered here.
        /// </summary>
        [Test]
        public void VoiceOverATv_IsHeard_AndTheTvAloneStartsNothing()
        {
            var vad = new VADManager(CreateProcessor());
            Speech(vad, TvVoiced, TvPause, 6.0);
            Feed(vad, TvPause, 0.5);

            Assert.That(Speech(vad, TvVoiced, TvPause, 9.0), Is.False, "The TV alone started a turn.");
            Assert.That(Speech(vad, 0.013f, TvPause, 3.0), Is.True, "A normal voice over the TV is not heard.");
        }

        [Test]
        public void TheNpcsOwnVoice_StaysOutOfTheNoiseFloor()
        {
            var vad = new VADManager(CreateProcessor());
            var before = vad.CurrentThreshold;
            vad.SetNpcSpeaking(true);

            Speech(vad, 0.07f, 0.02f, 10.0);

            Assert.That(vad.CurrentThreshold, Is.EqualTo(before).Within(1e-6f), "The NPC's voice through the speaker became the room noise.");
        }

        [Test]
        public void Silence_StartsNothing()
        {
            var vad = new VADManager(CreateProcessor());

            Assert.That(Feed(vad, Silence, 10.0), Is.False);
        }

        [Test]
        public void SteadyNoise_LiftsTheThreshold_ToNoiseTimesTheFactor()
        {
            var vad = new VADManager(new NoiseFloorVADProcessor("Test", minimumThreshold: 0.006f, noiseFactor: 3f));

            Feed(vad, 0.004f, 6.0);

            Assert.That(vad.CurrentThreshold, Is.EqualTo(0.012f).Within(1e-5f), "A steady noise (a fan, a TV) did not lift the threshold.");
            Assert.That(vad.NoiseFloor, Is.EqualTo(0.004f).Within(1e-6f));
        }

        [TestCase(0f, 3f)]
        [TestCase(0.006f, 0.5f)]
        public void Settings_OutOfRange_FailHard(float minimum, float factor)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new NoiseFloorVADProcessor("Test", minimum, factor));
        }

        [Test]
        public void SpeechInputHandler_WithTheNoiseFloorVad_UsesItWithItsSettings()
        {
            var go = new GameObject("NoiseFloorVadTests");
            try
            {
                go.AddComponent<StubCapture>();
                var handler = go.AddComponent<SpeechInputHandler>();
                var serialized = new SerializedObject(handler);
                serialized.FindProperty("useNoiseFloorVAD").boolValue = true;
                serialized.FindProperty("noiseFloorVadMinimumThreshold").floatValue = 0.007f;
                serialized.FindProperty("noiseFloorVadFactor").floatValue = 4f;
                serialized.ApplyModifiedPropertiesWithoutUndo();

                handler.InitializeForTesting();

                Assert.That(handler.VadManager.Processor, Is.TypeOf<NoiseFloorVADProcessor>(), "The Inspector switch does not choose the noise-floor VAD.");
                Assert.That(handler.VadManager.CurrentThreshold, Is.EqualTo(0.007f).Within(1e-6f), "The Inspector minimum does not reach the VAD.");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
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
        }
#pragma warning restore 67
    }
}
