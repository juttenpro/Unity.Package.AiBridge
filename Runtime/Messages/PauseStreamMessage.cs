using Newtonsoft.Json;

namespace Tsc.AIBridge.Messages
{
    /// <summary>
    /// Message sent from Unity to backend to pause the current audio stream.
    /// Backend will stop TTS generation and audio transmission until ResumeStream is received.
    ///
    /// Use case: Training scenario pause, Unity Editor pause, or explicit user pause.
    /// </summary>
    public class PauseStreamMessage
    {
        /// <summary>
        /// Message type identifier (always "PauseStream")
        /// </summary>
        [JsonProperty("type")]
        public string Type { get; set; } = "PauseStream";

        /// <summary>
        /// Request ID of the active stream to pause
        /// </summary>
        [JsonProperty("requestId")]
        public string RequestId { get; set; } = string.Empty;

        /// <summary>
        /// Reason for pause (e.g., "EditorPause", "TrainingPause", "UserPause")
        /// Optional, for debugging purposes
        /// </summary>
        [JsonProperty("reason")]
        public string Reason { get; set; } = string.Empty;
    }
}
