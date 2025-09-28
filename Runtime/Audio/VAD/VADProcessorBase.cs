using System;
using UnityEngine;

namespace Tsc.AIBridge.Audio.VAD
{
    /// <summary>
    /// Base class for Voice Activity Detection processors
    /// Provides common interface and shared functionality for all VAD implementations
    ///
    /// ARCHITECTURE NOTE: All VAD logic MUST extend this class
    /// DO NOT implement RMS/ZCR calculations elsewhere - use these utilities
    /// See Unity/ARCHITECTURE.md for complete audio pipeline overview
    /// </summary>
    public abstract class VADProcessorBase : IDisposable
    {
        // Universal constants for all VAD processors
        /// <summary>
        /// Standard pause threshold for speech detection (300ms)
        /// Used to ignore brief pauses between words while maintaining natural pause detection
        /// This value is synchronized with InterruptionDetectorCore.NPC_PAUSE_TOLERANCE
        /// </summary>
        protected const float SpeechPauseThreshold = 0.3f; // 300ms

        // Configuration
        protected readonly string Name;
        protected readonly bool EnableVerboseLogging;

        // State
        protected bool CurrentlySpeaking;
        protected DateTime LastActivityTime = DateTime.Now;
        protected float SilenceTimer; // Common to all VAD processors
        protected float[] LastAudioFrame; // Store last processed audio for feedback detection

        /// <summary>
        /// Current speech detection state
        /// </summary>
        public bool IsSpeaking => CurrentlySpeaking;

        /// <summary>
        /// Time since last speech activity (in seconds)
        /// </summary>
        public float TimeSinceLastActivity => (float)(DateTime.Now - LastActivityTime).TotalSeconds;

        /// <summary>
        /// Current detection threshold (abstract - each implementation defines this differently)
        /// </summary>
        public abstract float CurrentThreshold { get; }

        /// <summary>
        /// Get the last processed audio frame for feedback detection
        /// </summary>
        public float[] GetLastAudioFrame() => LastAudioFrame;

        protected VADProcessorBase(string name, bool isVerboseLogging = false)
        {
            Name = name;
            EnableVerboseLogging = isVerboseLogging;
        }

        /// <summary>
        /// Process audio frame and return speech detection result
        /// </summary>
        /// <param name="audioData">Audio samples</param>
        /// <param name="additionalContext">Optional additional context (e.g., PTT duration)</param>
        /// <returns>True if speech is detected</returns>
        public abstract bool ProcessAudioFrame(float[] audioData, float additionalContext = 0f);

        /// <summary>
        /// Common speech detection logic with pause tolerance
        /// Used by derived classes to handle volume-based detection
        /// </summary>
        /// <param name="volumeAboveThreshold">Whether current volume exceeds threshold</param>
        /// <param name="deltaTime">Time since last frame</param>
        /// <returns>True if speech is detected</returns>
        protected bool ProcessSpeechDetection(bool volumeAboveThreshold, float deltaTime)
        {
            if (volumeAboveThreshold)
            {
                // Audio detected - reset silence timer
                SilenceTimer = 0f;
                if (!CurrentlySpeaking)
                {
                    SetSpeakingState(true);
                }
            }
            else
            {
                // No audio - accumulate silence time
                SilenceTimer += deltaTime;

                // Stop speaking only after pause threshold exceeded
                if (CurrentlySpeaking && SilenceTimer > SpeechPauseThreshold)
                {
                    SetSpeakingState(false);
                }
            }

            return CurrentlySpeaking;
        }

        /// <summary>
        /// Reset VAD state (useful when starting new sessions)
        /// </summary>
        public virtual void Reset()
        {
            CurrentlySpeaking = false;
            LastActivityTime = DateTime.Now;
            SilenceTimer = 0f;
            if (EnableVerboseLogging)
                Debug.Log($"[{Name}VAD] Reset");
        }

        /// <summary>
        /// Calculate RMS (Root Mean Square) for volume level
        /// Common utility for all VAD processors
        /// </summary>
        protected float CalculateRMS(float[] audioData)
        {
            if (audioData == null || audioData.Length == 0)
                return 0f;

            var sum = 0f;
            for (var i = 0; i < audioData.Length; i++)
            {
                sum += audioData[i] * audioData[i];
            }
            return Mathf.Sqrt(sum / audioData.Length);
        }

        /// <summary>
        /// Calculate Zero Crossing Rate for frequency analysis
        /// Common utility for advanced VAD processors
        /// </summary>
        protected float CalculateZcr(float[] audioData)
        {
            if (audioData == null || audioData.Length < 2)
                return 0f;

            var crossings = 0;
            for (var i = 1; i < audioData.Length; i++)
            {
                if ((audioData[i] >= 0) != (audioData[i - 1] >= 0))
                {
                    crossings++;
                }
            }
            return (float)crossings / audioData.Length;
        }

        /// <summary>
        /// Update speaking state with logging
        /// </summary>
        protected void SetSpeakingState(bool isSpeaking, float confidence = 1.0f)
        {
            if (CurrentlySpeaking != isSpeaking)
            {
                // Only log state changes when verbose logging is enabled
                if (EnableVerboseLogging)
                {
                    Debug.Log($"[{Name}VAD-STATE-CHANGE] *** SPEAKING STATE CHANGING: {CurrentlySpeaking} -> {isSpeaking} ***");
                    Debug.Log($"[{Name}VAD-STATE-CHANGE] Silence timer: {SilenceTimer:F3}s, Confidence: {confidence:P0}");
                }

                CurrentlySpeaking = isSpeaking;
                if (isSpeaking)
                {
                    LastActivityTime = DateTime.Now;
                    if (EnableVerboseLogging)
                        Debug.Log($"[{Name}VAD] Speech started (confidence: {confidence:P0})");
                }
                else
                {
                    if (EnableVerboseLogging)
                        Debug.Log($"[{Name}VAD] Speech stopped");
                }
            }
        }

        /// <summary>
        /// Dispose of any resources used by the VAD processor
        /// </summary>
        public virtual void Dispose()
        {
            // Base implementation does nothing
            // Derived classes can override if they have resources to dispose
            if (EnableVerboseLogging)
                Debug.Log($"[{Name}VAD] Disposed");
        }
    }
}