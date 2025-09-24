namespace SimulationCrew.AIBridge.Core
{
    /// <summary>
    /// Represents the state of the WebSocket connection used for real-time audio streaming.
    /// This enum is used by ConnectionManager to track and manage the connection lifecycle.
    /// </summary>
    public enum ConnectionState
    {
        /// <summary>
        /// No connection, ready to connect
        /// </summary>
        Disconnected,
        
        /// <summary>
        /// Currently establishing connection
        /// </summary>
        Connecting,
        
        /// <summary>
        /// Connected and ready to send/receive
        /// </summary>
        Connected,
        
        /// <summary>
        /// Currently disconnecting
        /// </summary>
        Disconnecting,
        
        /// <summary>
        /// Error state, needs reset
        /// </summary>
        Error
    }
}
