using Newtonsoft.Json;

namespace Tsc.AIBridge.Messages
{
    /// <summary>
    /// Message sent from Unity to the backend to FREEZE the player's vocal-delivery (prosody) baseline
    /// for the active session. After this the backend stops folding new turns into the rolling reference
    /// and measures all subsequent turns against the reference captured so far.
    ///
    /// Sent by the RuleSystem at the warmup→escalation transition (only the content creator knows that
    /// moment), so a fixed escalation threshold stays meaningful and sustained escalation is not
    /// normalised away. The backend no-ops if prosody is off or the session is unknown.
    /// </summary>
    public class FreezeProsodyBaselineMessage
    {
        /// <summary>Message type identifier (always "FreezeProsodyBaseline").</summary>
        [JsonProperty("type")]
        public string Type { get; set; } = WebSocketMessageTypes.FreezeProsodyBaseline;

        /// <summary>Request ID of the active session whose baseline to freeze.</summary>
        [JsonProperty("requestId")]
        public string RequestId { get; set; } = string.Empty;
    }
}
