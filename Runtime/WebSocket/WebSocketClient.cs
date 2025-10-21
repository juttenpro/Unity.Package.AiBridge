using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Tsc.AIBridge.Auth;
using Tsc.AIBridge.Configuration;
using Tsc.AIBridge.Messages;
using Tsc.AIBridge.Core;
using Tsc.AIBridge.Utilities;
using UnityEngine;

namespace Tsc.AIBridge.WebSocket
{
    /// <summary>
    /// WEBSOCKET CLIENT - Singleton connection manager (THE ONLY connection authority).
    ///
    /// PRIMARY RESPONSIBILITY:
    /// Single source of truth for ALL WebSocket connection decisions.
    /// No other class should check connection state or manage connections!
    ///
    /// WHAT THIS CLASS DOES:
    /// ✅ Maintains singleton WebSocket connection
    /// ✅ Automatically establishes connection when needed (via SendXAsync methods)
    /// ✅ Handles JWT authentication and token caching
    /// ✅ Routes messages by RequestId to correct NPC
    /// ✅ Manages connection lifecycle (connect/disconnect/reconnect)
    /// ✅ Provides GetConnectionAsync() as THE connection API
    ///
    /// WHAT THIS CLASS DOES NOT DO:
    /// ❌ Let other classes check IsConnected before sending
    /// ❌ Let other classes decide when to connect
    /// ❌ Message formatting (NetworkMessageController's job)
    /// ❌ Audio processing (AudioStreamProcessor's job)
    /// ❌ NPC state management (StreamingApiClient's job)
    ///
    /// KEY API:
    /// - GetConnectionAsync(): Returns connection, creates if needed
    /// - SendSessionStartAsync(): Ensures connection, then sends
    /// - SendBinaryAsync(): Ensures connection, then sends
    /// - Other classes should NEVER check IsConnected!
    ///
    /// ARCHITECTURE PRINCIPLE:
    /// Other classes just call SendXAsync - we handle ALL connection logic internally.
    /// This prevents timing issues and connection race conditions.
    /// - Thread-safe message routing
    ///
    /// DEPENDENCIES:
    /// - WebSocketConnection: Low-level WebSocket implementation
    /// - JwtAuthenticationService: JWT token generation
    /// - INpcMessageHandler: Interface for NPC message processing
    /// </summary>
    public class WebSocketClient : MonoBehaviour
    {
        #region Events

        /// <summary>
        /// Event fired when connection is successfully established
        /// </summary>
        public event Action<IWebSocketConnection> OnConnectionEstablished;

        /// <summary>
        /// Event fired when connection attempt fails
        /// </summary>
        public event Action<ConnectionFailedEventArgs> OnConnectionFailed;

        /// <summary>
        /// Event fired when connection fails fatally and training cannot continue
        /// This includes both connection failures and lost connections
        /// </summary>
#pragma warning disable 0067 // Event is never used
        public event Action<string> OnFatalConnectionError;
#pragma warning restore 0067

        #endregion

        // Singleton instance
        private static WebSocketClient _instance;
        public static WebSocketClient Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = FindFirstObjectByType<WebSocketClient>();
                    if (_instance == null)
                    {
                        Debug.LogError("[WebSocketClient] No WebSocketClient found in scene! Please add WebSocketClient to the scene.");
                    }
                }
                return _instance;
            }
        }

        // Core WebSocket connection (reuse existing low-level implementation)
        private WebSocketConnection _webSocket;
        private bool _isConnecting;
        private string _currentUrl;

        // NPC message routing (RequestId -> handler)
        private readonly Dictionary<string, INpcMessageHandler> _npcHandlers = new();
        private readonly object _routingLock = new();

        // Authentication services
        private IAuthenticationService _authService;
        private IApiKeyProvider _apiKeyProvider;

        // Configuration
        [Header("API Configuration")]
        [SerializeField] private string apiBaseUrl = "https://api-orchestrator-service-104588943109.europe-west4.run.app";
        [SerializeField] private string webSocketEndpoint = "/api/websocket";

        // Public property for API base URL
        public string ApiBaseUrl => apiBaseUrl;

        [Header("Connection Settings")]
        [SerializeField]
        [Tooltip("Establish WebSocket connection during scene initialization for instant availability")]
        private bool establishConnection = true;

        [SerializeField]
        [Tooltip("Send HTTP wake-up call to prevent Cloud Run cold start")]
        private bool sendWakeUpCall = true;

        [Header("Debug Settings")]
        [SerializeField] private bool enableVerboseLogging;

        [Header("Persistence")]
        [Tooltip("Make this GameObject persist across scene changes (for Initializer scene setup)")]
        [SerializeField] private bool persistAcrossScenes = false;

        [Header("API Key Configuration")]
        [SerializeField]
        [Tooltip("Provider component implementing IApiKeyProvider or IAsyncApiKeyProvider.\nRequired for authentication with backend services.")]
        private MonoBehaviour apiKeyProviderComponent;

        // Connection state
        public virtual bool IsConnected => _webSocket != null && _webSocket.IsConnected;
        public ConnectionState State
        {
            get
            {
                if (_webSocket == null) return ConnectionState.Disconnected;
                if (_webSocket.IsConnected) return ConnectionState.Connected;
                if (_webSocket.IsConnecting) return ConnectionState.Connecting;
                return ConnectionState.Disconnected;
            }
        }

        /// <summary>
        /// Get cached JWT token for reuse in REST calls
        /// SIMPLICITY: Share auth between WebSocket and REST
        /// </summary>
        public string CachedJwtToken => (_authService as JwtAuthenticationService)?.CachedToken;

        // Events for connection lifecycle
        public event Action OnConnected;
        public event Action<NativeWebSocket.WebSocketCloseCode> OnDisconnected;

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;

            // Make persistent across scenes if configured (for Initializer scene setup)
            if (persistAcrossScenes)
            {
                DontDestroyOnLoad(gameObject);
                if(enableVerboseLogging)
                    Debug.Log("[WebSocketClient] Set to persist across scenes");
            }

            // Initialize authentication with configurable API key provider
            InitializeApiKeyProvider();
            _authService = new JwtAuthenticationService(apiBaseUrl);

            // Delay initialization to Start() to avoid interfering with scene loading
            // StartCoroutine(InitializeConnectionSequence()); // Moved to Start()
        }

        private void Start()
        {
            // Start initialization sequence after scene is properly loaded
            StartCoroutine(InitializeConnectionSequence());
        }

        /// <summary>
        /// Initialize API key provider from Inspector configuration.
        /// Supports both sync (IApiKeyProvider) and async (IAsyncApiKeyProvider) providers.
        /// REQUIRED: apiKeyProviderComponent must be assigned in Inspector.
        /// </summary>
        private void InitializeApiKeyProvider()
        {
            if (apiKeyProviderComponent == null)
            {
                Debug.LogError("[WebSocketClient] apiKeyProviderComponent is not assigned! " +
                    "Assign a component implementing IApiKeyProvider or IAsyncApiKeyProvider in the Inspector.");
                _apiKeyProvider = new SimpleApiKeyProvider(string.Empty); // Prevent null reference
                return;
            }

            // Check if it implements IApiKeyProvider (sync)
            if (apiKeyProviderComponent is IApiKeyProvider syncProvider)
            {
                _apiKeyProvider = syncProvider;
                if (enableVerboseLogging)
                    Debug.Log($"[WebSocketClient] Using sync API key provider: {apiKeyProviderComponent.GetType().Name}");
                return;
            }

            // Check if it implements IAsyncApiKeyProvider (async)
            if (apiKeyProviderComponent is IAsyncApiKeyProvider asyncProvider)
            {
                _apiKeyProvider = new AsyncApiKeyProviderAdapter(this, asyncProvider);
                if (enableVerboseLogging)
                    Debug.Log($"[WebSocketClient] Using async API key provider: {apiKeyProviderComponent.GetType().Name}");
                return;
            }

            // Provider assigned but doesn't implement required interface
            Debug.LogError($"[WebSocketClient] apiKeyProviderComponent ({apiKeyProviderComponent.GetType().Name}) " +
                "does not implement IApiKeyProvider or IAsyncApiKeyProvider!");
            _apiKeyProvider = new SimpleApiKeyProvider(string.Empty); // Prevent null reference
        }

        /// <summary>
        /// Register an NPC to receive messages for a specific RequestId
        /// </summary>
        public void RegisterNpc(string requestId, INpcMessageHandler handler)
        {
            if (string.IsNullOrEmpty(requestId))
            {
                Debug.LogError("[UnifiedWebSocket] Cannot register NPC with null/empty RequestId!");
                return;
            }

            lock (_routingLock)
            {
                if (_npcHandlers.ContainsKey(requestId))
                {
                    // Only warn if it's actually a different handler
                    // Same handler re-registering is fine (happens after reconnect)
                    if (_npcHandlers[requestId] != handler)
                    {
                        Debug.LogWarning($"[UnifiedWebSocket] Overwriting handler for RequestId: {requestId} (different handler)");
                    }
                    // If it's the same handler, silently update (normal reconnect behavior)
                }

                _npcHandlers[requestId] = handler;

                if (enableVerboseLogging)
                    Debug.Log($"[UnifiedWebSocket] Registered NPC for RequestId: {requestId}");
            }
        }

        /// <summary>
        /// Unregister an NPC from receiving messages
        /// </summary>
        public void UnregisterNpc(string requestId)
        {
            if (string.IsNullOrEmpty(requestId))
                return;

            lock (_routingLock)
            {
                if (_npcHandlers.Remove(requestId))
                {
                    if (enableVerboseLogging)
                        Debug.Log($"[UnifiedWebSocket] Unregistered NPC for RequestId: {requestId}");
                }
            }
        }

        /// <summary>
        /// Get or establish WebSocket connection.
        /// Returns existing connection if available, otherwise creates new one.
        /// </summary>
        /// <param name="cancellationToken">Token to cancel the connection attempt</param>
        /// <returns>Active WebSocket connection</returns>
        /// <exception cref="InvalidOperationException">When connection cannot be established</exception>
        public async Task<IWebSocketConnection> GetConnectionAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                // Return existing connection if connected
                if (_webSocket?.IsConnected == true)
                {
                    if (enableVerboseLogging)
                        Debug.Log("[UnifiedWebSocket] Returning existing connection");
                    return _webSocket;
                }

                // Create new connection
                if (enableVerboseLogging)
                    Debug.Log("[UnifiedWebSocket] Establishing new connection...");

                // Use existing EnsureConnectionAsync logic
                if (await EnsureConnectionAsync(cancellationToken))
                {
                    OnConnectionEstablished?.Invoke(_webSocket);
                    return _webSocket;
                }

                var error = "Failed to establish WebSocket connection";
                OnConnectionFailed?.Invoke(new ConnectionFailedEventArgs(
                    ConnectionFailureReason.NetworkError, error));
                throw new InvalidOperationException(error);
            }
            catch (OperationCanceledException)
            {
                var error = "Connection attempt was cancelled";
                OnConnectionFailed?.Invoke(new ConnectionFailedEventArgs(
                    ConnectionFailureReason.Cancelled, error));
                throw;
            }
            catch (Exception ex)
            {
                OnConnectionFailed?.Invoke(new ConnectionFailedEventArgs(
                    ConnectionFailureReason.Unknown, ex.Message));
                throw;
            }
        }

        /// <summary>
        /// Send SessionStart message to begin a conversation
        /// </summary>
        public virtual async Task SendSessionStartAsync(SessionStartMessage message, CancellationToken cancellationToken = default)
        {
            if (message == null)
            {
                Debug.LogError("[UnifiedWebSocket] Cannot send null SessionStartMessage!");
                throw new ArgumentNullException(nameof(message));
            }

            // Ensure connection before sending - pass cancellation token
            if (enableVerboseLogging)
                Debug.Log($"[UnifiedWebSocket] Ensuring connection before SessionStart for RequestId: {message.RequestId}");
            if (!await EnsureConnectionAsync(cancellationToken))
            {
                Debug.LogError("[UnifiedWebSocket] Failed to establish connection for SessionStart");
                throw new InvalidOperationException("Failed to establish WebSocket connection");
            }
            if (enableVerboseLogging)
                Debug.Log($"[UnifiedWebSocket] Connection ready, sending SessionStart for RequestId: {message.RequestId}");

            // Pre-register the NPC for this RequestId (no handler yet, but reserve the slot)
            // The actual handler will be set when RegisterNpc is called

            // Send the message (check for cancellation)
            cancellationToken.ThrowIfCancellationRequested();

            await _webSocket.SendJsonAsync(message);

            if (enableVerboseLogging)
                Debug.Log($"[UnifiedWebSocket] Sent SessionStart for RequestId: {message.RequestId}");
        }

        /// <summary>
        /// Send binary audio data
        /// </summary>
        public virtual async Task SendBinaryAsync(byte[] audioData)
        {
            // Ensure connection before sending - auto-reconnect if needed
            if (enableVerboseLogging && (_webSocket == null || !_webSocket.IsConnected))
                Debug.Log("[UnifiedWebSocket] Connection lost - auto-reconnecting for SendBinaryAsync...");

            if (!await EnsureConnectionAsync(CancellationToken.None))
            {
                Debug.LogError("[UnifiedWebSocket] Cannot send audio - failed to establish connection!");
                return;
            }

            await _webSocket.SendBinaryAsync(audioData);
        }

        /// <summary>
        /// Send EndOfSpeech message
        /// </summary>
        public async Task SendEndOfSpeechAsync(string requestId)
        {
            // Ensure connection before sending - auto-reconnect if needed
            if (enableVerboseLogging && (_webSocket == null || !_webSocket.IsConnected))
                Debug.Log($"[UnifiedWebSocket] Connection lost - auto-reconnecting for SendEndOfSpeechAsync (RequestId: {requestId})...");

            if (!await EnsureConnectionAsync(CancellationToken.None))
            {
                Debug.LogError("[UnifiedWebSocket] Cannot send EndOfSpeech - failed to establish connection!");
                return;
            }

            var message = new EndOfSpeechMessage
            {
                Type = "EndOfSpeech",
                RequestId = requestId
            };

            await _webSocket.SendJsonAsync(message);
        }

        /// <summary>
        /// Send EndOfAudio message
        /// </summary>
        public async Task SendEndOfAudioAsync(string requestId)
        {
            // Ensure connection before sending - auto-reconnect if needed
            if (enableVerboseLogging && (_webSocket == null || !_webSocket.IsConnected))
                Debug.Log($"[UnifiedWebSocket] Connection lost - auto-reconnecting for SendEndOfAudioAsync (RequestId: {requestId})...");

            if (!await EnsureConnectionAsync(CancellationToken.None))
            {
                Debug.LogError("[UnifiedWebSocket] Cannot send EndOfAudio - failed to establish connection!");
                return;
            }

            var message = new EndOfAudioMessage
            {
                Type = "EndOfAudio",
                RequestId = requestId
            };

            await _webSocket.SendJsonAsync(message);
        }

        /// <summary>
        /// Send SessionCancel message to cancel a session (EMERGENCY - stops everything)
        /// </summary>
        public async Task SendSessionCancelAsync(SessionCancelMessage message)
        {
            if (_webSocket == null || !_webSocket.IsConnected)
            {
                Debug.LogError("[UnifiedWebSocket] Cannot send SessionCancel - not connected!");
                return;
            }

            await _webSocket.SendJsonAsync(message);
            if (enableVerboseLogging)
                Debug.Log($"[UnifiedWebSocket] Sent SessionCancel for RequestId: {message.RequestId}");
        }

        /// <summary>
        /// Send InterruptionOccurred message (NORMAL interruption - stops TTS, keeps LLM for metadata)
        /// </summary>
        public async Task SendInterruptionOccurredAsync(InterruptionOccurredMessage message)
        {
            // Ensure connection before sending (follows WebSocketClient pattern)
            if (!await EnsureConnectionAsync())
            {
                Debug.LogError("[UnifiedWebSocket] Failed to establish connection for InterruptionOccurred");
                return;
            }

            await _webSocket.SendJsonAsync(message);

            // CRITICAL FIX: NativeWebSocket SendText() race condition
            // SendText() can return before the message is actually transmitted over the network.
            // If the next message (SessionStart) is sent immediately after (3-4ms), the first
            // message (InterruptionOccurred) can be lost in the send buffer.
            // Adding a 50ms delay ensures the message is fully transmitted before the next message.
            await System.Threading.Tasks.Task.Delay(50);

            if (enableVerboseLogging)
                Debug.Log($"[UnifiedWebSocket] Sent InterruptionOccurred for RequestId: {message.RequestId}");
        }

        /// <summary>
        /// Send TextInput message for text-only conversation (no audio)
        /// </summary>
        public async Task SendTextInputAsync(TextInputMessage message)
        {
            // Ensure connection before sending (follows WebSocketClient pattern)
            if (!await EnsureConnectionAsync())
            {
                Debug.LogError("[UnifiedWebSocket] Failed to establish connection for TextInput");
                return;
            }

            await _webSocket.SendJsonAsync(message);
            if (enableVerboseLogging)
                Debug.Log($"[UnifiedWebSocket] Sent TextInput for RequestId: {message.RequestId}, Text: '{message.Text}'");
        }

        /// <summary>
        /// Send DirectTTS message - text directly to TTS without LLM processing
        /// </summary>
        public async Task SendDirectTTSAsync(DirectTTSMessage message)
        {
            // Ensure connection before sending (follows WebSocketClient pattern)
            if (!await EnsureConnectionAsync())
            {
                Debug.LogError("[UnifiedWebSocket] Failed to establish connection for DirectTTS");
                return;
            }

            await _webSocket.SendJsonAsync(message);
            if (enableVerboseLogging)
                Debug.Log($"[UnifiedWebSocket] Sent DirectTTS for RequestId: {message.RequestId}, Text: '{message.Text}', Voice: {message.Voice ?? "default"}");
        }

        /// <summary>
        /// Send AnalysisRequest message for conversation analysis
        /// </summary>
        public async Task SendAnalysisRequestAsync(AnalysisRequestMessage message)
        {
            // Ensure connection before sending (follows WebSocketClient pattern)
            if (!await EnsureConnectionAsync())
            {
                Debug.LogError("[UnifiedWebSocket] Failed to establish connection for AnalysisRequest");
                return;
            }

            await _webSocket.SendJsonAsync(message);
            if (enableVerboseLogging)
                Debug.Log($"[UnifiedWebSocket] Sent AnalysisRequest for RequestId: {message.RequestId}, Model: {message.Context?.llmModel ?? "default"}");
        }

        /// <summary>
        /// Send PauseStream message to pause audio streaming from backend
        /// </summary>
        public async Task SendPauseStreamAsync(PauseStreamMessage message)
        {
            // Ensure connection before sending
            if (!await EnsureConnectionAsync())
            {
                Debug.LogError("[UnifiedWebSocket] Failed to establish connection for PauseStream");
                return;
            }

            await _webSocket.SendJsonAsync(message);
            if (enableVerboseLogging)
                Debug.Log($"[UnifiedWebSocket] Sent PauseStream for RequestId: {message.RequestId}, Reason: {message.Reason ?? "none"}");
        }

        /// <summary>
        /// Send ResumeStream message to resume paused audio streaming
        /// </summary>
        public async Task SendResumeStreamAsync(ResumeStreamMessage message)
        {
            // Ensure connection before sending
            if (!await EnsureConnectionAsync())
            {
                Debug.LogError("[UnifiedWebSocket] Failed to establish connection for ResumeStream");
                return;
            }

            await _webSocket.SendJsonAsync(message);
            if (enableVerboseLogging)
                Debug.Log($"[UnifiedWebSocket] Sent ResumeStream for RequestId: {message.RequestId}");
        }

        /// <summary>
        /// Ensure WebSocket connection is established
        /// </summary>
        private async Task<bool> EnsureConnectionAsync(CancellationToken cancellationToken = default)
        {
            // Already connected
            if (_webSocket != null && _webSocket.IsConnected)
                return true;

            // Already connecting
            if (_isConnecting)
            {
                // Wait for connection to complete (with cancellation support)
                while (_isConnecting && !cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(100, cancellationToken);
                }

                cancellationToken.ThrowIfCancellationRequested();
                return _webSocket != null && _webSocket.IsConnected;
            }

            // Start new connection
            _isConnecting = true;
            var startTime = System.DateTime.Now;

            try
            {
                // Get JWT token
                var jwtStartTime = System.DateTime.Now;
                var jwtToken = await _authService.GetAuthTokenAsync(
                    "UnifiedConnection",
                    Constants.DefaultPlayerRole,
                    _apiKeyProvider.GetOrchestratorApiKey());
                var jwtDuration = (System.DateTime.Now - jwtStartTime).TotalMilliseconds;
                if (enableVerboseLogging)
                    Debug.Log($"[UnifiedWebSocket] JWT token generation took {jwtDuration:F0}ms");

                if (string.IsNullOrEmpty(jwtToken))
                {
                    Debug.LogError("[UnifiedWebSocket] Failed to get JWT token");
                    return false;
                }

                // Build WebSocket URL
                var wsScheme = apiBaseUrl.StartsWith("https://") ? "wss" : "ws";
                var wsBaseUrl = apiBaseUrl.Replace("http://", "").Replace("https://", "");
                var baseUrl = $"{wsScheme}://{wsBaseUrl.TrimEnd('/')}{webSocketEndpoint}";
                var fullUrl = $"{baseUrl}?token={Uri.EscapeDataString(jwtToken)}";

                // Create WebSocket connection
                _webSocket = new WebSocketConnection(this, 1f, 30f, enableVerboseLogging);

                // Subscribe to events
                _webSocket.OnTextMessageReceived += HandleTextMessage;
                _webSocket.OnBinaryMessageReceived += HandleBinaryMessage;
                _webSocket.OnDisconnected += HandleWebSocketDisconnection;

                // Connect
                var wsStartTime = System.DateTime.Now;
                var connected = await _webSocket.ConnectAsync(fullUrl, "UnifiedConnection", jwtToken);
                var wsDuration = (System.DateTime.Now - wsStartTime).TotalMilliseconds;
                if (enableVerboseLogging)
                    Debug.Log($"[UnifiedWebSocket] WebSocket handshake took {wsDuration:F0}ms");

                if (connected)
                {
                    _currentUrl = fullUrl;
                    var totalDuration = (System.DateTime.Now - startTime).TotalMilliseconds;
                    if (enableVerboseLogging)
                        Debug.Log($"[UnifiedWebSocket] Connection established successfully (Total: {totalDuration:F0}ms, JWT: {jwtDuration:F0}ms, WS: {wsDuration:F0}ms)");
                    OnConnected?.Invoke();
                    return true;
                }
                else
                {
                    Debug.LogError("[UnifiedWebSocket] Failed to establish connection");
                    CleanupConnection();
                    return false;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UnifiedWebSocket] Connection error: {ex.Message}");
                CleanupConnection();
                return false;
            }
            finally
            {
                _isConnecting = false;
            }
        }

        /// <summary>
        /// Handle incoming text messages and route to appropriate NPC
        /// </summary>
        private void HandleTextMessage(string json)
        {
            if (string.IsNullOrEmpty(json))
                return;

            // SPECIAL HANDLING: BufferHint messages need to go to AdaptiveBufferManager
            // Use NpcMessageRouter with bufferHintOnly=true to prevent duplicate NPC routing
            // BufferHint will go to BOTH AdaptiveBufferManager (via NpcMessageRouter) AND NPC (via handler.OnTextMessage below)
            var isBufferHint = json.Contains("\"type\":\"bufferHint\"") || json.Contains("\"type\":\"BufferHint\"");
            if (isBufferHint)
            {
                NpcMessageRouter.Instance.RouteMessage(json, requestId: null, bufferHintOnly: true);

                // CRITICAL FIX: BufferHint must reach ALL NPCs for TTS latency tracking (required for metrics)
                // Broadcast to all handlers regardless of RequestId to ensure LatencyTracker gets updated
                lock (_routingLock)
                {
                    foreach (var handler in _npcHandlers.Values)
                    {
                        try
                        {
                            handler.OnTextMessage(json);
                        }
                        catch (Exception ex)
                        {
                            Debug.LogError($"[UnifiedWebSocket] Error broadcasting BufferHint to NPC handler: {ex.Message}");
                        }
                    }
                }

                // BufferHint has been handled, don't process further
                return;
            }

            // Check for error messages and log them prominently
            if (json.Contains("\"type\":\"Error\"") || json.Contains("\"type\":\"error\"") || json.Contains("\"type\":\"ConfigurationError\""))
            {
                try
                {
                    var errorData = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
                    if (errorData != null)
                    {
                        var errorType = errorData.ContainsKey("type") ? errorData["type"].ToString() : "Error";

                        // Backend can send error in either "message" or "error" field
                        var errorMessage = errorData.ContainsKey("message") && !string.IsNullOrEmpty(errorData["message"].ToString())
                            ? errorData["message"].ToString()
                            : errorData.ContainsKey("error") ? errorData["error"].ToString() : "Unknown error";

                        // Extract details field for more specific error information
                        var errorDetails = errorData.ContainsKey("details") && !string.IsNullOrEmpty(errorData["details"].ToString())
                            ? errorData["details"].ToString()
                            : null;

                        // Service can be explicit field, or derived from type/error
                        var service = errorData.ContainsKey("service") && !string.IsNullOrEmpty(errorData["service"].ToString())
                            ? errorData["service"].ToString()
                            : errorType; // Use error type as service identifier

                        var suggestion = errorData.ContainsKey("suggestion") ? errorData["suggestion"].ToString() : "";
                        var errorRequestId = errorData.ContainsKey("requestId") ? errorData["requestId"].ToString() : "No request ID";

                        // Special handling for configuration errors
                        if (errorType == "ConfigurationError")
                        {
                            // Log with special formatting for configuration errors
                            Debug.LogError($"\n" +
                                $"════════════════════════════════════════════════════════\n" +
                                $"⚠️ CONFIGURATION ERROR - {service}\n" +
                                $"════════════════════════════════════════════════════════\n" +
                                $"\n{errorMessage}\n" +
                                (!string.IsNullOrEmpty(errorDetails) ? $"Details: {errorDetails}\n" : "") +
                                $"Request ID: {errorRequestId}\n");

                            if (!string.IsNullOrEmpty(suggestion))
                            {
                                Debug.LogWarning($"\n💡 SOLUTION:\n{suggestion}\n" +
                                    $"════════════════════════════════════════════════════════\n");
                            }
                        }
                        else
                        {
                            // Regular error logging with request ID for debugging
                            Debug.LogError($"[BACKEND ERROR - {service}] {errorMessage}" +
                                (!string.IsNullOrEmpty(errorDetails) ? $"\nDetails: {errorDetails}" : "") +
                                $"\n(RequestId: {errorRequestId})");
                            if (!string.IsNullOrEmpty(suggestion))
                            {
                                Debug.LogWarning($"[SUGGESTION] {suggestion}");
                            }
                        }

                        // Also log full error for debugging
                        if (enableVerboseLogging)
                            Debug.Log($"[ERROR DETAILS] {json}");
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[UnifiedWebSocket] Failed to parse error message: {ex.Message}\nRaw message: {json}");
                }

                // Don't continue processing error messages
                return;
            }

            // LOG INCOMING MESSAGES (except verbose ones)
            if (!json.Contains("BufferHint") && !json.Contains("Transcription"))
            {
                if (enableVerboseLogging)
                    Debug.Log($"[UnifiedWebSocket] RECEIVED MESSAGE: {json.Substring(0, Math.Min(200, json.Length))}...");
            }

            // Extract RequestId from message
            var requestId = ExtractRequestId(json);

            if (string.IsNullOrEmpty(requestId))
            {
                // Broadcast messages without RequestId to all NPCs
                lock (_routingLock)
                {
                    foreach (var handler in _npcHandlers.Values)
                    {
                        try
                        {
                            handler.OnTextMessage(json);
                        }
                        catch (Exception ex)
                        {
                            Debug.LogError($"[UnifiedWebSocket] Error in NPC text handler: {ex.Message}");
                        }
                    }
                }
            }
            else
            {
                // Route to specific NPC
                INpcMessageHandler handler = null;
                lock (_routingLock)
                {
                    _npcHandlers.TryGetValue(requestId, out handler);
                }

                if (handler != null)
                {
                    try
                    {
                        handler.OnTextMessage(json);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[UnifiedWebSocket] Error in NPC text handler for {requestId}: {ex.Message}");
                    }
                }
                else if (enableVerboseLogging)
                {
                    Debug.LogWarning($"[UnifiedWebSocket] No handler for RequestId: {requestId}");
                }
            }
        }

        /// <summary>
        /// Handle incoming binary messages (audio) and route to appropriate NPC.
        /// STRICT MODE: All audio MUST be wrapped with RequestId.
        /// Routes to specific NPC only - no broadcast fallback.
        /// </summary>
        private void HandleBinaryMessage(byte[] data)
        {
            if (data == null || data.Length == 0)
                return;

            try
            {
                // Unwrap audio to extract RequestId (REQUIRED)
                var (requestId, audioData) = BinaryAudioWrapper.UnwrapAudioChunk(data);

                lock (_routingLock)
                {
                    // Route to specific NPC by RequestId
                    if (_npcHandlers.TryGetValue(requestId, out var handler))
                    {
                        try
                        {
                            handler.OnBinaryMessage(audioData);
                        }
                        catch (Exception ex)
                        {
                            Debug.LogError($"[UnifiedWebSocket] Error in NPC binary handler for RequestId {requestId}: {ex.Message}");
                        }
                    }
                    else
                    {
                        Debug.LogError($"[UnifiedWebSocket] No NPC handler registered for RequestId: {requestId}. Audio dropped.");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UnifiedWebSocket] Failed to unwrap audio data: {ex.Message}. Audio must be wrapped with RequestId!");
            }
        }

        /// <summary>
        /// Extract RequestId from JSON message
        /// </summary>
        private string ExtractRequestId(string json)
        {
            try
            {
                // Simple extraction without full deserialization for performance
                var requestIdIndex = json.IndexOf("\"requestId\"", StringComparison.OrdinalIgnoreCase);
                if (requestIdIndex == -1)
                    requestIdIndex = json.IndexOf("\"RequestId\"", StringComparison.OrdinalIgnoreCase);

                if (requestIdIndex > 0)
                {
                    var startQuote = json.IndexOf('"', requestIdIndex + 11);
                    var endQuote = json.IndexOf('"', startQuote + 1);

                    if (startQuote > 0 && endQuote > startQuote)
                    {
                        return json.Substring(startQuote + 1, endQuote - startQuote - 1);
                    }
                }
            }
            catch
            {
                // Ignore parsing errors
            }

            return null;
        }

        /// <summary>
        /// Handle WebSocket disconnection from WebSocketConnection (without code)
        /// </summary>
        private void HandleWebSocketDisconnection()
        {
            // WebSocketConnection doesn't provide the close code anymore
            HandleDisconnection(NativeWebSocket.WebSocketCloseCode.Normal);
        }

        /// <summary>
        /// Handle WebSocket disconnection
        /// </summary>
        private void HandleDisconnection(NativeWebSocket.WebSocketCloseCode code)
        {
            if (enableVerboseLogging)
                Debug.Log($"[UnifiedWebSocket] Disconnected: {code}");
            CleanupConnection();
            OnDisconnected?.Invoke(code);
        }

        /// <summary>
        /// Clean up WebSocket connection
        /// </summary>
        private void CleanupConnection()
        {
            if (_webSocket != null)
            {
                _webSocket.OnTextMessageReceived -= HandleTextMessage;
                _webSocket.OnBinaryMessageReceived -= HandleBinaryMessage;
                _webSocket.OnDisconnected -= HandleWebSocketDisconnection;
                _webSocket = null;
            }

            _currentUrl = null;
        }

        /// <summary>
        /// Wait for API key provider to be ready (if it's a caching provider like OrchestratorApiKeyProvider).
        ///
        /// OrchestratorApiKeyProvider waits for login and NetworkHelper initialization, then fetches the API key.
        /// This method polls the IsCached property to know when it's ready.
        /// Expected wait time: A few seconds after login (includes login time + API key fetch).
        /// </summary>
        private System.Collections.IEnumerator WaitForApiKeyProviderReady()
        {
            // Check if the provider component is OrchestratorApiKeyProvider (Extended package)
            // We use reflection to avoid tight coupling to Extended package
            if (apiKeyProviderComponent != null)
            {
                var providerType = apiKeyProviderComponent.GetType();
                var isCachedProperty = providerType.GetProperty("IsCached",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

                if (isCachedProperty != null)
                {
                    // This is a caching provider - wait for cache to be ready
                    // (Provider fetches API key via event-driven mechanism, not polling!)
                    float waitTime = 0f;
                    float maxWait = 15f; // Max 15 seconds wait (generous timeout)

                    while (waitTime < maxWait)
                    {
                        var isCached = (bool)isCachedProperty.GetValue(apiKeyProviderComponent);
                        if (isCached)
                        {
                            if (enableVerboseLogging)
                                Debug.Log($"[WebSocketClient] API key provider cache ready after {waitTime:F1}s");
                            yield break;
                        }

                        yield return new WaitForSeconds(0.1f);
                        waitTime += 0.1f;
                    }

                    Debug.LogWarning($"[WebSocketClient] API key provider cache not ready after {maxWait}s - proceeding anyway");
                }
            }
        }

        /// <summary>
        /// Initialize connection sequence for optimal performance.
        /// Handles both wake-up call (server warm-up) and connection establishment in parallel.
        /// </summary>
        private System.Collections.IEnumerator InitializeConnectionSequence()
        {
            // IMPORTANT: If using OrchestratorApiKeyProvider (or any caching provider),
            // wait for it to cache the API key before attempting connection establishment
            yield return WaitForApiKeyProviderReady();

            // Start BOTH operations in parallel for speed!
            // JWT pre-fetch and wake-up call can happen simultaneously

            Task jwtTask = null;

            // 1. Start JWT pre-fetch immediately (this takes 4+ seconds!)
            if (sendWakeUpCall || establishConnection)
            {
                if (enableVerboseLogging)
                    Debug.Log("[UnifiedWebSocket] Starting JWT pre-fetch in parallel...");
                jwtTask = PreFetchJwtToken();
            }

            // 2. Send wake-up call in parallel with JWT fetch
            if (sendWakeUpCall)
            {
                // Don't wait for this - let it run in parallel
                StartCoroutine(SendWakeUpCallAsync());
            }

            // 3. Wait a bit for JWT to complete (but not too long)
            if (jwtTask != null)
            {
                float waitTime = 0;
                while (!jwtTask.IsCompleted && waitTime < 2.0f)
                {
                    yield return new WaitForSeconds(0.1f);
                    waitTime += 0.1f;
                }

                if (jwtTask.IsCompleted && !jwtTask.IsFaulted)
                {
                    if (enableVerboseLogging)
                        Debug.Log("[UnifiedWebSocket] JWT pre-fetch completed successfully");
                }
            }

            // 4. Then establish connection (JWT should be cached by now)
            if (establishConnection)
            {
                yield return StartCoroutine(EstablishConnection());
            }
        }

        /// <summary>
        /// Send wake-up call to prevent Cloud Run cold start
        /// </summary>
        private System.Collections.IEnumerator SendWakeUpCallAsync()
        {
            yield return new WaitForSeconds(0.1f); // Small delay for scene setup

            var healthCheckUrl = apiBaseUrl.TrimEnd('/') + "/health";

            if (enableVerboseLogging)
                Debug.Log($"[UnifiedWebSocket] Sending wake-up call to: {healthCheckUrl}");

            using var request = UnityEngine.Networking.UnityWebRequest.Get(healthCheckUrl);
            request.timeout = 10;

            yield return request.SendWebRequest();

            if (request.result == UnityEngine.Networking.UnityWebRequest.Result.Success ||
                request.responseCode == 404)
            {
                if (enableVerboseLogging)
                    Debug.Log("[UnifiedWebSocket] Cloud Run service warmed up successfully");
                // JWT pre-fetch now happens in parallel in InitializeConnectionSequence
            }
            else
            {
                Debug.LogWarning($"[UnifiedWebSocket] Wake-up call failed: {request.error}");
            }
        }

        /// <summary>
        /// Pre-fetch JWT token to warm up cache and SSL/TLS
        /// </summary>
        private async Task PreFetchJwtToken()
        {
            try
            {
                var startTime = System.DateTime.Now;
                var token = await _authService.GetAuthTokenAsync(
                    "UnifiedConnection",
                    Constants.DefaultPlayerRole,
                    _apiKeyProvider.GetOrchestratorApiKey());

                var duration = (System.DateTime.Now - startTime).TotalMilliseconds;

                if (!string.IsNullOrEmpty(token))
                {
                    if (enableVerboseLogging)
                        Debug.Log($"[UnifiedWebSocket] JWT token pre-fetched successfully ({duration:F0}ms) - cached for future use");
                }
                else
                {
                    Debug.LogWarning($"[UnifiedWebSocket] Failed to pre-fetch JWT token ({duration:F0}ms)");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UnifiedWebSocket] Error pre-fetching JWT token: {ex.Message}");
            }
        }

        /// <summary>
        /// Establish WebSocket connection during scene initialization
        /// </summary>
        private System.Collections.IEnumerator EstablishConnection()
        {

            if (enableVerboseLogging)
                Debug.Log("[UnifiedWebSocket] Establishing WebSocket connection...");

            // Use the new public GetConnectionAsync method
            var connectionTask = GetConnectionAsync();

            // Wait for connection task
            while (!connectionTask.IsCompleted)
            {
                yield return null;
            }

            // Check for exceptions first before accessing Result
            if (connectionTask.IsFaulted)
            {
                Debug.LogError($"[UnifiedWebSocket] ❌ Connection failed: {connectionTask.Exception?.GetBaseException().Message}");
            }
            else if (connectionTask.IsCanceled)
            {
                Debug.LogWarning("[UnifiedWebSocket] ⚠️ Connection was cancelled");
            }
            else if (connectionTask.IsCompletedSuccessfully)
            {
                if (enableVerboseLogging)
                    Debug.Log("[UnifiedWebSocket] ✅ Connection established successfully");
            }
            else
            {
                Debug.LogWarning("[UnifiedWebSocket] ⚠️ Failed to establish connection");
            }
        }

        private void Update()
        {
            // Dispatch WebSocket message queue on Unity main thread
            _webSocket?.DispatchMessageQueue();
        }

        private void OnDestroy()
        {
            // Clear singleton instance
            if (_instance == this)
            {
                _instance = null;
            }

            // Cleanup on destroy
            if (_webSocket != null)
            {
                _ = _webSocket.DisconnectAsync();
            }

            CleanupConnection();
        }

        private void OnApplicationQuit()
        {
            OnDestroy();
        }
    }

    /// <summary>
    /// Interface for NPC message handlers
    /// </summary>
    public interface INpcMessageHandler
    {
        void OnTextMessage(string json);
        void OnBinaryMessage(byte[] data);
        void OnRequestComplete(string requestId);
    }

    /// <summary>
    /// Connection state enumeration
    /// </summary>
    public enum ConnectionState
    {
        Disconnected,
        Connecting,
        Connected
    }
}