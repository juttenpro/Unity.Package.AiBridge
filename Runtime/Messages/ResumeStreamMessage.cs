using Newtonsoft.Json;

namespace Tsc.AIBridge.Messages
{
    /// <summary>
    /// Message sent from Unity to backend to resume a paused audio stream.
    /// Backend will continue TTS generation and audio transmission from where it left off.
    ///
    /// Must be paired with a previous PauseStream message for the same requestId.
    /// </summary>
    public class ResumeStreamMessage
    {
        /// <summary>
        /// Message type identifier (always "ResumeStream")
        /// </summary>
        [JsonProperty("type")]
        public string Type { get; set; } = "ResumeStream";

        /// <summary>
        /// Request ID of the paused stream to resume
        /// </summary>
        [JsonProperty("requestId")]
        public string RequestId { get; set; } = string.Empty;
    }
}
