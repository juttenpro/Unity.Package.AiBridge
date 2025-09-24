using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SimulationCrew.AIBridge.Audio.Feedback
{
    /// <summary>
    /// Detects audio feedback by comparing microphone input with NPC audio output.
    /// Isolated implementation for easy removal if needed.
    /// </summary>
    public class AudioFeedbackDetector
    {
        // Configuration
        private const int BUFFER_SIZE = 2048; // ~40ms at 48kHz
        private const int CORRELATION_WINDOW = 512; // ~10ms for pattern matching
        private const float DELAY_MAX_MS = 100f; // Max expected delay between speaker and mic
        private const float CORRELATION_THRESHOLD = 0.6f; // 60% similarity = feedback detected
        private const float VOLUME_RATIO_MIN = 0.1f; // Mic volume must be at least 10% of NPC volume
        private const float VOLUME_RATIO_MAX = 0.9f; // Mic volume shouldn't be more than 90% of NPC

        // CRITICAL: Real interruptions have LOUD user speech
        // When someone genuinely interrupts, they speak LOUDER than normal to be heard over the NPC
        private const float INTERRUPTION_VOLUME_THRESHOLD = 0.15f; // Minimum mic RMS for real interruption
        private const float LOUD_SPEECH_RATIO = 1.2f; // User speaking 120% of NPC volume = likely real interruption

        // Buffers
        private readonly Queue<float> _npcAudioBuffer = new Queue<float>();
        private readonly Queue<float> _micAudioBuffer = new Queue<float>();
        private readonly List<float> _npcHistory = new List<float>(); // Longer history for delay compensation

        // State
        private float _lastNpcRMS;
        private float _lastMicRMS;
        private float _lastCorrelation;
        private bool _lastDetectionResult;
        private int _sampleRate = 48000;

        // Debug
        private readonly bool _enableDebugLogging = true;
        private int _frameCounter;
        private int _detectionCount;
        private int _processCount;

        public AudioFeedbackDetector(int sampleRate = 48000)
        {
            _sampleRate = sampleRate;
            Debug.Log($"[AudioFeedbackDetector] Initialized with sample rate: {sampleRate}");
        }

        /// <summary>
        /// Process audio frames and detect if mic input contains NPC audio (feedback).
        /// </summary>
        /// <param name="micAudio">Current microphone audio frame</param>
        /// <param name="npcAudio">Current NPC audio being played (can be null if NPC silent)</param>
        /// <returns>Confidence score (0-1) that mic contains feedback. > 0.5 suggests feedback.</returns>
        public float DetectFeedback(float[] micAudio, float[] npcAudio)
        {
            _processCount++;

            if (micAudio == null || micAudio.Length == 0)
            {
                if (_processCount <= 5 && _enableDebugLogging)
                {
                    Debug.Log($"[AudioFeedbackDetector] Process #{_processCount} - No mic audio provided");
                }
                return 0f;
            }

            // Update buffers
            AddToBuffer(_micAudioBuffer, micAudio, BUFFER_SIZE);

            if (npcAudio != null && npcAudio.Length > 0)
            {
                AddToBuffer(_npcAudioBuffer, npcAudio, BUFFER_SIZE);
                AddToHistory(npcAudio);
            }

            // Need enough data for correlation
            if (_micAudioBuffer.Count < CORRELATION_WINDOW || _npcHistory.Count < CORRELATION_WINDOW * 2)
            {
                return 0f;
            }

            // Calculate RMS for both signals
            var micRMS = CalculateRMS(_micAudioBuffer.ToArray());
            var npcRMS = CalculateRMS(_npcAudioBuffer.ToArray());

            _lastMicRMS = micRMS;
            _lastNpcRMS = npcRMS;

            // Quick check: If NPC is silent, no feedback possible
            if (npcRMS < 0.001f)
            {
                if ((_frameCounter % 50 == 0 || _processCount <= 10) && _enableDebugLogging)
                {
                    Debug.Log($"[AudioFeedbackDetector] #{_processCount} NPC silent (RMS: {npcRMS:F6}), no feedback possible");
                }
                return 0f;
            }

            // Quick check: If mic is too quiet relative to NPC, likely no feedback
            var volumeRatio = micRMS / (npcRMS + 0.0001f); // Avoid division by zero
            if (volumeRatio < VOLUME_RATIO_MIN)
            {
                if ((_frameCounter % 50 == 0 || _processCount <= 10) && _enableDebugLogging)
                {
                    Debug.Log($"[AudioFeedbackDetector] #{_processCount} Mic too quiet. Ratio: {volumeRatio:F3} (mic: {micRMS:F6}, npc: {npcRMS:F6}, threshold: {VOLUME_RATIO_MIN})");
                }
                return 0f;
            }

            // CRITICAL CHECK: Is user speaking LOUD enough for real interruption?
            // Real interruptions require speaking OVER the NPC, which means LOUD speech
            var isLoudEnoughForInterruption = micRMS > INTERRUPTION_VOLUME_THRESHOLD;
            var isSpeakingOverNPC = volumeRatio > LOUD_SPEECH_RATIO;

            if (isSpeakingOverNPC)
            {
                // User is speaking LOUDER than NPC - this is likely REAL interruption, not feedback!
                if ((_frameCounter % 25 == 0 || _processCount <= 10) && _enableDebugLogging)
                {
                    Debug.Log($"[AudioFeedbackDetector] #{_processCount} 🔊 USER SPEAKING OVER NPC! Volume ratio: {volumeRatio:F3} (mic: {micRMS:F6}, npc: {npcRMS:F6}) - REAL INTERRUPTION");
                }
                return 0f; // Return 0 confidence - this is NOT feedback
            }

            // If mic is almost as loud as NPC (but not louder), suspicious but need correlation check
            if (volumeRatio > VOLUME_RATIO_MAX)
            {
                if (_frameCounter % 50 == 0 && _enableDebugLogging)
                {
                    Debug.Log($"[AudioFeedbackDetector] Mic loud but not over NPC. Ratio: {volumeRatio:F3} - checking correlation");
                }
                // Don't return here - still check correlation
            }

            // Perform correlation with delay compensation
            var maxCorrelation = 0f;
            var bestDelay = 0;

            // Try different delays (0 to DELAY_MAX_MS)
            var maxDelaySamples = (int)(DELAY_MAX_MS * _sampleRate / 1000f);
            var micArray = _micAudioBuffer.Take(CORRELATION_WINDOW).ToArray();

            for (var delay = 0; delay < Math.Min(maxDelaySamples, _npcHistory.Count - CORRELATION_WINDOW); delay += 10)
            {
                var npcArray = _npcHistory.Skip(delay).Take(CORRELATION_WINDOW).ToArray();
                if (npcArray.Length < CORRELATION_WINDOW)
                    continue;

                var correlation = CalculateCorrelation(micArray, npcArray);
                if (correlation > maxCorrelation)
                {
                    maxCorrelation = correlation;
                    bestDelay = delay;
                }
            }

            _lastCorrelation = maxCorrelation;

            // Calculate confidence score based on correlation and volume ratio
            var confidence = 0f;

            if (maxCorrelation > CORRELATION_THRESHOLD)
            {
                // High correlation detected

                // Special case: User is loud enough for interruption
                if (isLoudEnoughForInterruption && micRMS > 0.12f)
                {
                    // Even with correlation, if user is speaking LOUD, it's likely real speech
                    // People naturally speak louder when interrupting
                    confidence = maxCorrelation * 0.3f; // Low confidence - probably real speech

                    if (_frameCounter % 25 == 0 && _enableDebugLogging)
                    {
                        Debug.Log($"[AudioFeedbackDetector] High correlation BUT loud user speech (RMS: {micRMS:F3}) - likely REAL interruption");
                    }
                }
                // Adjust confidence based on volume ratio (feedback is usually quieter)
                else if (volumeRatio >= VOLUME_RATIO_MIN && volumeRatio <= 0.7f)
                {
                    // Perfect feedback signature: correlated and quieter
                    confidence = maxCorrelation * 0.9f;
                }
                else if (volumeRatio > 0.7f && volumeRatio <= 1.0f)
                {
                    // Might be actual speech that happens to correlate
                    // But not loud enough to be confident interruption
                    confidence = maxCorrelation * 0.6f;
                }
            }

            // Debug logging
            _frameCounter++;
            var isFeedback = confidence > 0.5f;

            // Always log first 10 calls and when feedback is detected
            if ((_processCount <= 10 || isFeedback || (_frameCounter % 25 == 0 && maxCorrelation > 0.3f)) && _enableDebugLogging)
            {
                if (isFeedback)
                {
                    _detectionCount++;
                    Debug.LogWarning($"[AudioFeedbackDetector] #{_processCount} ⚠️ FEEDBACK DETECTED #{_detectionCount}! " +
                                   $"Correlation: {maxCorrelation:F3}, Delay: {bestDelay}samples, " +
                                   $"VolumeRatio: {volumeRatio:F3}, Confidence: {confidence:F3}");
                }
                else if (_processCount <= 10 || maxCorrelation > 0.3f)
                {
                    Debug.Log($"[AudioFeedbackDetector] #{_processCount} Analysis - " +
                             $"Correlation: {maxCorrelation:F3}, VolumeRatio: {volumeRatio:F3}, " +
                             $"MicRMS: {micRMS:F6}, NpcRMS: {npcRMS:F6}, " +
                             $"Confidence: {confidence:F3} => No feedback");
                }
            }

            _lastDetectionResult = confidence > 0.5f;
            return confidence;
        }

        /// <summary>
        /// Simple check if feedback is currently detected.
        /// </summary>
        public bool IsFeedbackDetected()
        {
            return _lastDetectionResult;
        }

        /// <summary>
        /// Get detailed info for debugging.
        /// </summary>
        public string GetDebugInfo()
        {
            return $"MicRMS: {_lastMicRMS:F6}, NpcRMS: {_lastNpcRMS:F6}, " +
                   $"Correlation: {_lastCorrelation:F3}, Feedback: {_lastDetectionResult}";
        }

        /// <summary>
        /// Reset detector state (e.g., when starting new session).
        /// </summary>
        public void Reset()
        {
            var hadData = _npcAudioBuffer.Count > 0 || _micAudioBuffer.Count > 0;

            _npcAudioBuffer.Clear();
            _micAudioBuffer.Clear();
            _npcHistory.Clear();
            _lastNpcRMS = 0f;
            _lastMicRMS = 0f;
            _lastCorrelation = 0f;
            _lastDetectionResult = false;
            _frameCounter = 0;
            _processCount = 0;
            _detectionCount = 0;

            Debug.Log($"[AudioFeedbackDetector] 🔄 RESET - Cleared buffers (had data: {hadData}), " +
                     $"Ready for new PTT session");
        }

        // Helper methods

        private void AddToBuffer(Queue<float> buffer, float[] data, int maxSize)
        {
            foreach (var sample in data)
            {
                buffer.Enqueue(sample);
            }

            while (buffer.Count > maxSize)
            {
                buffer.Dequeue();
            }
        }

        private void AddToHistory(float[] data)
        {
            _npcHistory.AddRange(data);

            // Keep only last 200ms of history
            var maxHistorySize = (int)(0.2f * _sampleRate);
            if (_npcHistory.Count > maxHistorySize)
            {
                _npcHistory.RemoveRange(0, _npcHistory.Count - maxHistorySize);
            }
        }

        private float CalculateRMS(float[] data)
        {
            if (data == null || data.Length == 0)
                return 0f;

            var sum = 0f;
            for (var i = 0; i < data.Length; i++)
            {
                sum += data[i] * data[i];
            }
            return Mathf.Sqrt(sum / data.Length);
        }

        private float CalculateCorrelation(float[] signal1, float[] signal2)
        {
            if (signal1.Length != signal2.Length || signal1.Length == 0)
                return 0f;

            // Normalize signals for correlation
            var mean1 = signal1.Average();
            var mean2 = signal2.Average();

            var norm1 = new float[signal1.Length];
            var norm2 = new float[signal2.Length];

            for (var i = 0; i < signal1.Length; i++)
            {
                norm1[i] = signal1[i] - mean1;
                norm2[i] = signal2[i] - mean2;
            }

            // Calculate correlation coefficient
            var numerator = 0f;
            var sum1Sq = 0f;
            var sum2Sq = 0f;

            for (var i = 0; i < norm1.Length; i++)
            {
                numerator += norm1[i] * norm2[i];
                sum1Sq += norm1[i] * norm1[i];
                sum2Sq += norm2[i] * norm2[i];
            }

            var denominator = Mathf.Sqrt(sum1Sq * sum2Sq);
            if (denominator < 0.0001f)
                return 0f;

            return Mathf.Abs(numerator / denominator);
        }
    }
}