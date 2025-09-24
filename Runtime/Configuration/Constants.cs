namespace SimulationCrew.AIBridge.Configuration
{
    /// <summary>
    /// Central configuration for network and WebSocket constants
    /// </summary>
    public static class Constants
    {
        // WebSocket Configuration
        public const float IdleCheckInterval = 10f;
        
        // Session Configuration
        public const float SendLoopDelay = 0.01f;
        
        // Default Values
        //public const string DefaultLanguageCode = "nl-NL";
        public const string DefaultPlayerRole = "player";

        //Streaming Configuration
        //public const string VerboseLoggingFormat = "[{0}] {1}";

        // Audio Configuration
        //public const float SilenceThreshold = 0.01f; // Threshold for silence detection
    }
}
