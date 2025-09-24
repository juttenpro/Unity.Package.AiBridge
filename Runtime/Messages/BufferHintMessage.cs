using Newtonsoft.Json;

namespace SimulationCrew.AIBridge.Messages
{
    /// <summary>
    /// Represents a buffer hint message from the backend for adaptive audio buffering.
    /// Used to adjust Unity's audio buffer size based on network quality and TTS latency.
    ///
    /// Critical for VR applications to prevent audio underruns during poor network conditions.
    /// </summary>
    public class BufferHintMessage
    {
        /// <summary>
        /// Message type identifier (always "BufferHint")
        /// </summary>
        [JsonProperty("type")]
        public string Type { get; set; } = "BufferHint";

        /// <summary>
        /// Unique request ID for this session
        /// </summary>
        [JsonProperty("requestId")]
        public string RequestId { get; set; } = string.Empty;

        /// <summary>
        /// TTS latency in milliseconds (time to first audio chunk)
        /// </summary>
        [JsonProperty("ttsLatencyMs")]
        public double TtsLatencyMs { get; set; }

        /// <summary>
        /// Latency level classification (Fast, Normal, Slow)
        /// </summary>
        [JsonProperty("latencyLevel")]
        public string LatencyLevel { get; set; } = string.Empty;

        /// <summary>
        /// Recommended buffer size (Small, Medium, Large)
        /// </summary>
        [JsonProperty("recommendedBufferSize")]
        public string RecommendedBufferSize { get; set; } = string.Empty;

        /// <summary>
        /// Network quality assessment (Excellent, Good, Fair, Poor)
        /// </summary>
        [JsonProperty("networkQuality")]
        public string NetworkQuality { get; set; } = string.Empty;

        /// <summary>
        /// Sentence index for this buffer hint (-1 for initial measurement)
        /// </summary>
        [JsonProperty("sentenceIndex")]
        public int SentenceIndex { get; set; }
    }
}