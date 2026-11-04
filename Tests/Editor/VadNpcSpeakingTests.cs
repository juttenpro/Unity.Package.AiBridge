using NUnit.Framework;
using Tsc.AIBridge.Audio.VAD;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: while the NPC speaks, its own voice must not teach the adaptive VAD threshold, also with an
    /// open microphone (voice activation, no push-to-talk).
    ///
    /// WHY: Assistant Platform on a Pixel, 2026-11-03: "I have to talk really loudly before it works". The NPC's voice
    ///      through the phone speaker reached the microphone and filled the VAD's loud samples, which set the threshold
    ///      at her level. SetNpcSpeaking(true) is meant to pause that, but the exception for the first 2 s of a PTT
    ///      press checked only "pttDuration &lt; 2", and without PTT the duration is always 0: the pause never held.
    /// WHAT: NPC speaking and no PTT: the threshold stays put. NPC silent: it still adapts. PTT held under 2 s while
    ///       the NPC speaks: it still adapts (the Training Platform's first-turn behaviour, unchanged).
    /// HOW:  Frames of a fixed amplitude (RMS = amplitude), loud and quiet mixed like speech, straight into the processor.
    /// NOT COVERED: real echo on a phone, and SpeechInputHandler passing the duration (it passes 0 without PTT).
    /// </summary>
    public class VadNpcSpeakingTests
    {
        const int Frames = 1000;

        static float[] Frame(float amplitude)
        {
            var samples = new float[320];
            for (var i = 0; i < samples.Length; i++) samples[i] = amplitude;
            return samples;
        }

        /// <summary>
        /// 30 loud frames, then 15 quiet ones: speech with pauses, as the NPC's voice sounds in the microphone. Blocks, not
        /// single frames: the processor smooths over 5 frames, so a lone quiet frame never counts as quiet.
        /// </summary>
        static void FeedSpeech(DynamicRangeVADProcessor vad, float pttDuration)
        {
            var loud = Frame(0.08f);
            var quiet = Frame(0.005f);
            for (var i = 0; i < Frames; i++)
                vad.ProcessAudioFrame(i / 15 % 3 == 2 ? quiet : loud, pttDuration);
        }

        [Test]
        public void NpcSpeaking_WithoutPtt_DoesNotMoveTheThreshold()
        {
            var vad = new DynamicRangeVADProcessor("Test", useAdaptiveCalibration: true);
            var before = vad.CurrentThreshold;
            vad.SetNpcSpeaking(true);

            FeedSpeech(vad, pttDuration: 0f);

            Assert.That(vad.CurrentThreshold, Is.EqualTo(before).Within(1e-6f),
                "The NPC's own voice raised the VAD threshold: the patient has to talk loudly to be heard.");
        }

        [Test]
        public void NpcSilent_TheThresholdStillAdapts()
        {
            var vad = new DynamicRangeVADProcessor("Test", useAdaptiveCalibration: true);
            var before = vad.CurrentThreshold;

            FeedSpeech(vad, pttDuration: 0f);

            Assert.That(vad.CurrentThreshold, Is.Not.EqualTo(before).Within(1e-4f), "The adaptive VAD no longer adapts at all.");
        }

        [Test]
        public void NpcSpeaking_DuringTheFirstTwoSecondsOfPtt_StillAdapts()
        {
            var vad = new DynamicRangeVADProcessor("Test", useAdaptiveCalibration: true);
            var before = vad.CurrentThreshold;
            vad.SetNpcSpeaking(true);

            FeedSpeech(vad, pttDuration: 0.5f);

            Assert.That(vad.CurrentThreshold, Is.Not.EqualTo(before).Within(1e-4f),
                "The first-turn adaptation during PTT is gone: the Training Platform's behaviour changed.");
        }
    }
}
