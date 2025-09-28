using System;

namespace Tsc.AIBridge.Core
{
    /// <summary>
    /// Interface for WebSocket connection operations.
    /// Abstracts the WebSocket implementation for testability.
    /// </summary>
    public interface IWebSocketConnection
    {
        bool IsConnected { get; }
        void SendMessage(string message);
        void SendBinaryData(byte[] data);
        void Connect(string url);
        void Disconnect();

        // Events
        event Action<string> OnTextMessageReceived;
        event Action<byte[]> OnBinaryMessageReceived;
        event Action OnConnected;
        event Action OnDisconnected;
        event Action<string> OnError;
    }
}