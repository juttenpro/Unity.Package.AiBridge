using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using NativeWebSocket;
using Newtonsoft.Json;
using Tsc.AIBridge.Core;

namespace Tsc.AIBridge.WebSocket
{
    /// <summary>
    /// Low-level WebSocket connection handler using NativeWebSocket library.
    /// Manages WebSocket lifecycle, authentication, message transport, and reconnection logic.
    /// Key features:
    /// - JWT authentication via query parameters
    /// - Automatic reconnection with exponential backoff
    /// - Binary and text message handling
    /// - Thread-safe message dispatch to Unity main thread
    /// - Connection state tracking and error handling
    /// - Proper resource cleanup on disposal
    /// </summary>
    public class WebSocketConnection : IWebSocketConnection, IDisposable
    {
        private EnhancedWebSocket _webSocket;
        private CancellationTokenSource _cancellationTokenSource;
        private readonly MonoBehaviour _owner;
        //private readonly string _apiBaseUrl;
        private string _jwtToken;
        private bool _isDisconnecting;
        private readonly bool _isVerboseLogging;

        // Connection state
        public bool IsConnected => _webSocket?.State == WebSocketState.Open;
        public bool IsConnecting { get; private set; }

        // Events
        public event Action OnConnected;
        public event Action OnDisconnected;
        public event Action<string> OnError;
        public event Action<byte[]> OnBinaryMessageReceived;
        public event Action<string> OnTextMessageReceived;

        // Static events removed - were unused and causing compiler warnings

        // Configuration
        private readonly float _reconnectBaseDelay;
        private readonly float _reconnectMaxDelay;
        private readonly int _maxReconnectAttempts;
        private float _currentReconnectDelay;
        private int _reconnectAttempts;
        private bool _autoReconnectEnabled = true;
        private bool _isReconnecting;

        // Session preservation
        private string _lastWsUrl;
        private string _lastPersonaName;

        // Message tracking
        private int _binaryMessageCount;

        public WebSocketConnection(MonoBehaviour owner, /*string apiBaseUrl,*/ float reconnectBaseDelay = 1f, float reconnectMaxDelay = 30f, bool isVerboseLogging = false, int maxReconnectAttempts = 10)
        {
            _isVerboseLogging = isVerboseLogging;
            _owner = owner;
            //_apiBaseUrl = apiBaseUrl;
            _reconnectBaseDelay = reconnectBaseDelay;
            _reconnectMaxDelay = reconnectMaxDelay;
            _maxReconnectAttempts = maxReconnectAttempts;
            _currentReconnectDelay = reconnectBaseDelay;
        }

        /// <summary>
        /// Establishes WebSocket connection to the specified URL with JWT authentication.
        /// Waits for connection to complete with a 10-second timeout.
        /// </summary>
        /// <param name="wsUrl">Full WebSocket URL including query parameters</param>
        /// <param name="personaName">Name of the persona for logging</param>
        /// <param name="jwtToken">JWT token for authentication</param>
        /// <returns>True if connection was successfully established, false otherwise</returns>
        public async Task<bool> ConnectAsync(string wsUrl, string personaName, string jwtToken)
        {
            if (IsConnected || IsConnecting)
            {
                Debug.LogWarning($"[WebSocketConnection] Already connected or connecting");
                return false;
            }

            // Store for reconnection
            _lastWsUrl = wsUrl;
            _lastPersonaName = personaName;

            IsConnecting = true;
            _jwtToken = jwtToken;

            try
            {
                // Use the URL as-is (already contains all parameters)
                _webSocket = new EnhancedWebSocket(wsUrl);

                // Set up event handlers with proper binary/text separation
                _webSocket.OnOpen += HandleOpen;
                _webSocket.OnTextMessage += HandleTextMessage;
                _webSocket.OnBinaryMessage += HandleBinaryMessage;
                _webSocket.OnError += HandleError;
                _webSocket.OnClose += HandleClose;

                //Debug.Log($"[WebSocketConnection] Connecting to: {wsUrl}");
                //Debug.Log($"[WebSocketConnection] Starting connection attempt");

                // Create cancellation token for this connection attempt
                _cancellationTokenSource = new CancellationTokenSource();

                // Start connection (fire and forget - we'll wait for the OnOpen event)
                #pragma warning disable CS4014
                _webSocket.ConnectAsync();
                #pragma warning restore CS4014

                // Wait for connection with timeout (10 seconds)
                var startTime = DateTime.UtcNow;
                var timeout = TimeSpan.FromSeconds(10);

                // CRITICAL FIX: Store local reference to avoid race condition with Cleanup()
                // If HandleClose is called during connection, Cleanup() sets _webSocket = null
                // which would cause NullReferenceException in the while loop condition
                var ws = _webSocket;

                while (ws != null && ws.State == WebSocketState.Connecting &&
                       DateTime.UtcNow - startTime < timeout)
                {
                    await Task.Delay(100); // Check every 100ms
                    ws = _webSocket; // Re-check in case it changed
                }

                //var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                //Debug.Log($"[WebSocketConnection] Connection attempt completed after {elapsed:F1}s, state: {ws?.State}");

                // Check connection result - use local reference and null-check
                if (ws != null && ws.State == WebSocketState.Open)
                {
                    //Debug.Log($"[WebSocketConnection] Connection successful");
                    return true;
                }

                // Connection failed or timed out
                var finalState = ws?.State.ToString() ?? "null (cleaned up)";
                Debug.LogError($"[WebSocketConnection] Connection failed, final state: {finalState}");
                _cancellationTokenSource?.Cancel();
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
                return false;
            }
            catch (Exception ex)
            {
                // Don't log errors if we're shutting down
                if (!_isDisconnecting && _owner && _owner.gameObject)
                {
                    Debug.LogError($"[WebSocketConnection] Connection error: {ex.Message}");
                    OnError?.Invoke(ex.Message);
                }
                return false;
            }
            finally
            {
                IsConnecting = false;
            }
        }


        public async Task DisconnectAsync()
        {
            // Prevent multiple simultaneous disconnect calls
            if (_isDisconnecting) return;
            _isDisconnecting = true;
            _autoReconnectEnabled = false; // Disable auto-reconnect for manual disconnects

            if (_webSocket != null)
            {
                try
                {
                    // Store local reference to avoid null reference during async operations
                    var ws = _webSocket;
                    if (ws == null) return;

                    var state = ws.State;
                    if (state == WebSocketState.Open)
                    {
                        //Debug.Log($"[WebSocketConnection] Closing WebSocket connection gracefully");
                        await ws.CloseAsync();

                        // Wait a bit for close to complete
                        var timeout = DateTime.Now.AddSeconds(2);
                        while (ws.State == WebSocketState.Closing && DateTime.Now < timeout)
                        {
                            await Task.Delay(50);
                        }
                    }
                    //else
                    //{
                    //    Debug.Log($"[WebSocketConnection] WebSocket already in state: {state}");
                    //}
                }
                catch (Exception ex)
                {
                    // Only log if not already disconnecting (avoid spurious errors)
                    if (!_isDisconnecting)
                    {
                        Debug.LogError($"[WebSocketConnection] Error during disconnect: {ex.Message}");
                    }
                }
            }

            Cleanup();
            _isDisconnecting = false;
        }

        public async Task SendBinaryAsync(byte[] data)
        {
            if (!IsConnected)
            {
                Debug.LogWarning($"[WebSocketConnection] Cannot send binary data - not connected. State: {_webSocket?.State}");
                throw new InvalidOperationException("WebSocket is not connected");
            }

            // Only log first binary message and milestones when verbose logging is enabled
            //if (_binaryMessageCount == 0 || (_binaryMessageCount % 100 == 0 && _isVerboseLogging))
            //{
            //    Debug.Log($"[WebSocketConnection] Binary message #{_binaryMessageCount + 1} ({data.Length} bytes)");
            //}
            _binaryMessageCount++;

            await _webSocket.SendAsync(data);
        }

        public async Task SendTextAsync(string message)
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("WebSocket is not connected");
            }

            // CRITICAL DEBUG: Log before/after EnhancedWebSocket send
            var messageType = message.Contains("\"type\":") ? message.Substring(message.IndexOf("\"type\":") + 8, 20) : "unknown";
            Debug.LogError($"[DEBUG-WS-CONNECTION] SendTextAsync BEFORE EnhancedWebSocket.SendTextAsync - type: {messageType}, length: {message.Length}");

            // NativeWebSocket requires SendText for proper text frame
            await _webSocket.SendTextAsync(message);

            Debug.LogError($"[DEBUG-WS-CONNECTION] SendTextAsync AFTER EnhancedWebSocket.SendTextAsync completed - type: {messageType}");
        }

        public async Task SendJsonAsync(object obj)
        {
            // CRITICAL DEBUG: Log JSON serialization
            var json = JsonConvert.SerializeObject(obj);
            var messageType = obj.GetType().Name;
            Debug.LogError($"[DEBUG-WS-CONNECTION] SendJsonAsync - serialized {messageType}, JSON length: {json.Length}");

            await SendTextAsync(json);

            Debug.LogError($"[DEBUG-WS-CONNECTION] SendJsonAsync - SendTextAsync completed for {messageType}");
        }

        // IWebSocketConnection sync wrappers for compatibility
        public void SendMessage(string message)
        {
            // Fire-and-forget for sync interface
            _ = SendTextAsync(message);
        }

        public void SendBinaryData(byte[] data)
        {
            // Fire-and-forget for sync interface
            _ = SendBinaryAsync(data);
        }

        public void Connect(string url)
        {
            // Not used - connection is managed via ConnectAsync
            Debug.LogWarning("WebSocketConnection.Connect() called but connection is managed via ConnectAsync");
        }

        public void Disconnect()
        {
            // Fire-and-forget for sync interface
            _ = DisconnectAsync();
        }

        public void DispatchMessageQueue()
        {
            #if !UNITY_WEBGL || UNITY_EDITOR
            _webSocket?.DispatchMessageQueue();
            #endif
        }

        private void HandleOpen()
        {
            if (!_owner || !_owner.gameObject || _isDisconnecting) return;

            Debug.Log($"[WebSocketConnection] Connected successfully");

            IsConnecting = false;
            _currentReconnectDelay = _reconnectBaseDelay; // Reset reconnect delay
            _reconnectAttempts = 0; // Reset attempt counter
            _isReconnecting = false; // Clear reconnecting flag
            _binaryMessageCount = 0; // Reset counter for new connection
            OnConnected?.Invoke();
        }

        private void HandleTextMessage(string json)
        {
            if (!_owner || !_owner.gameObject || _isDisconnecting) return;

            OnTextMessageReceived?.Invoke(json);
        }

        private void HandleBinaryMessage(byte[] data)
        {
            //Debug.Log($"[WebSocketConnection] HandleBinaryMessage called with {data?.Length ?? 0} bytes");

            if (!_owner || !_owner.gameObject || _isDisconnecting)
            {
                Debug.LogWarning($"[WebSocketConnection] Ignoring binary message - owner:{_owner}, gameObject:{_owner?.gameObject}, disconnecting:{_isDisconnecting}");
                return;
            }

            //Debug.Log($"[WebSocketConnection] Invoking OnBinaryMessageReceived with {data.Length} bytes");
            OnBinaryMessageReceived?.Invoke(data);
        }

        private void HandleError(string error)
        {
            // Don't log errors during shutdown
            if (!_owner || !_owner.gameObject || _isDisconnecting) return;

            Debug.LogError($"[WebSocketConnection] Connection error: {error}");
            try
            {
                OnError?.Invoke(error);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[WebSocketConnection] Error in error handler: {ex.Message}");
            }
        }

        private void HandleClose(WebSocketCloseCode code)
        {
            if (!_owner || !_owner.gameObject) return;

            // Only log if not intentionally disconnecting
            if (!_isDisconnecting)
            {
                //Debug.Log($"[WebSocketConnection] Disconnected: {code}");

                // Check if we should attempt reconnection
                if (_autoReconnectEnabled && ShouldReconnect(code))
                {
                    _ = AttemptReconnectAsync();
                }
            }

            OnDisconnected?.Invoke();

            // Cleanup
            Cleanup();
        }

        private static bool ShouldReconnect(WebSocketCloseCode code)
        {
            return code switch
            {
                WebSocketCloseCode.Abnormal => true,
                WebSocketCloseCode.Away => true,
                //WebSocketCloseCode.Normal => false,
                //WebSocketCloseCode.ProtocolError => false,
                //WebSocketCloseCode.UnsupportedData => false,
                _ => false
            };
        }

        /// <summary>
        /// Attempts to reconnect to the WebSocket server with exponential backoff.
        /// Implements retry logic with configurable maximum attempts and delay limits.
        /// </summary>
        /// <returns>Task representing the asynchronous reconnection attempt</returns>
        private async Task AttemptReconnectAsync()
        {
            if (_isReconnecting || _reconnectAttempts >= _maxReconnectAttempts)
            {
                if (_reconnectAttempts >= _maxReconnectAttempts)
                {
                    Debug.LogWarning($"[WebSocketConnection] Max reconnect attempts ({_maxReconnectAttempts}) reached - abandoning reconnection");
                }
                return;
            }

            // Validate we have the necessary connection info
            if (string.IsNullOrEmpty(_lastWsUrl) || string.IsNullOrEmpty(_jwtToken))
            {
                Debug.LogError($"[WebSocketConnection] Cannot reconnect - missing connection parameters");
                return;
            }

            _isReconnecting = true;
            _reconnectAttempts++;

            try
            {
                //Debug.Log($"[WebSocketConnection] Attempting reconnect #{_reconnectAttempts}/{_maxReconnectAttempts} after {_currentReconnectDelay}s delay");

                // Wait for the current delay
                await Task.Delay((int)(_currentReconnectDelay * 1000));

                // Check if we should still reconnect (avoid race conditions)
                if (_isDisconnecting || !_owner || !_owner.gameObject)
                {
                    //Debug.Log($"[WebSocketConnection] Reconnect cancelled - component shutting down");
                    return;
                }

                // Try to reconnect
                var success = await ConnectAsync(_lastWsUrl, _lastPersonaName, _jwtToken);

                if (success)
                {
                    //Debug.Log($"[WebSocketConnection] Reconnection successful on attempt #{_reconnectAttempts}");
                    // Reset delay and attempts on success (handled in HandleOpen)
                }
                else
                {
                    Debug.LogWarning($"[WebSocketConnection] Reconnect attempt #{_reconnectAttempts} failed");

                    // Exponential backoff with maximum limit
                    var newDelay = _currentReconnectDelay * 2;
                    _currentReconnectDelay = newDelay > _reconnectMaxDelay ? _reconnectMaxDelay : newDelay;

                    // Schedule next attempt if we haven't reached max attempts
                    if (_reconnectAttempts < _maxReconnectAttempts)
                    {
                        //Debug.Log($"[WebSocketConnection] Scheduling next reconnect attempt with {_currentReconnectDelay}s delay");
                        _ = AttemptReconnectAsync();
                    }
                    else
                    {
                        Debug.LogError($"[WebSocketConnection] All reconnect attempts exhausted - connection lost");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[WebSocketConnection] Error during reconnect attempt: {ex.Message}");
            }
            finally
            {
                _isReconnecting = false;
            }
        }

        // Not used anymore - URL is built in StreamingApiClient
        //private string BuildWebSocketUrl(string endpoint)
        //{
        //    var wsScheme = _apiBaseUrl.StartsWith("https://") ? "wss" : "ws";
        //    var wsBaseUrl = _apiBaseUrl.Replace("http://", "").Replace("https://", "");

        //    return $"{wsScheme}://{wsBaseUrl.TrimEnd('/')}{endpoint}?token={_jwtToken}";
        //}

        private void Cleanup()
        {
            try
            {
                _cancellationTokenSource?.Cancel();
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
            }
            catch { /* Ignore cleanup errors */ }

            if (_webSocket != null)
            {
                try
                {
                    _webSocket.OnOpen -= HandleOpen;
                    _webSocket.OnTextMessage -= HandleTextMessage;
                    _webSocket.OnBinaryMessage -= HandleBinaryMessage;
                    _webSocket.OnError -= HandleError;
                    _webSocket.OnClose -= HandleClose;
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[WebSocketConnection] Error during cleanup: {ex.Message}");
                }
                finally
                {
                    _webSocket = null;
                }
            }
        }

        public void Dispose()
        {
            _ = DisconnectAsync();
        }
    }
}