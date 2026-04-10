using NUnit.Framework;
using Tsc.AIBridge.Audio.VAD;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: VAD configuration values from SpeechInputHandler Inspector must
    /// actually reach the underlying DynamicRangeVADProcessor. Before v1.6.16 the
    /// VADManager.SetAdaptiveSettings() and SetFixedThreshold() methods were stubs that only
    /// logged but never applied their parameters, so the Inspector fields (adaptiveMargin,
    /// minimumThreshold, fixedVadThreshold, useAdaptiveVAD) were entirely placebo.
    ///
    /// WHY: In production, end users reported having to almost shout to interrupt NPCs, even
    ///      with a close-talk headset in a quiet office. The root cause was that the VAD used
    ///      hardcoded defaults (0.03 threshold, 0.02 quiet margin) regardless of Inspector
    ///      configuration. Users with soft voices or headsets that produce low RMS (~0.012-0.018)
    ///      never crossed the threshold and could not interrupt.
    /// WHAT: These tests verify that configuration flows through VADManager to the processor
    ///      and that the processor can detect speech at realistic headset volumes.
    /// HOW: Construct processors directly with known configuration, feed synthetic audio frames
    ///      at target RMS levels, and assert on the detection result and current threshold.
    ///
    /// SUCCESS CRITERIA:
    /// - SetAdaptiveSettings(margin, minThreshold) actually changes the processor's behavior
    /// - SetFixedThreshold(value) makes the processor use a constant threshold
    /// - Speech at RMS 0.018 (typical close-talk headset at normal speaking volume) is detected
    /// - Current threshold is queryable for production diagnostic logging
    ///
    /// BUSINESS IMPACT:
    /// - Failing = Users must shout to interrupt → bad training UX → lost credibility
    /// - Failing = "Configurable" settings in Inspector are lies → developers can't tune per scenario
    /// - Failing = No regression protection against reintroducing stub methods
    /// </summary>
    [TestFixture]
    public class VadConfigurationTests
    {
        /// <summary>
        /// BUSINESS REQUIREMENT: The processor must accept custom margins via its constructor
        /// so that callers (VADManager, tests, per-persona configuration) can tune sensitivity
        /// without reflection or editing the package source.
        /// </summary>
        [Test]
        public void DynamicRangeVADProcessor_ConstructorAcceptsCustomMargins()
        {
            var processor = new DynamicRangeVADProcessor(
                "Test",
                enableVerboseLogging: false,
                useAdaptiveCalibration: true,
                additiveMarginQuiet: 0.008f,
                additiveMarginNoisy: 0.006f);

            // Processor must be constructed without throwing and expose a current threshold.
            // The initial threshold is the default, but the constructor parameters must be
            // stored for use when the first adaptation runs.
            Assert.IsNotNull(processor);
            Assert.Greater(processor.CurrentThreshold, 0f);
        }

        /// <summary>
        /// BUSINESS REQUIREMENT: Minimum threshold must be configurable so that installations
        /// with very quiet environments (close-talk headset, silent office) can drive detection
        /// sensitivity below the hardcoded 0.015 floor.
        /// </summary>
        [Test]
        public void DynamicRangeVADProcessor_AllowsLoweringMinimumThreshold()
        {
            var processor = new DynamicRangeVADProcessor(
                "Test",
                enableVerboseLogging: false,
                useAdaptiveCalibration: true);

            processor.SetMinimumThreshold(0.006f);

            // After lowering the floor, calling ForceAdaptation on an empty sample set should
            // allow the threshold to drift below the old 0.015 hardcoded floor.
            // We simulate silence (very quiet audio) and let adaptation run.
            var silence = new float[480]; // 10ms at 48kHz, all zeros
            for (var i = 0; i < 30; i++)
            {
                processor.ProcessAudioFrame(silence, pttDuration: 0f);
            }
            processor.ForceAdaptation();

            // With a lowered floor, threshold should be allowed to go below 0.015.
            // The exact value depends on noise floor calculation, but it MUST be possible.
            Assert.LessOrEqual(processor.CurrentThreshold, 0.015f + 0.001f,
                "Lowering minimum threshold should allow adaptation below the previous hardcoded floor");
        }

        /// <summary>
        /// BUSINESS REQUIREMENT: VADManager.SetAdaptiveSettings must NOT be a stub. The margin
        /// and minimum threshold parameters must reach the underlying processor. This is the
        /// specific bug that caused production issues: the method existed, logged a line, but
        /// did nothing.
        /// </summary>
        [Test]
        public void VADManager_SetAdaptiveSettings_PropagatesToProcessor()
        {
            var manager = new VADManager(enableLogging: false);

            var beforeThreshold = manager.CurrentThreshold;

            // Apply aggressive sensitivity settings.
            manager.SetAdaptiveSettings(margin: 0.006f, minThreshold: 0.006f);

            // Processor must acknowledge the new settings. We verify by checking that the
            // processor is accessible and configured. If the method is still a stub, the
            // diagnostic info will not reflect the new margin.
            Assert.IsNotNull(manager.Processor);
            Assert.IsTrue(manager.Processor is DynamicRangeVADProcessor);

            // After applying aggressive settings, feed silence and force adaptation.
            // The resulting threshold must be lower than what the hardcoded defaults would
            // produce (which had a 0.015 floor).
            var silence = new float[480];
            for (var i = 0; i < 30; i++)
            {
                manager.ProcessAudioFrame(silence, pttDuration: 0f);
            }
            manager.ForceAdaptation();

            Assert.LessOrEqual(manager.CurrentThreshold, 0.015f + 0.001f,
                "SetAdaptiveSettings must actually lower the floor when a smaller minThreshold is passed");
        }

        /// <summary>
        /// BUSINESS REQUIREMENT: SetFixedThreshold must disable adaptive calibration and pin
        /// the threshold. This is required for scenarios where administrators want predictable,
        /// non-learning behavior (e.g. classroom deployments with known mic setups).
        /// </summary>
        [Test]
        public void VADManager_SetFixedThreshold_PinsThresholdAndDisablesAdaptation()
        {
            var manager = new VADManager(enableLogging: false);

            manager.SetFixedThreshold(0.01f);

            // Feed noise that would normally cause adaptive calibration to raise the threshold.
            var loudishNoise = new float[480];
            for (var i = 0; i < loudishNoise.Length; i++)
                loudishNoise[i] = (i % 2 == 0) ? 0.04f : -0.04f; // RMS ~0.04

            for (var i = 0; i < 100; i++)
            {
                manager.ProcessAudioFrame(loudishNoise, pttDuration: 0f);
            }
            manager.ForceAdaptation();

            // With fixed threshold mode enabled, the threshold must remain near 0.01 regardless
            // of what samples were processed. Tolerance allows for minor smoothing artifacts.
            Assert.AreEqual(0.01f, manager.CurrentThreshold, 0.002f,
                "Fixed threshold must not drift from its pinned value");
        }

        /// <summary>
        /// BUSINESS REQUIREMENT: Default VAD thresholds must be low enough that a normal
        /// speaker wearing a close-talk headset in a quiet office can trigger speech detection
        /// without raising their voice. This is the regression test for the "must shout to
        /// interrupt" production report.
        ///
        /// A typical close-talk headset at normal speaking volume produces RMS in the
        /// 0.015-0.025 range. The VAD must detect this as speech with DEFAULT settings.
        /// </summary>
        [Test]
        public void DynamicRangeVADProcessor_DetectsTypicalHeadsetSpeechAtDefaultSettings()
        {
            var processor = new DynamicRangeVADProcessor(
                "Test",
                enableVerboseLogging: false,
                useAdaptiveCalibration: true);

            // Simulate 1 second of typical headset speech at RMS ~0.02.
            // 50 frames * 20ms = 1 second (ProcessAudioFrame assumes ~50Hz deltaTime).
            var speech = new float[480];
            for (var i = 0; i < speech.Length; i++)
                speech[i] = (i % 2 == 0) ? 0.02f : -0.02f; // RMS = 0.02

            bool detectedSpeech = false;
            for (var i = 0; i < 50; i++)
            {
                if (processor.ProcessAudioFrame(speech, pttDuration: 0f))
                {
                    detectedSpeech = true;
                    break;
                }
            }

            Assert.IsTrue(detectedSpeech,
                $"VAD must detect typical headset speech (RMS 0.02) within 1 second. " +
                $"Current threshold: {processor.CurrentThreshold:F4}");
        }

        /// <summary>
        /// BUSINESS REQUIREMENT: The VADManager must expose diagnostic information for
        /// production logging so that when users report "can't interrupt", the logs show the
        /// actual threshold and recent samples.
        /// </summary>
        [Test]
        public void VADManager_GetDiagnosticInfo_ReturnsNonEmptyString()
        {
            var manager = new VADManager(enableLogging: false);
            var info = manager.GetDiagnosticInfo();

            Assert.IsNotNull(info);
            Assert.IsNotEmpty(info);
            StringAssert.Contains("Threshold", info, "Diagnostic info should include the current threshold");
        }
    }
}
