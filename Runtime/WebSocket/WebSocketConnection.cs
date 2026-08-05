using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using NativeWebSocket;
using Newtonsoft.Json;
using Tsc.AIBridge.Core;
using Tsc.AIBridge.Messages;

namespace Tsc.AIBridge.WebSocket
{
    /// <summary>
    /// Low-level WebSocket connection handler using NativeWebSocket library.
    /// Manages the lifecycle of ONE socket: connect, message transport, close, cleanup.
    /// Key features:
    /// - JWT authentication via query parameters
    /// - Binary and text message handling
    /// - Thread-safe message dispatch to Unity main thread
    /// - Connection state tracking and error handling
    /// - Proper resource cleanup on disposal
    /// </summary>
    /// <remarks>
    /// This class does NOT reconnect. Reconnection is owned solely by
    /// <see cref="WebSocketClient"/>.EnsureConnectionAsync, which every SendXAsync calls first: it
    /// fetches a FRESH JWT, disposes the stale socket, constructs a new WebSocketConnection and
    /// re-subscribes to its events.
    ///
    /// There used to be a second auto-reconnect loop here (10 attempts, exponential backoff) and it
    /// could not work. HandleClose started the loop and then raised OnDisconnected, on which
    /// WebSocketClient.CleanupConnection() unsubscribed from this instance and nulled its reference —
    /// so a successful reconnect was invisible to the app, while the reopened socket stayed unread on
    /// the backend holding one of Kestrel's 100 upgraded-connection slots. It also retried with the
    /// ORIGINAL JWT (valid one hour, ClockSkew.Zero server-side), so after an hour every attempt
    /// failed on auth and the final attempt reported "Connection lost … restart" as a FATAL error,
    /// ending live training sessions over a recoverable hiccup (customer HMC, IVA bedrijfsartsen).
    ///
    /// Consequence for callers: after a close, the socket stays down until the next send. That is
    /// intentional — event-driven rather than polling, and a warm socket before a lesson is the job
    /// of the lesson-start connection check, not of a background retry loop.
    /// </remarks>
    public class WebSocketConnection : IWebSocketConnection, IDisposable
    {
        private EnhancedWebSocket _webSocket;
        private CancellationTokenSource _cancellationTokenSource;
        private readonly MonoBehaviour _owner;
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

        // Last URL, kept for diagnostics only (sanitized in logs — it carries the JWT).
        private string _lastWsUrl;

        // Message tracking
        private int _binaryMessageCount;
        private DateTime _connectionStartTime;

        public WebSocketConnection(MonoBehaviour owner, bool isVerboseLogging = false)
        {
            _isVerboseLogging = isVerboseLogging;
            _owner = owner;
        }

        /// <summary>
        /// Establishes WebSocket connection to the specified URL with JWT authentication.
        /// Waits for connection to complete with a 10-second timeout.
        /// </summary>
        /// <param name="wsUrl">
        /// Full WebSocket URL including the <c>?token=</c> query parameter. The caller
        /// (<see cref="WebSocketClient"/>.EnsureConnectionAsync) owns JWT acquisition and builds this
        /// URL; this class never handles the token separately.
        /// </param>
        /// <returns>True if connection was successfully established, false otherwise</returns>
        public async Task<bool> ConnectAsync(string wsUrl)
        {
            if (IsConnected || IsConnecting)
            {
                Debug.LogWarning($"[WebSocketConnection] Already connected or connecting");
                return false;
            }

            // Kept for diagnostics only
            _lastWsUrl = wsUrl;

            IsConnecting = true;

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

                // Always a warning, never an error: the caller (EnsureConnectionAsync) decides how to
                // report this, and a LogError from the transport layer would raise the host's fatal
                // "app must restart" popup for what is a retryable failure.
                Debug.LogWarning($"[WebSocketConnection] Connection failed\n" +
                                 $"  URL: {sanitizedUrl}\n" +
                                 $"  Final State: {finalState}\n" +
                                 $"  Time Elapsed: {totalElapsed:F2}s");

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
                    Debug.LogWarning($"[WebSocketConnection] Connection error: {ex.Message}");
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
                    Debug.LogWarning($"[WebSocketConnection] Error during disconnect: {ex.Message}");
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

            // Never LogError here: that triggers the host ErrorHandler's fatal "app must restart"
            // popup, and from this layer every failure is retryable — the next send re-runs
            // EnsureConnectionAsync with a fresh JWT. Deciding that a failure is final is the
            // caller's job, not the transport's.
            Debug.LogWarning($"[WebSocketConnection] Connection error: {error}");

            try
            {
                OnError?.Invoke(error);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[WebSocketConnection] Error in error handler: {ex.Message}");
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

                // DIAGNOSTIC: this is the log line that tells you, in a field report, whether the
                // socket ever opened at all (network blocks wss) or opened and later dropped.
                Debug.Log($"[WebSocketConnection] 🔌 DISCONNECTED\n" +
                         $"  Code: {code}\n" +
                         $"  Duration: {timeSinceConnect.TotalSeconds:F1}s\n" +
                         $"  Messages sent: {messagesSent}\n" +
                         $"  URL: {SanitizeUrl(_lastWsUrl ?? "null")}");

                // No reconnect here by design — WebSocketClient.EnsureConnectionAsync reconnects on
                // the next send, with a fresh JWT and re-subscribed handlers. See the class remarks.
            }

            OnDisconnected?.Invoke();

            // Cleanup
            Cleanup();
        }

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
                        Debug.LogWarning($"[WebSocketConnection] ❌ DNS resolution returned no addresses");
                    }
                }
                catch (Exception dnsEx)
                {
                    Debug.LogWarning($"[WebSocketConnection] ❌ DNS resolution failed: {dnsEx.Message}");
                    Debug.LogWarning($"[WebSocketConnection] This could indicate: No internet connection, DNS server issues, or invalid hostname");
                }

                // Check if owner still exists (might be shutting down)
                if (!_owner || !_owner.gameObject)
                {
                    Debug.LogWarning($"[WebSocketConnection] ⚠️ Owner MonoBehaviour or GameObject is null - component may be shutting down");
                }

                // Log current reconnection state
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[WebSocketConnection] Diagnostics failed: {ex.Message}");
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
                    Debug.LogWarning($"[WebSocketConnection] Error during cleanup: {ex.Message}");
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