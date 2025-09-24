using System;
using UnityEngine;

namespace SimulationCrew.AIBridge.Data
{
    /// <summary>
    /// Container for conversation latency statistics and performance metrics.
    /// Tracks both individual component latencies and overall performance.
    /// Used for performance monitoring, debugging, and UI display.
    /// </summary>
    public class LatencyStats
    {
        /// <summary>
        /// Gets or sets the most recent end-to-end latency measurement in milliseconds.
        /// Measured from user releasing PTT to AI starting to speak.
        /// </summary>
        public long LastEndToEndLatency { get; set; }
        
        /// <summary>
        /// Gets or sets the average latency across all measurements in milliseconds.
        /// </summary>
        public double AverageLatency { get; set; }
        
        /// <summary>
        /// Gets or sets the minimum latency recorded in milliseconds.
        /// </summary>
        public long MinLatency { get; set; }
        
        /// <summary>
        /// Gets or sets the maximum latency recorded in milliseconds.
        /// </summary>
        public long MaxLatency { get; set; }
        
        /// <summary>
        /// Gets or sets the number of latency samples collected.
        /// </summary>
        public int SampleCount { get; set; }

        /// <summary>
        /// Gets or sets the Cloud Run cold start latency in milliseconds.
        /// Only relevant for the first request after deployment.
        /// </summary>
        public long LastBootLatency { get; set; }
        
        /// <summary>
        /// Gets or sets the LLM processing latency in milliseconds.
        /// Time taken for the AI model to generate a response.
        /// </summary>
        public long LastLlmLatency { get; set; }
        
        /// <summary>
        /// Gets or sets the Speech-to-Text latency in milliseconds.
        /// Time taken to transcribe user's speech.
        /// </summary>
        public long LastSttLatency { get; set; }
        
        /// <summary>
        /// Gets or sets the Push-To-Talk duration in milliseconds.
        /// How long the user held the PTT button.
        /// </summary>
        public long LastPttDuration { get; set; }
        
        /// <summary>
        /// Gets or sets the Text-to-Speech latency in milliseconds.
        /// Time taken to generate audio from text.
        /// </summary>
        public long LastTtsLatency { get; set; }
        
        /// <summary>
        /// Gets or sets the TTS latency level description.
        /// E.g. "Fast", "Normal", "Slow"
        /// </summary>
        public string TtsLatencyLevel { get; set; }

        /// <summary>
        /// Gets the first audio latency, which equals the perceived end-to-end latency.
        /// This is what the user experiences as response time.
        /// </summary>
        public long FirstAudioLatency => LastEndToEndLatency;

        /// <summary>
        /// Gets or sets the persona name associated with these stats.
        /// </summary>
        public string PersonaName { get; set; }
        
        /// <summary>
        /// Gets or sets the timestamp of the last stat update.
        /// </summary>
        public DateTime LastUpdateTime { get; set; }

        /// <summary>
        /// Gets a color representation of the current latency for UI visualization.
        /// Based on perceived latency thresholds.
        /// </summary>
        /// <returns>Green for excellent, yellow for good, orange for fair, red for poor</returns>
        public Color GetLatencyColor()
        {
            // Base op First Audio latency, niet end-to-end
            if (FirstAudioLatency < 1000) return Color.green;
            if (FirstAudioLatency < 1500) return Color.yellow;
            if (FirstAudioLatency < 2000) return new Color(1f, 0.6f, 0.1f);
            return new Color(1f, 0.4f, 0.4f);
        }
        
        /// <summary>
        /// Gets a text category describing the current latency performance.
        /// </summary>
        /// <returns>Performance category: Excellent/Good/Fair/Poor</returns>
        public string GetLatencyCategory()
        {
            // Base op First Audio latency
            if (FirstAudioLatency < 1000) return "Excellent";
            if (FirstAudioLatency < 1500) return "Good";
            if (FirstAudioLatency < 2000) return "Fair";
            return "Poor";
        }

        public string FormattedFirstAudio => $"{FirstAudioLatency}ms";
        public string FormattedLastLatency => $"{LastEndToEndLatency}ms";
        public string FormattedAverageLatency => $"{AverageLatency:F0}ms";
        public string FormattedRange => $"{MinLatency}-{MaxLatency}ms";

        // Breakdown formatting with TTS
        public string FormattedBreakdown => LastTtsLatency > 0 
            ? $"STT:{LastSttLatency}ms LLM:{LastLlmLatency}ms TTS:{LastTtsLatency}ms"
            : $"STT:{LastSttLatency}ms LLM:{LastLlmLatency}ms";
    }

    /// <summary>
    /// Represents network connection quality levels.
    /// Used for adaptive quality settings and user feedback.
    /// </summary>
    public enum NetworkQuality
    {
        /// <summary>
        /// Excellent network conditions (< 1s latency)
        /// </summary>
        Excellent,
        
        /// <summary>
        /// Good network conditions (1-1.5s latency)
        /// </summary>
        Good,
        
        /// <summary>
        /// Fair network conditions (1.5-2s latency)
        /// </summary>
        Fair,
        
        /// <summary>
        /// Poor network conditions (> 2s latency)
        /// </summary>
        Poor
    }
    
}
