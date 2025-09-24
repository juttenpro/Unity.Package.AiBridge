using System;
using System.Threading.Tasks;

namespace SimulationCrew.AIBridge.WebSocket
{
    /// <summary>
    /// Event arguments for connection failure events
    /// </summary>
    public class ConnectionFailedEventArgs : EventArgs
    {
        public ConnectionFailureReason Reason { get; }
        public string ErrorMessage { get; }
        public Exception Exception { get; }

        public ConnectionFailedEventArgs(ConnectionFailureReason reason, string errorMessage, Exception exception = null)
        {
            Reason = reason;
            ErrorMessage = errorMessage ?? "Unknown error";
            Exception = exception;
        }
    }

    /// <summary>
    /// Reasons why a connection attempt might fail
    /// </summary>
    public enum ConnectionFailureReason
    {
        /// <summary>
        /// Connection attempt was cancelled by user
        /// </summary>
        Cancelled,

        /// <summary>
        /// Network error prevented connection
        /// </summary>
        NetworkError,

        /// <summary>
        /// Authentication failed (JWT token issue)
        /// </summary>
        AuthenticationError,

        /// <summary>
        /// Connection timeout
        /// </summary>
        Timeout,

        /// <summary>
        /// Unknown failure reason
        /// </summary>
        Unknown
    }

    /// <summary>
    /// Reasons why an established connection might be lost
    /// </summary>
    public enum DisconnectionReason
    {
        /// <summary>
        /// Clean disconnect requested by client
        /// </summary>
        ClientRequested,

        /// <summary>
        /// Server closed the connection
        /// </summary>
        ServerClosed,

        /// <summary>
        /// Network failure caused disconnect
        /// </summary>
        NetworkFailure,

        /// <summary>
        /// Connection timed out
        /// </summary>
        Timeout,

        /// <summary>
        /// Unknown disconnection reason
        /// </summary>
        Unknown
    }
}