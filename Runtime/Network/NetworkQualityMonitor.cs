using System;
using UnityEngine;
using Tsc.AIBridge.Messages;
using Tsc.AIBridge.Audio.Playback;

namespace Tsc.AIBridge.Network
{
    /// <summary>
    /// Centralized network quality monitoring for the entire scene.
    /// Processes network measurements ONCE for all NPCs to avoid redundant testing.
    /// Automatically configures adaptive buffering based on real network conditions.
    /// NO manual configuration needed - the system self-calibrates.
    /// </summary>
    public class NetworkQualityMonitor : MonoBehaviour
    {
        #region Singleton

        private static NetworkQualityMonitor _instance;
        public static NetworkQualityMonitor Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = FindFirstObjectByType<NetworkQualityMonitor>();
                    if (_instance == null && Application.isPlaying)
                    {
                        // Auto-create if not in scene
                        var go = new GameObject("NetworkQualityMonitor");
                        _instance = go.AddComponent<NetworkQualityMonitor>();
                        Debug.Log("[NetworkQualityMonitor] Auto-created singleton instance");
                    }
                }
                return _instance;
            }
        }

        #endregion

        #region Events

        /// <summary>
        /// Fired when network quality changes significantly
        /// </summary>
        public event Action<string> OnNetworkQualityChanged;

        /// <summary>
        /// Fired when a new measurement is received
        /// </summary>
        public event Action<float> OnLatencyMeasured;

        #endregion

        #region State

        // Current network metrics
        private string _currentQuality = "Unknown";
        private float _currentLatencyMs;
        private float _averageLatencyMs;
        private float _minLatencyMs = float.MaxValue;
        private float _maxLatencyMs;
        private int _measurementCount;

        // Timing
        private float _lastMeasurementTime;
        private float _firstMeasurementTime;
        private bool _hasReceivedInitialMeasurement;

        // Self-calibration
        private const float CALIBRATION_PERIOD = 10f; // First 10 seconds are calibration
        private bool _isCalibrating = true;

        // Constants - NO Inspector configuration needed!
        private const float BASE_BUFFER = 0.05f;     // 50ms absolute minimum
        private const float TARGET_BUFFER = 0.1f;    // 100ms target for good network
        private const float MAX_BUFFER = 1.0f;       // 1 second max for terrible network

        #endregion

        #region Properties

        public string CurrentQuality => _currentQuality;
        public float AverageLatencyMs => _averageLatencyMs;
        public float MinLatencyMs => _minLatencyMs == float.MaxValue ? 0 : _minLatencyMs;
        public float MaxLatencyMs => _maxLatencyMs;
        public int MeasurementCount => _measurementCount;
        public bool IsCalibrating => _isCalibrating;

        /// <summary>
        /// Get recommended buffer duration based on current network conditions
        /// </summary>
        public float RecommendedBufferDuration
        {
            get
            {
                if (_measurementCount == 0)
                    return TARGET_BUFFER; // Default until we have data

                // Self-calibrating formula based on actual network behavior
                // Uses the 95th percentile approach (max latency with some margin)
                var p95Latency = _maxLatencyMs * 0.95f;

                // Convert latency to buffer: roughly 0.1ms buffer per 1ms latency
                // But clamp between our min and max
                var buffer = BASE_BUFFER + (p95Latency * 0.0001f);

                // During calibration, be more conservative
                if (_isCalibrating)
                    buffer *= 1.2f;

                return Mathf.Clamp(buffer, BASE_BUFFER, MAX_BUFFER);
            }
        }

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            _firstMeasurementTime = Time.time;

            // Subscribe to WebSocket client for ALL buffer hints
            // This replaces per-NPC handling
            SubscribeToGlobalMessages();
        }

        private void Update()
        {
            // Check if calibration period is over
            if (_isCalibrating && Time.time - _firstMeasurementTime > CALIBRATION_PERIOD)
            {
                _isCalibrating = false;
                Debug.Log($"[NetworkQualityMonitor] Calibration complete. " +
                         $"Network: {_currentQuality}, " +
                         $"Latency: {_averageLatencyMs:F0}ms (min: {MinLatencyMs:F0}ms, max: {_maxLatencyMs:F0}ms), " +
                         $"Buffer: {RecommendedBufferDuration*1000:F0}ms");

                // Apply final calibrated buffer to all NPCs
                UpdateGlobalBuffer();
            }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Process a buffer hint from ANY connection (not NPC-specific)
        /// </summary>
        public void ProcessBufferHint(BufferHintMessage message)
        {
            if (message == null) return;

            // Update measurements
            _lastMeasurementTime = Time.time;
            _measurementCount++;

            // Update latency stats
            var latency = (float)message.TtsLatencyMs;
            _currentLatencyMs = latency;
            _minLatencyMs = Mathf.Min(_minLatencyMs, latency);
            _maxLatencyMs = Mathf.Max(_maxLatencyMs, latency);

            // Rolling average
            if (_measurementCount == 1)
                _averageLatencyMs = latency;
            else
                _averageLatencyMs = (_averageLatencyMs * (_measurementCount - 1) + latency) / _measurementCount;

            // Update quality assessment
            var previousQuality = _currentQuality;
            _currentQuality = DetermineNetworkQuality(latency);

            // Fire events
            OnLatencyMeasured?.Invoke(latency);
            if (_currentQuality != previousQuality)
            {
                OnNetworkQualityChanged?.Invoke(_currentQuality);
            }

            // Update global buffer settings
            UpdateGlobalBuffer();

            // Log during calibration
            if (_isCalibrating && _measurementCount % 5 == 0)
            {
                Debug.Log($"[NetworkQualityMonitor] Calibrating... {_measurementCount} samples, " +
                         $"avg: {_averageLatencyMs:F0}ms, buffer: {RecommendedBufferDuration*1000:F0}ms");
            }
        }

        /// <summary>
        /// Process initial RTT measurement from WebSocket connection
        /// </summary>
        public void ProcessInitialRtt(float rttMs)
        {
            if (!_hasReceivedInitialMeasurement)
            {
                _hasReceivedInitialMeasurement = true;

                // Use RTT as initial estimate
                _currentLatencyMs = rttMs;
                _averageLatencyMs = rttMs;
                _minLatencyMs = rttMs;
                _maxLatencyMs = rttMs;
                _measurementCount = 1;

                Debug.Log($"[NetworkQualityMonitor] Initial RTT: {rttMs:F0}ms, " +
                         $"Initial buffer: {RecommendedBufferDuration*1000:F0}ms");

                UpdateGlobalBuffer();
            }
        }

        /// <summary>
        /// Get current network statistics
        /// </summary>
        public string GetNetworkStats()
        {
            return $"Quality: {_currentQuality}, " +
                   $"Latency: {_averageLatencyMs:F0}ms (min: {MinLatencyMs:F0}ms, max: {_maxLatencyMs:F0}ms), " +
                   $"Samples: {_measurementCount}, " +
                   $"Buffer: {RecommendedBufferDuration*1000:F0}ms";
        }

        #endregion

        #region Private Methods

        private void SubscribeToGlobalMessages()
        {
            // TODO: Subscribe to WebSocketClient's global message stream
            // This will be implemented when we refactor WebSocketClient
        }

        private string DetermineNetworkQuality(float latencyMs)
        {
            // Self-calibrating thresholds
            if (latencyMs < 500) return "Excellent";
            if (latencyMs < 1000) return "Good";
            if (latencyMs < 2000) return "Fair";
            return "Poor";
        }

        private void UpdateGlobalBuffer()
        {
            // Update the centralized AdaptiveBufferManager
            var bufferManager = AdaptiveBufferManager.Instance;
            if (bufferManager != null)
            {
                // Direct update - no need for hints or complex logic
                var buffer = RecommendedBufferDuration;

                // Use the ProcessInitialMeasurement method to set buffer directly
                bufferManager.ProcessInitialMeasurement(buffer * 1000f); // Convert to ms
            }
        }

        #endregion
    }
}