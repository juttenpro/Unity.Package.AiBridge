namespace Tsc.AIBridge
{
    /// <summary>
    /// Whether AIBridge writes conversation content (transcripts, raw backend messages) to the Unity log. On by default,
    /// as before 5.20.0. An app that must keep speech out of its logs (medical context) turns it off once at start-up;
    /// timings, ids and error codes are still logged.
    /// Static on purpose: app-wide configuration, set once at start-up, never user data. Do not toggle it per turn.
    /// </summary>
    public static class ConversationContentLogging
    {
        public const string Hidden = "<content hidden>";

        public static bool Enabled { get; set; } = true;

        /// <summary>The text itself when content logging is on, otherwise a fixed placeholder.</summary>
        public static string Content(string text) => Enabled ? text : Hidden;
    }
}
