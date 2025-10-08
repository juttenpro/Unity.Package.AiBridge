using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Tsc.AIBridge.Audio.VAD
{
    /// <summary>
    /// Dynamic VAD that adapts threshold based on observed noise and speech levels
    /// No learning phase - starts detecting immediately with conservative threshold
    /// </summary>
    public class DynamicRangeVADProcessor : VADProcessorBase
    {
        // Configuration
        private const float DefaultThreshold = 0.03f; // Lowered for better sensitivity
        private const float MinThreshold = 0.015f; // Original value - production doesn't need safety net
        private const float MaxThreshold = 0.10f; // Cap for noisy environments (prevents VAD from becoming deaf)
        private const float DefaultAdditiveMarginQuiet = 0.02f; // Default additive margin for quiet environments
        private const float DefaultAdditiveMarginNoisy = 0.015f; // Default additive margin for noisy environments
        private const float NoisyEnvironment = 0.04f; // Above this = noisy environment

        // Configurable margins
        private float _additiveMarginQuiet;
        private float _additiveMarginNoisy;

        // Adaptive calibration settings
        private bool _useAdaptiveCalibration = true;
        private float _lastCalibrationTime = 0f;
        private const float CalibrationInterval = 5.0f; // Recalibrate every 5 seconds of silence

        // State tracking
        private float _currentThreshold = DefaultThreshold;
        private readonly List<float> _quietSamples = new(100);
        private readonly List<float> _loudSamples = new(100);
        private bool _isNpcSpeaking; // Track if NPC is currently speaking
        private bool _hasWarnedAboutLowNoiseFloor; // Track if we've already warned about low noise
        private bool _hasWarnedAboutSuspiciouslyLowInput; // Track if we've already warned about suspiciously low input
        private bool _hasWarnedAboutPoorSignalToNoise; // Track if we've already warned about poor signal-to-noise
        private bool _hasWarnedAboutLowSignalToNoise; // Track if we've already warned about low signal-to-noise

        // Smart margin learning
        private readonly List<float> _quietestSpeechVolumes = new(10); // Track QUIETEST speech volumes only
        private float _currentAdaptiveMargin = -1f; // -1 means using default
        private float _lowestDetectedSpeech = float.MaxValue; // Track absolute minimum speech volume ever detected

        // Sustained detection - prevent spikes/clicks from triggering speech
        private float _volumeAboveThresholdTime; // How long volume has been above threshold
        private const float SustainedSpeechTime = 0.05f; // Require 50ms of sustained volume for speech detection (lowered from 100ms for faster response)

        // Smoothing
        private readonly Queue<float> _recentVolumes = new();
        private const int SmoothingWindow = 5; // ~100ms at 50Hz

        public DynamicRangeVADProcessor(string name, bool enableVerboseLogging = false, bool useAdaptiveCalibration = true,
            float? additiveMarginQuiet = null, float? additiveMarginNoisy = null)
            : base(name, enableVerboseLogging)
        {
            _useAdaptiveCalibration = useAdaptiveCalibration;
            _additiveMarginQuiet = additiveMarginQuiet ?? DefaultAdditiveMarginQuiet;
            _additiveMarginNoisy = additiveMarginNoisy ?? DefaultAdditiveMarginNoisy;
        }

        /// <summary>
        /// Set whether NPC is currently speaking.
        /// When NPC is speaking, VAD won't adapt thresholds to prevent NPC audio from affecting calibration.
        /// </summary>
        public void SetNpcSpeaking(bool isSpeaking)
        {
            _isNpcSpeaking = isSpeaking;
            if (EnableVerboseLogging && isSpeaking)
            {
                Debug.Log($"[{Name}VAD] NPC speaking - threshold adaptation paused");
            }
        }

        public override bool ProcessAudioFrame(float[] audioData, float pttDuration = 0f)
        {
            if (audioData == null || audioData.Length == 0)
                return CurrentlySpeaking;

            // Store the audio frame for feedback detection
            LastAudioFrame = audioData;

            var deltaTime = 0.02f; // ~50Hz

            // Calculate RMS volume
            var currentVolume = CalculateRMS(audioData);

            // DEBUG: Log raw volume every 180 frames (reduced spam) and only in verbose mode
            if (EnableVerboseLogging && Time.frameCount % 180 == 0)
            {
                Debug.Log($"[{Name}VAD-DEBUG] Raw RMS: {currentVolume:F6}, Array length: {audioData.Length}, PTT duration: {pttDuration:F3}s");
            }

            // Smooth the volume
            _recentVolumes.Enqueue(currentVolume);
            if (_recentVolumes.Count > SmoothingWindow)
                _recentVolumes.Dequeue();

            var smoothedVolume = _recentVolumes.Average();

            // CRITICAL: Collect samples for adaptation
            // Allow sample collection during first 2 seconds of PTT even if NPC is speaking
            // This ensures we can adapt to the user's voice level during first turn
            var allowAdaptation = !_isNpcSpeaking || pttDuration < 2.0f;

            if (allowAdaptation)
            {
                // Collect samples for threshold adaptation
                CollectSamples(smoothedVolume);

                // Adapt threshold based on collected samples
                AdaptThreshold();

                // Log when we're adapting despite NPC speaking (first 2 seconds)
                if (_isNpcSpeaking && pttDuration < 2.0f && EnableVerboseLogging && Time.frameCount % 90 == 0)
                {
                    Debug.Log($"[{Name}VAD] Early PTT adaptation period - collecting samples (PTT: {pttDuration:F1}s)");
                }

                // Track time for periodic recalibration during silence
                if (_useAdaptiveCalibration && !CurrentlySpeaking && smoothedVolume < _currentThreshold)
                {
                    _lastCalibrationTime += 0.02f; // deltaTime approximation

                    // Recalibrate periodically during silence
                    if (_lastCalibrationTime > CalibrationInterval)
                    {
                        _lastCalibrationTime = 0f;
                        if (EnableVerboseLogging)
                        {
                            Debug.Log($"[{Name}VAD] Periodic recalibration during silence");
                        }
                        ForceAdaptation();
                    }
                }
                else
                {
                    _lastCalibrationTime = 0f; // Reset when speaking
                }
            }
            else if (EnableVerboseLogging && Time.frameCount % 180 == 0)
            {
                Debug.Log($"[{Name}VAD] Skipping adaptation - NPC is speaking and PTT > 2s");
            }

            // CRITICAL: Sustained detection to prevent clicks/spikes
            // Volume must be above threshold for SUSTAINED_SPEECH_TIME before detecting speech
            var volumeIsLoud = smoothedVolume > _currentThreshold;

            // DEBUG: Log decision factors every 180 frames (reduced spam) and only in verbose mode
            if (EnableVerboseLogging && Time.frameCount % 180 == 0)
            {
                Debug.Log($"[{Name}VAD-DEBUG] Smoothed: {smoothedVolume:F6}, Threshold: {_currentThreshold:F6}, IsLoud: {volumeIsLoud}, SustainedTime: {_volumeAboveThresholdTime:F3}s, CurrentlySpeaking: {CurrentlySpeaking}");
            }

            bool isSpeaking;

            if (volumeIsLoud)
            {
                _volumeAboveThresholdTime += deltaTime;

                // DEBUG: Log when we're accumulating time (only in verbose mode and reduced frequency)
                if (EnableVerboseLogging && _volumeAboveThresholdTime > 0 && _volumeAboveThresholdTime < SustainedSpeechTime && Time.frameCount % 60 == 0)
                {
                    Debug.Log($"[{Name}VAD-DEBUG] Accumulating loud time: {_volumeAboveThresholdTime:F3}s / {SustainedSpeechTime:F3}s needed");
                }

                // Only consider it loud enough if sustained for minimum time
                // This filters out keyboard clicks, PTT noise, and other short spikes
                var sustainedLoudEnough = _volumeAboveThresholdTime >= SustainedSpeechTime;

                // DEBUG: Log when sustained threshold is reached
                if (EnableVerboseLogging && sustainedLoudEnough && !CurrentlySpeaking)
                {
                    Debug.Log($"[{Name}VAD-TRIGGER] SUSTAINED THRESHOLD REACHED! Time: {_volumeAboveThresholdTime:F3}s, Volume: {smoothedVolume:F6}, Threshold: {_currentThreshold:F6}");
                }

                // Track QUIETEST speech volumes for smart margin learning
                if (sustainedLoudEnough && !CurrentlySpeaking)
                {
                    // Update absolute minimum
                    if (smoothedVolume < _lowestDetectedSpeech)
                    {
                        _lowestDetectedSpeech = smoothedVolume;

                        // Only track if it's relatively quiet speech (not shouting)
                        // Consider "quiet" as less than 2x the current threshold
                        if (smoothedVolume < _currentThreshold * 2.0f)
                        {
                            _quietestSpeechVolumes.Add(smoothedVolume);
                            if (_quietestSpeechVolumes.Count > 10)
                                _quietestSpeechVolumes.RemoveAt(0);
                        }

                        // Gradually learn better margin
                        LearnOptimalMargin();
                    }
                }

                // Use base class for speech state management
                isSpeaking = ProcessSpeechDetection(sustainedLoudEnough, deltaTime);

                // DEBUG: Log state changes
                var stateChanged = EnableVerboseLogging && isSpeaking != CurrentlySpeaking;
                if (stateChanged)
                {
                    Debug.Log($"[{Name}VAD-STATE] Speech state WILL CHANGE: {CurrentlySpeaking} -> {isSpeaking}");
                }
            }
            else
            {
                // Volume dropped below threshold - reset sustained timer
                if (EnableVerboseLogging && _volumeAboveThresholdTime > 0)
                {
                    Debug.Log($"[{Name}VAD-DEBUG] Volume dropped below threshold, resetting timer from {_volumeAboveThresholdTime:F3}s");
                }
                _volumeAboveThresholdTime = 0f;

                // Use base class for speech state management (will handle pause tolerance)
                isSpeaking = ProcessSpeechDetection(false, deltaTime);
            }

            return isSpeaking;
        }

        private void CollectSamples(float volume)
        {
            // CRITICAL FIX: Widen the collection ranges to capture more samples
            // Previous ranges were too narrow (0.5x to 1.5x), causing voices around 0.04
            // to fall in the "ambiguous" zone and never trigger adaptation

            // Categorize as quiet or loud based on current threshold
            // Use wider ranges: 0.8x for quiet, 1.2x for loud (was 0.5x and 1.5x)
            if (volume < _currentThreshold * 0.8f)
            {
                // Definitely quiet/noise (< 0.024 with threshold 0.03)
                _quietSamples.Add(volume);
                if (_quietSamples.Count > 100)
                    _quietSamples.RemoveAt(0);

                // Don't log individual sample collection - causes massive spam
                // Adaptation logging in AdaptThreshold() is sufficient
            }
            else if (volume > _currentThreshold * 1.2f)
            {
                // Definitely loud/speech (> 0.036 with threshold 0.03)
                _loudSamples.Add(volume);
                if (_loudSamples.Count > 100)
                    _loudSamples.RemoveAt(0);

                // Don't log individual sample collection - causes massive spam
                // Adaptation logging in AdaptThreshold() is sufficient
            }
            // Volumes near threshold (0.024 to 0.036) are still ignored as ambiguous
            // but the range is now much smaller
        }

        private void AdaptThreshold()
        {
            // Need enough samples to adapt (lowered from 20 to 10 for faster initial adaptation)
            if (_quietSamples.Count < 10)
            {
                // Log why adaptation isn't happening
                if (EnableVerboseLogging && Time.frameCount % 180 == 0)
                {
                    Debug.Log($"[{Name}VAD] Adaptation skipped - insufficient samples (quiet: {_quietSamples.Count}/10, loud: {_loudSamples.Count})");
                }
                return; // Not enough data yet
            }

            // Calculate noise floor from quiet samples (75th percentile to avoid spikes)
            var sortedQuiet = _quietSamples.OrderBy(x => x).ToList();
            var calculatedNoiseFloor = sortedQuiet[sortedQuiet.Count * 3 / 4];

            // DEV SAFETY: Prevent threshold from going too low (mainly for development)
            // In production (VR/Mobile) this is rarely needed as mic selection is automatic
            // But in Unity Editor with multiple mics, wrong selection can cause near-zero noise floor
            // This safety net helps developers but doesn't affect production sensitivity
            const float MinimumNoiseFloor = 0.003f; // Very low - mostly for dev safety
            var noiseFloor = Mathf.Max(calculatedNoiseFloor, MinimumNoiseFloor);

            // Only log noise floor clamping once per session in verbose mode to avoid spam
            if (EnableVerboseLogging && calculatedNoiseFloor < MinimumNoiseFloor && !_hasWarnedAboutLowNoiseFloor)
            {
                Debug.Log($"[{Name}VAD] Noise floor clamped: {calculatedNoiseFloor:F6} → {MinimumNoiseFloor:F6} (wrong mic/very quiet environment detected)");
                _hasWarnedAboutLowNoiseFloor = true;
            }
            // Reset warning flag if noise floor returns to normal
            else if (calculatedNoiseFloor >= MinimumNoiseFloor)
            {
                _hasWarnedAboutLowNoiseFloor = false;
            }

            // Calculate new threshold
            float newThreshold;

            if (_loudSamples.Count >= 20)
            {
                // We have both quiet and loud samples - find optimal separation
                var sortedLoud = _loudSamples.OrderBy(x => x).ToList();
                var speechFloor = sortedLoud[sortedLoud.Count / 4]; // 25th percentile of loud

                // Check signal-to-noise ratio - only warn when critically low
                // Reduced thresholds: 1.2x for critical (was 2.0x), 1.5x for low (was 3.0x)
                // Most headsets work fine with 2-3x ratio, so these were false warnings
                var signalToNoiseRatio = speechFloor / noiseFloor;
                if (signalToNoiseRatio < 1.2f && !_hasWarnedAboutPoorSignalToNoise)
                {
                    Debug.LogWarning($"[{Name}VAD] ⚠️ CRITICAL SIGNAL-TO-NOISE RATIO: {signalToNoiseRatio:F1}x " +
                                   $"(noise: {noiseFloor:F4}, speech: {speechFloor:F4})\n" +
                                   $"CAUSES: Wrong microphone selected, mic too far away, or very noisy environment\n" +
                                   $"SOLUTION: Check mic selection in Windows/Unity, move mic closer, or reduce background noise");
                    _hasWarnedAboutPoorSignalToNoise = true;
                }
                else if (signalToNoiseRatio < 1.5f && !_hasWarnedAboutLowSignalToNoise && !_hasWarnedAboutPoorSignalToNoise)
                {
                    Debug.LogWarning($"[{Name}VAD] ⚠️ VERY LOW SIGNAL-TO-NOISE RATIO: {signalToNoiseRatio:F1}x " +
                                   $"(noise: {noiseFloor:F4}, speech: {speechFloor:F4})\n" +
                                   $"May cause unreliable speech detection. Consider adjusting mic position or gain.");
                    _hasWarnedAboutLowSignalToNoise = true;
                }
                // Reset warnings if signal improves (lowered from 4.0x to 2.0x)
                else if (signalToNoiseRatio >= 2.0f)
                {
                    _hasWarnedAboutPoorSignalToNoise = false;
                    _hasWarnedAboutLowSignalToNoise = false;
                }

                // Set threshold between noise and speech (closer to noise)
                newThreshold = noiseFloor + (speechFloor - noiseFloor) * 0.35f;

                // But ensure minimum margin above noise (ADDITIVE based on environment)
                // Use learned margin if available, otherwise use defaults
                var additiveMargin = GetCurrentAdditiveMargin(noiseFloor);
                var marginThreshold = noiseFloor + additiveMargin; // ADDITIVE, not multiplicative
                newThreshold = Mathf.Max(newThreshold, marginThreshold);
            }
            else
            {
                // Only have noise data - use ADDITIVE margin based on environment
                // In noisy environments, use smaller margin so people don't have to shout
                var additiveMargin = GetCurrentAdditiveMargin(noiseFloor);
                newThreshold = noiseFloor + additiveMargin; // ADDITIVE, not multiplicative

                // Warning for high noise environments
                if (noiseFloor > 0.05f)
                {
                    Debug.LogWarning($"[{Name}VAD] ⚠️ VERY HIGH BACKGROUND NOISE: {noiseFloor:F4}\n" +
                                   $"This will make speech detection difficult.\n" +
                                   $"SOLUTION: Reduce background noise, use headset with noise cancellation, or move to quieter location");
                }
                else if (noiseFloor > NoisyEnvironment)
                {
                    if (EnableVerboseLogging)
                    {
                        Debug.Log($"[{Name}VAD] Noisy environment detected (noise: {noiseFloor:F4}) - using reduced margin");
                    }
                }

                // Warning for suspiciously low noise (likely wrong mic) - only warn once
                if (calculatedNoiseFloor < 0.001f && _quietSamples.Count >= 20 && !_hasWarnedAboutSuspiciouslyLowInput)
                {
                    Debug.LogWarning($"[{Name}VAD] ⚠️ SUSPICIOUSLY LOW AUDIO INPUT: {calculatedNoiseFloor:F6}\n" +
                                   $"LIKELY CAUSE: Wrong microphone selected or mic not receiving audio\n" +
                                   $"SOLUTION: Check audio input device in Windows Sound Settings and Unity Audio Settings");
                    _hasWarnedAboutSuspiciouslyLowInput = true;
                }
                // Reset warning if input returns to normal
                else if (calculatedNoiseFloor >= 0.001f)
                {
                    _hasWarnedAboutSuspiciouslyLowInput = false;
                }
            }

            // Apply limits and smooth the change
            newThreshold = Mathf.Clamp(newThreshold, MinThreshold, MaxThreshold);

            // Store old threshold for logging
            var oldThreshold = _currentThreshold;

            // Smooth threshold changes to avoid jumps
            _currentThreshold = _currentThreshold * 0.9f + newThreshold * 0.1f;

            // Only log significant threshold changes to reduce spam
            if (EnableVerboseLogging && Mathf.Abs(oldThreshold - _currentThreshold) > 0.005f)
            {
                Debug.Log($"[{Name}VAD-ADAPTED] Threshold changed: {oldThreshold:F4} → {_currentThreshold:F4} " +
                         $"(noise floor: {noiseFloor:F4}, samples: {_quietSamples.Count} quiet, {_loudSamples.Count} loud)");
            }
            // Skip minor threshold changes - they cause too much log spam
            // Removed "No threshold change needed" logs - too spammy
        }

        public override void Reset()
        {
            if (EnableVerboseLogging)
            {
                Debug.Log($"[{Name}VAD-RESET] FULL RESET CALLED - Clearing all state");
                Debug.Log($"[{Name}VAD-RESET] Before reset: Threshold={_currentThreshold:F6}, SustainedTime={_volumeAboveThresholdTime:F3}s, Speaking={CurrentlySpeaking}");
            }

            base.Reset();
            _currentThreshold = DefaultThreshold;
            _quietSamples.Clear();
            _loudSamples.Clear();
            _recentVolumes.Clear();
            _volumeAboveThresholdTime = 0f; // Reset sustained detection timer
            _hasWarnedAboutLowNoiseFloor = false; // Reset all warning flags
            _hasWarnedAboutSuspiciouslyLowInput = false;
            _hasWarnedAboutPoorSignalToNoise = false;
            _hasWarnedAboutLowSignalToNoise = false;

            // Reset learned margin but keep the samples for next session
            _currentAdaptiveMargin = -1f;
            // Keep _quietestSpeechVolumes for cross-session learning
            _lowestDetectedSpeech = float.MaxValue;

            if (EnableVerboseLogging)
            {
                Debug.Log($"[{Name}VAD-RESET] After reset: Threshold={_currentThreshold:F6}, SustainedTime={_volumeAboveThresholdTime:F3}s, Speaking={CurrentlySpeaking}");
            }
        }

        public override float CurrentThreshold => _currentThreshold;

        /// <summary>
        /// Get info for debugging
        /// </summary>
        public string GetRangeInfo()
        {
            var noiseLevel = _quietSamples.Count > 0 ? _quietSamples.Average() : 0f;
            var speechLevel = _loudSamples.Count > 0 ? _loudSamples.Average() : 0f;

            return $"Threshold: {_currentThreshold:F4}, Noise: {noiseLevel:F4}, Speech: {speechLevel:F4}, " +
                   $"Samples: {_quietSamples.Count} quiet, {_loudSamples.Count} loud" +
                   (_currentAdaptiveMargin > 0 ? $", Learned margin: {_currentAdaptiveMargin:F4}" : "");
        }

        /// <summary>
        /// Learn optimal margin based on quietest detected speech
        /// </summary>
        private void LearnOptimalMargin()
        {
            if (_quietestSpeechVolumes.Count < 3)
                return; // Need enough samples

            // Calculate current noise floor
            var noiseFloor = _quietSamples.Count > 10
                ? _quietSamples.OrderBy(x => x).ToList()[_quietSamples.Count * 3 / 4]
                : 0.001f;

            // Find the quietest reliable speech (use 20th percentile to avoid outliers)
            var sortedSpeech = _quietestSpeechVolumes.OrderBy(x => x).ToList();
            var quietestReliableSpeech = sortedSpeech[sortedSpeech.Count / 5]; // 20th percentile

            // Calculate optimal margin (80% of distance to quietest speech for safety)
            var idealMargin = (quietestReliableSpeech - noiseFloor) * 0.8f;

            // Only update if new margin would be SMALLER (more sensitive)
            // Never make it less sensitive based on loud speech
            if (_currentAdaptiveMargin < 0 || idealMargin < _currentAdaptiveMargin)
            {
                // Gradually adjust (10% change per update)
                if (_currentAdaptiveMargin < 0)
                {
                    _currentAdaptiveMargin = idealMargin; // First time learning
                }
                else
                {
                    _currentAdaptiveMargin = _currentAdaptiveMargin * 0.9f + idealMargin * 0.1f;
                }

                // But never go below a minimum safety margin
                _currentAdaptiveMargin = Mathf.Max(_currentAdaptiveMargin, 0.005f);

                if (EnableVerboseLogging)
                {
                    Debug.Log($"[{Name}VAD-LEARNED] Adjusted margin to {_currentAdaptiveMargin:F4} " +
                             $"(quietest speech: {quietestReliableSpeech:F4}, noise: {noiseFloor:F4})");
                }
            }
        }

        /// <summary>
        /// Get current additive margin (learned or default)
        /// </summary>
        private float GetCurrentAdditiveMargin(float noiseFloor)
        {
            // If we have learned from real speech, use that
            if (_currentAdaptiveMargin > 0)
            {
                return _currentAdaptiveMargin;
            }

            // Otherwise use defaults based on environment
            return noiseFloor > NoisyEnvironment ? _additiveMarginNoisy : _additiveMarginQuiet;
        }

        /// <summary>
        /// Force immediate threshold adaptation with current samples
        /// Used after PTT release calibration period
        /// </summary>
        public void ForceAdaptation()
        {
            //Debug.Log($"[{_name}VAD] FORCED ADAPTATION - Current samples: {_quietSamples.Count} quiet, {_loudSamples.Count} loud");

            // If we have at least 5 quiet samples, use them for adaptation
            if (_quietSamples.Count >= 5)
            {
                // Run adaptation even with fewer samples
                AdaptThreshold();
                //Debug.Log($"[{_name}VAD] Forced adaptation complete with {_quietSamples.Count} samples");
            }
            else if (_loudSamples.Count >= 10)
            {
                // If we only have loud samples (user was speaking), use them to set a lower threshold
                // This ensures the threshold will be below the user's voice level
                var sortedLoud = _loudSamples.OrderBy(x => x).ToList();
                var quietestLoud = sortedLoud[0]; // Minimum volume when speaking

                // Set threshold at 70% of the quietest speech volume
                var newThreshold = quietestLoud * 0.7f;
                newThreshold = Mathf.Clamp(newThreshold, MinThreshold, MaxThreshold);

                var oldThreshold = _currentThreshold;
                _currentThreshold = newThreshold;

                //Debug.Log($"[{_name}VAD-ADAPTED] Forced from speech samples: {oldThreshold:F4} → {_currentThreshold:F4} " +
                //         $"(quietest speech: {quietestLoud:F4}, samples: {_loudSamples.Count} loud)");
            }
            else
            {
                // Not enough samples - lower threshold by 20% to be more sensitive
                var oldThreshold = _currentThreshold;
                _currentThreshold = Mathf.Max(_currentThreshold * 0.8f, MinThreshold);

                //Debug.Log($"[{_name}VAD-ADAPTED] Forced lower (insufficient samples): {oldThreshold:F4} → {_currentThreshold:F4}");
            }
        }
    }
}