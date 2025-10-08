using System;
using UnityEngine;
using NativeWebSocket;

namespace Tsc.AIBridge.WebSocket
{
    /// <summary>
    /// Enhanced WebSocket wrapper that properly separates binary and text messages.
    /// Fixes the critical bug where binary audio data gets corrupted by being treated as JSON.
    ///
    /// The underlying NativeWebSocket library has separate WebSocketMessageType.Text and
    /// WebSocketMessageType.Binary internally, but exposes them through a single OnMessage event.
    /// This wrapper restores the proper separation using robust message type detection.
    /// </summary>
    public class EnhancedWebSocket : IDisposable
    {
        private NativeWebSocket.WebSocket _webSocket;
        private bool _isReceivingAudioStream;

        // Separate events for proper message type handling
        public event Action OnOpen;
        public event Action<string> OnTextMessage;     // JSON messages
        public event Action<byte[]> OnBinaryMessage;   // Audio data
        public event Action<string> OnError;
        public event Action<WebSocketCloseCode> OnClose;

        /// <summary>
        /// Gets the current state of the underlying WebSocket connection.
        /// </summary>
        public WebSocketState State => _webSocket?.State ?? WebSocketState.Closed;

        /// <summary>
        /// Initializes a new instance of the EnhancedWebSocket wrapper.
        /// Sets up proper event forwarding with binary/text message separation.
        /// </summary>
        /// <param name="url">The WebSocket URL to connect to</param>
        public EnhancedWebSocket(string url)
        {
            _webSocket = new NativeWebSocket.WebSocket(url);

            // Set up event forwarding with proper message type separation
            _webSocket.OnOpen += () => OnOpen?.Invoke();
            _webSocket.OnMessage += HandleMessage;
            _webSocket.OnError += (error) => OnError?.Invoke(error);
            _webSocket.OnClose += (code) => OnClose?.Invoke(code);
        }

        /// <summary>
        /// Robust message type detection that fixes the binary/text corruption bug.
        /// Uses multiple detection methods to reliably distinguish between JSON and binary data.
        /// Handles fragmented audio streams correctly.
        /// </summary>
        /// <param name="data">Raw message data received from WebSocket</param>
        private void HandleMessage(byte[] data)
        {
            if (data == null || data.Length == 0)
                return;

            // Debug logging to see what messages we receive
            //var bytesToShow = Math.Min(8, data.Length);
            //var firstBytesArray = new string[bytesToShow];
            //for (int i = 0; i < bytesToShow; i++)
            //{
            //    firstBytesArray[i] = $"0x{data[i]:X2}";
            //}
            //var firstBytes = string.Join(" ", firstBytesArray);
            //Debug.Log($"[EnhancedWebSocket] Received message: {data.Length} bytes, first bytes: {firstBytes}");

            // Check if this is the start of a new Ogg stream
            if (IsOggAudioData(data))
            {
                // Definitely binary audio data - start of stream
                //Debug.Log($"[EnhancedWebSocket] Detected OGG audio stream start (OggS header)");
                _isReceivingAudioStream = true;
                OnBinaryMessage?.Invoke(data);
            }
            else if (IsJsonMessage(data))
            {
                // Definitely JSON text message
                try
                {
                    var json = System.Text.Encoding.UTF8.GetString(data);
                    // Only log important state changes
                    //if (json.Contains("\"type\":\"AudioStreamStart\"") || json.Contains("\"type\":\"AudioStreamEnd\""))
                    //{
                    //    Debug.Log($"[EnhancedWebSocket] {(json.Contains("Start") ? "Audio stream starting" : "Audio stream ended")}");
                    //}

                    // Check if this is AudioStreamEnd to stop treating binary as audio
                    if (json.Contains("\"type\":\"AudioStreamEnd\""))
                    {
                        _isReceivingAudioStream = false;
                    }
                    // Check if this is AudioStreamStart to prepare for audio
                    else if (json.Contains("\"type\":\"AudioStreamStart\""))
                    {
                        _isReceivingAudioStream = true;
                    }

                    OnTextMessage?.Invoke(json);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[EnhancedWebSocket] Failed to decode JSON message: {ex.Message}");
                }
            }
            else if (_isReceivingAudioStream)
            {
                // We're in the middle of an audio stream - treat as binary continuation
                //Debug.Log($"[EnhancedWebSocket] Continuing audio stream (binary data during stream)");
                OnBinaryMessage?.Invoke(data);
            }
            else
            {
                // Unknown data and not in audio stream - default to binary for safety
                //Debug.Log($"[EnhancedWebSocket] Unknown data type, not OGG/JSON, _isReceivingAudioStream={_isReceivingAudioStream} - defaulting to binary");
                OnBinaryMessage?.Invoke(data);
            }
        }

        /// <summary>
        /// Detects Ogg audio data by checking for the standard Ogg magic number.
        /// This is 100% reliable for Ogg/Opus audio streams.
        /// </summary>
        /// <param name="data">Byte array to check for Ogg header</param>
        /// <returns>True if data starts with "OggS" magic number, false otherwise</returns>
        private static bool IsOggAudioData(byte[] data)
        {
            // Ogg pages always start with "OggS" magic number
            return data.Length >= 4 &&
                   data[0] == 'O' &&
                   data[1] == 'g' &&
                   data[2] == 'g' &&
                   data[3] == 'S';
        }

        /// <summary>
        /// Detects JSON messages using multiple validation methods.
        /// More robust than just checking for '{' which can appear in binary data.
        /// </summary>
        /// <param name="data">Byte array to validate as JSON</param>
        /// <returns>True if data is valid JSON with a 'type' field, false otherwise</returns>
        private static bool IsJsonMessage(byte[] data)
        {
            if (data.Length < 2)
                return false;

            // Quick rejection for obviously non-JSON data
            if (data[0] != '{' && data[0] != '[')
                return false;

            try
            {
                var text = System.Text.Encoding.UTF8.GetString(data);

                // Additional validation: must contain type field (our message protocol)
                return text.Contains("\"type\":") || text.Contains("'type':");
            }
            catch
            {
                // UTF-8 decode failed = definitely not JSON
                return false;
            }
        }

        /// <summary>
        /// Establishes the WebSocket connection asynchronously.
        /// </summary>
        /// <returns>Task representing the asynchronous connect operation</returns>
        public async System.Threading.Tasks.Task ConnectAsync()
        {
            await _webSocket.Connect();
        }

        /// <summary>
        /// Closes the WebSocket connection gracefully.
        /// </summary>
        /// <returns>Task representing the asynchronous close operation</returns>
        public async System.Threading.Tasks.Task CloseAsync()
        {
            await _webSocket.Close();
        }

        /// <summary>
        /// Sends binary data through the WebSocket connection.
        /// </summary>
        /// <param name="data">Binary data to send</param>
        /// <returns>Task representing the asynchronous send operation</returns>
        public async System.Threading.Tasks.Task SendAsync(byte[] data)
        {
            await _webSocket.Send(data);
        }

        /// <summary>
        /// Sends text data through the WebSocket connection.
        /// </summary>
        /// <param name="text">Text message to send</param>
        /// <returns>Task representing the asynchronous send operation</returns>
        public async System.Threading.Tasks.Task SendTextAsync(string text)
        {
            // CRITICAL DEBUG: Log before/after NativeWebSocket.SendText
            var messageType = text.Contains("\"type\":") ? text.Substring(text.IndexOf("\"type\":") + 8, 20) : "unknown";
            Debug.LogError($"[DEBUG-ENHANCED-WS] SendTextAsync BEFORE NativeWebSocket.SendText - type: {messageType}, state: {State}");

            await _webSocket.SendText(text);

            Debug.LogError($"[DEBUG-ENHANCED-WS] SendTextAsync AFTER NativeWebSocket.SendText completed - type: {messageType}");
        }

        /// <summary>
        /// Dispatches queued messages on platforms that require manual message processing.
        /// This is typically called from Unity's Update loop for WebGL builds.
        /// </summary>
        public void DispatchMessageQueue()
        {
            _webSocket?.DispatchMessageQueue();
        }

        /// <summary>
        /// Releases all resources used by the EnhancedWebSocket.
        /// Closes the connection and cleans up the underlying WebSocket.
        /// </summary>
        public void Dispose()
        {
            _webSocket?.Close();
            _webSocket = null;
        }
    }
}