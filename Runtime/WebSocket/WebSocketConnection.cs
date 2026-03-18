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
        private DateTime _connectionStartTime;

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

            var connectionStartTime = DateTime.UtcNow;

            try
            {
                // LOG: Connection attempt details (sanitize URL to hide token)
                var sanitizedUrl = SanitizeUrl(wsUrl);
                Debug.Log($"[WebSocketConnection] 🔌 Starting connection attempt to: {sanitizedUrl}");

                // Use the URL as-is (already contains all parameters)
                _webSocket = new EnhancedWebSocket(wsUrl);

                // Set up event handlers with proper binary/text separation
                _webSocket.OnOpen += HandleOpen;
                _webSocket.OnTextMessage += HandleTextMessage;
                _webSocket.OnBinaryMessage += HandleBinaryMessage;
                _webSocket.OnError += HandleError;
                _webSocket.OnClose += HandleClose;

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

                var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;

                // Check connection result - use local reference and null-check
                if (ws != null && ws.State == WebSocketState.Open)
                {
                    Debug.Log($"[WebSocketConnection] ✅ Connection successful after {elapsed:F1}s");
                    return true;
                }

                // Connection failed or timed out
                var finalState = ws?.State.ToString() ?? "null (cleaned up)";
                var totalElapsed = (DateTime.UtcNow - connectionStartTime).TotalSeconds;

                // DIAGNOSTIC LOGGING: Detailed failure information
                Debug.LogError($"[WebSocketConnection] ❌ CONNECTION FAILED\n" +
                              $"  URL: {sanitizedUrl}\n" +
                              $"  Final State: {finalState}\n" +
                              $"  Time Elapsed: {totalElapsed:F2}s\n" +
                              $"  Reconnect Attempt: #{_reconnectAttempts}/{_maxReconnectAttempts}\n" +
                              $"  Auto-Reconnect: {(_autoReconnectEnabled ? "Enabled" : "Disabled")}");

                // Trigger health check to diagnose if backend is reachable
                _ = DiagnoseConnectionFailure(sanitizedUrl);

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

            // NativeWebSocket requires SendText for proper text frame
            await _webSocket.SendTextAsync(message);
        }

        public async Task SendJsonAsync(object obj)
        {
            var json = JsonConvert.SerializeObject(obj);
            await SendTextAsync(json);
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

            //Debug.Log($"[WebSocketConnection] Connected successfully");

            IsConnecting = false;
            _currentReconnectDelay = _reconnectBaseDelay; // Reset reconnect delay
            _reconnectAttempts = 0; // Reset attempt counter
            _isReconnecting = false; // Clear reconnecting flag
            _binaryMessageCount = 0; // Reset counter for new connection
            _connectionStartTime = DateTime.UtcNow; // Track connection start for diagnostics
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
                // DIAGNOSTIC: Log with timestamp and connection info
                var timeSinceConnect = DateTime.UtcNow - _connectionStartTime;
                var messagesSent = _binaryMessageCount;

                Debug.Log($"[WebSocketConnection] 🔌 DISCONNECTED\n" +
                         $"  Code: {code}\n" +
                         $"  Duration: {timeSinceConnect.TotalSeconds:F1}s\n" +
                         $"  Messages sent: {messagesSent}\n" +
                         $"  Auto-reconnect: {_autoReconnectEnabled}\n" +
                         $"  Will reconnect: {(_autoReconnectEnabled && ShouldReconnect(code) ? "YES" : "NO")}\n" +
                         $"  URL: {SanitizeUrl(_lastWsUrl ?? "null")}");

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
                WebSocketCloseCode.Normal => true,     // Auto-reconnect on idle timeout (backend sends Normal)
                WebSocketCloseCode.Abnormal => true,
                WebSocketCloseCode.Away => true,
                //WebSocketCloseCode.ProtocolError => false,
                //WebSocketCloseCode.UnsupportedData => false,
                _ => false
            };
        }

        /// <summary>
        /// Attempts to reconnect to the WebSocket server with exponential backoff.
        /// Implements retry logic with configurable maximum attempts and delay limits.
        /// CRITICAL: Uses OLD JWT token - consider refreshing for long-running sessions
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
            if (string.IsNullOrEmpty(_lastWsUrl))
            {
                UserErrorLogger.LogError(
                    "Connection configuration error. Please restart the session.",
                    "[WebSocketConnection] Cannot reconnect - missing WebSocket URL");
                return;
            }

            _isReconnecting = true;
            _reconnectAttempts++;

            try
            {
                Debug.Log($"[WebSocketConnection] 🔄 Attempting reconnect #{_reconnectAttempts}/{_maxReconnectAttempts} after {_currentReconnectDelay}s delay");

                // Wait for the current delay
                await Task.Delay((int)(_currentReconnectDelay * 1000));

                // Check if we should still reconnect (avoid race conditions)
                if (_isDisconnecting || !_owner || !_owner.gameObject)
                {
                    Debug.Log($"[WebSocketConnection] Reconnect cancelled - component shutting down");
                    return;
                }

                // CRITICAL FIX: Check if JWT token might be expired
                // For now, we reuse the old token, but log a warning if reconnect happens after long duration
                // TODO: Add JWT refresh mechanism for long-running sessions
                if (string.IsNullOrEmpty(_jwtToken))
                {
                    UserErrorLogger.LogError(
                        "Your session has expired. Please restart the session.",
                        "[WebSocketConnection] Cannot reconnect - JWT token is null. This should not happen!");
                    return;
                }

                // Try to reconnect with existing token
                // NOTE: If token expired (>55min old), this will fail and user needs to restart session
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
                        UserErrorLogger.LogError(
                            "Connection lost. Please check your internet connection and restart.",
                            "[WebSocketConnection] All reconnect attempts exhausted - connection lost");
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

        /// <summary>
        /// Removes sensitive information (JWT tokens) from WebSocket URL for safe logging.
        /// </summary>
        /// <param name="wsUrl">Original WebSocket URL</param>
        /// <returns>Sanitized URL with token values replaced with [REDACTED]</returns>
        private string SanitizeUrl(string wsUrl)
        {
            if (string.IsNullOrEmpty(wsUrl))
                return wsUrl;

            // Hide JWT token from URL for logging
            // Handles both ?token=... and &token=...
            var sanitized = System.Text.RegularExpressions.Regex.Replace(
                wsUrl,
                @"([?&])(token|jwt)=([^&]+)",
                "$1$2=[REDACTED]",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
            );

            return sanitized;
        }

        /// <summary>
        /// Performs diagnostic checks when connection failure occurs to help identify root cause.
        /// Checks DNS resolution, Unity network state, and component lifecycle status.
        /// </summary>
        /// <param name="sanitizedUrl">Sanitized WebSocket URL (without tokens)</param>
        private async Task DiagnoseConnectionFailure(string sanitizedUrl)
        {
            try
            {
                Debug.Log($"[WebSocketConnection] 🔍 Running connection diagnostics...");

                // Extract host from WebSocket URL
                var uri = new Uri(sanitizedUrl.Replace("[REDACTED]", "dummy"));
                var host = uri.Host;
                var port = uri.Port;

                Debug.Log($"[WebSocketConnection] 📍 Target: {host}:{port} (scheme: {uri.Scheme})");

                // Log Unity network state
                Debug.Log($"[WebSocketConnection] 📡 Unity Network Reachability: {Application.internetReachability}");

                // Check if we can resolve DNS
                try
                {
                    var addresses = await System.Net.Dns.GetHostAddressesAsync(host);
                    if (addresses != null && addresses.Length > 0)
                    {
                        var addressList = new System.Text.StringBuilder();
                        for (int i = 0; i < addresses.Length; i++)
                        {
                            if (i > 0) addressList.Append(", ");
                            addressList.Append(addresses[i].ToString());
                        }
                        Debug.Log($"[WebSocketConnection] ✅ DNS resolution successful: {addressList}");
                    }
                    else
                    {
                        Debug.LogError($"[WebSocketConnection] ❌ DNS resolution returned no addresses");
                    }
                }
                catch (Exception dnsEx)
                {
                    Debug.LogError($"[WebSocketConnection] ❌ DNS resolution failed: {dnsEx.Message}");
                    Debug.LogError($"[WebSocketConnection] This could indicate: No internet connection, DNS server issues, or invalid hostname");
                }

                // Check if owner still exists (might be shutting down)
                if (!_owner || !_owner.gameObject)
                {
                    Debug.LogWarning($"[WebSocketConnection] ⚠️ Owner MonoBehaviour or GameObject is null - component may be shutting down");
                }

                // Log current reconnection state
                Debug.Log($"[WebSocketConnection] 🔄 Reconnection state: Attempts={_reconnectAttempts}/{_maxReconnectAttempts}, " +
                         $"Delay={_currentReconnectDelay:F1}s, IsReconnecting={_isReconnecting}, AutoReconnect={_autoReconnectEnabled}");

            }
            catch (Exception ex)
            {
                Debug.LogError($"[WebSocketConnection] Diagnostics failed: {ex.Message}");
            }
        }

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
                    // CRITICAL: Dispose WebSocket before nulling reference to prevent memory leaks
                    // Without this, orphaned WebSockets remain in memory causing ObjectDisposedException on backend
                    _webSocket?.Dispose();
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