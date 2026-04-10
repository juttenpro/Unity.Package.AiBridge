using System;
using UnityEngine;

namespace Tsc.AIBridge.Audio.VAD
{
    /// <summary>
    /// Manages Voice Activity Detection independently from MonoBehaviour components.
    /// Handles calibration, adaptation, and provides clean API for speech detection.
    /// Can be used for both interruption detection and future voice activation features.
    /// </summary>
    public class VADManager : IDisposable
    {
        private readonly VADProcessorBase _vadProcessor;
        private readonly bool _enableLogging;

        /// <summary>
        /// Is the user currently speaking (detected by VAD)
        /// </summary>
        public bool IsUserSpeaking => _vadProcessor?.IsSpeaking ?? false;

        /// <summary>
        /// Current VAD threshold
        /// </summary>
        public float CurrentThreshold => _vadProcessor?.CurrentThreshold ?? 0f;

        /// <summary>
        /// Get the underlying VAD processor for advanced operations
        /// </summary>
        public VADProcessorBase Processor => _vadProcessor;

        public VADManager(bool enableLogging = false)
        {
            _enableLogging = enableLogging;

            // Create adaptive VAD processor with additive thresholds
            _vadProcessor = new DynamicRangeVADProcessor("Player", enableLogging, useAdaptiveCalibration: true);

            if(enableLogging)
                Debug.Log($"[VADManager] Initialized (logging: TRUE)");
        }

        /// <summary>
        /// Process audio frame and detect speech
        /// </summary>
        /// <param name="samples">Audio samples to process</param>
        /// <param name="pttDuration">How long PTT has been pressed (for adaptation)</param>
        /// <returns>True if speech detected</returns>
        public bool ProcessAudioFrame(float[] samples, float pttDuration)
        {
            return _vadProcessor?.ProcessAudioFrame(samples, pttDuration) ?? false;
        }

        /// <summary>
        /// Reset VAD state for new PTT session
        /// </summary>
        public void ResetForNewSession()
        {
            _vadProcessor?.Reset();
        }

        /// <summary>
        /// Inform VAD that NPC is speaking (prevents threshold adaptation)
        /// </summary>
        public void SetNpcSpeaking(bool isSpeaking)
        {
            if (_vadProcessor is DynamicRangeVADProcessor dynamicVad)
            {
                dynamicVad.SetNpcSpeaking(isSpeaking);

                if (_enableLogging)
                {
                    Debug.Log($"[VADManager] NPC speaking: {isSpeaking}");
                }
            }
        }

        /// <summary>
        /// Force immediate threshold adaptation (after PTT release calibration)
        /// </summary>
        public void ForceAdaptation()
        {
            if (_vadProcessor is DynamicRangeVADProcessor dynamicVad)
            {
                dynamicVad.ForceAdaptation();

                if (_enableLogging)
                {
                    Debug.Log("[VADManager] Forced threshold adaptation after PTT release");
                }
            }
        }

        /// <summary>
        /// Calibrate VAD during the silence between STT completion and NPC response.
        /// This is the PERFECT moment for calibration:
        /// - User has stopped speaking (STT complete)
        /// - NPC hasn't started yet
        /// - Guaranteed silence period
        /// </summary>
        public void CalibrateBetweenSTTAndNPC()
        {
            if (_vadProcessor is DynamicRangeVADProcessor dynamicVad)
            {
                // Force adaptation with current noise samples
                dynamicVad.ForceAdaptation();

                if (_enableLogging)
                {
                    Debug.Log("[VADManager] Calibrating during STT->NPC silence gap - optimal timing!");
                }
            }
        }

        /// <summary>
        /// Set adaptive VAD settings (for adaptive mode).
        /// Before v1.6.16 this method was a stub that only logged. It now actually propagates
        /// the settings to the underlying DynamicRangeVADProcessor.
        /// </summary>
        /// <param name="margin">Additive margin above noise floor. Quiet-environment margin is
        /// set to this value; noisy-environment margin is derived as 80% of it so noisy rooms
        /// keep a slightly smaller margin (users don't have to raise their voice).</param>
        /// <param name="minThreshold">Minimum threshold floor even in silent environments.
        /// Lower this to allow close-talk headsets in very quiet rooms to trigger detection at
        /// realistic speaking volumes (~0.015-0.025 RMS).</param>
        public void SetAdaptiveSettings(float margin, float minThreshold)
        {
            if (_vadProcessor is DynamicRangeVADProcessor dynamicVad)
            {
                dynamicVad.SetAdaptiveMode();
                dynamicVad.SetMargins(margin, margin * 0.8f);
                dynamicVad.SetMinimumThreshold(minThreshold);

                if (_enableLogging)
                {
                    Debug.Log($"[VADManager] Adaptive settings applied: margin={margin:F4}, min={minThreshold:F4}");
                }
            }
        }

        /// <summary>
        /// Set a fixed VAD threshold (disables adaptive mode).
        /// Before v1.6.16 this method was a stub that only logged. It now actually pins the
        /// threshold on the underlying processor.
        /// </summary>
        /// <param name="threshold">Fixed threshold value. Adaptive calibration is disabled.</param>
        public void SetFixedThreshold(float threshold)
        {
            if (_vadProcessor is DynamicRangeVADProcessor dynamicVad)
            {
                dynamicVad.SetFixedThreshold(threshold);

                if (_enableLogging)
                {
                    Debug.Log($"[VADManager] Fixed threshold applied: {threshold:F4}");
                }
            }
        }

        /// <summary>
        /// Get diagnostic info about current VAD state
        /// </summary>
        public string GetDiagnosticInfo()
        {
            if (_vadProcessor is DynamicRangeVADProcessor dynamicVad)
            {
                return dynamicVad.GetRangeInfo();
            }
            return "VAD info not available";
        }

        /// <summary>
        /// Cleanup resources
        /// </summary>
        public void Dispose()
        {
            // Currently no resources to dispose, but keeping for future extensibility
            if (_enableLogging)
            {
                Debug.Log("[VADManager] Disposed");
            }
        }
    }
}