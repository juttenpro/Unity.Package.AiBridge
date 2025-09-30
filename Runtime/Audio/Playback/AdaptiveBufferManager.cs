using UnityEngine;

namespace Tsc.AIBridge.Audio.Playback
{
    /// <summary>
    /// Centralized manager for adaptive audio buffering across all NPCs in the scene.
    /// Maintains a single source of truth for network quality and buffer recommendations
    /// that all StreamingAudioPlayers can reference.
    ///
    /// Architecture:
    /// - Singleton instance accessible scene-wide
    /// - Receives BufferHint messages from any NPC's connection
    /// - Calculates optimal buffer based on worst-case network conditions
    /// - All StreamingAudioPlayers query this manager for buffer settings
    ///
    /// This ensures consistent audio playback quality across all NPCs and prevents
    /// individual NPCs from having different buffer settings that could cause
    /// inconsistent user experience.
    /// </summary>
    public class AdaptiveBufferManager : MonoBehaviour
    {
        #region Singleton

        private static AdaptiveBufferManager _instance;

        /// <summary>
        /// Check if an instance exists without creating one
        /// </summary>
        public static bool HasInstance => _instance != null;

        public static AdaptiveBufferManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = FindFirstObjectByType<AdaptiveBufferManager>();
                    if (_instance == null)
                    {
                        Debug.LogError("[AdaptiveBufferManager] Not found in scene! Please add AdaptiveBufferManager to your scene.\n" +
                                      "GameObject > Create Empty > Add Component > AdaptiveBufferManager\n" +
                                      "Configure the buffer settings in the Inspector.");
                    }
                }
                return _instance;
            }
        }

        #endregion

        #region Configuration

        [Header("Buffer Configuration")]
        [SerializeField]
        [Tooltip("Target buffer duration in seconds - the optimal value for balanced latency/stability")]
        [Range(0.05f, 0.3f)]
        private float targetBufferDuration = 0.1f;

        [SerializeField]
        [Tooltip("Minimum buffer duration in seconds - absolute floor for perfect network")]
        [Range(0.03f, 0.1f)]
        private float minBufferDuration = 0.05f;

        [SerializeField]
        [Tooltip("Maximum buffer duration in seconds for poor network conditions")]
        [Range(0.2f, 2.0f)]
        private float maxBufferDuration = 0.5f;

        [SerializeField]
        [Tooltip("Enable adaptive buffering based on network quality")]
        private bool enableAdaptiveBuffering = true;

        [Header("Network Quality Thresholds")]
        [SerializeField]
        [Tooltip("TTS latency threshold for 'Excellent' network (ms)")]
        private float excellentThresholdMs = 500f;

        [SerializeField]
        [Tooltip("TTS latency threshold for 'Good' network (ms)")]
        private float goodThresholdMs = 1000f;

        [SerializeField]
        [Tooltip("TTS latency threshold for 'Fair' network (ms)")]
        private float fairThresholdMs = 2000f;

        [Header("Adaptation Settings")]
        [SerializeField]
        [Tooltip("How quickly to adapt to improving network conditions (0-1)")]
        [Range(0.1f, 1.0f)]
        private float improvementRate = 0.5f;

        [SerializeField]
        [Tooltip("How quickly to adapt to worsening network conditions (0-1)")]
        [Range(0.1f, 1.0f)]
        private float degradationRate = 0.8f;

        [Header("Debug Settings")]
        [SerializeField]
        private bool enableVerboseLogging;
        #endregion

        #region Events

        /// <summary>
        /// Event raised when buffer duration changes (broadcasts to all NPCs)
        /// </summary>
        public System.Action<float> OnBufferUpdateEvent;

        #endregion

        #region State

        // Current buffer recommendation
        private float _currentBufferDuration;
        private float _targetBufferDuration;

        // Network quality tracking
        private string _currentNetworkQuality = "Unknown";
        private float _averageTtsLatencyMs;
        private int _measurementCount;
        private float _worstCaseLatencyMs;

        // Stability tracking - prevent rapid changes
        private string _lastNetworkQuality = "Unknown";
        private int _consistentMeasurements;
        private const int STABILITY_THRESHOLD = 3; // Need 3 consistent measurements to change
        private float _lastMeasurementTime;

        // Timing
        private float _lastUpdateTime;
        private const float UPDATE_INTERVAL = 0.5f; // Smooth updates every 500ms

        #endregion

        #region Properties

        /// <summary>
        /// Current recommended buffer duration for all NPCs
        /// </summary>
        public float CurrentBufferDuration => _currentBufferDuration;

        /// <summary>
        /// Current network quality assessment
        /// </summary>
        public string NetworkQuality => _currentNetworkQuality;

        /// <summary>
        /// Whether adaptive buffering is enabled
        /// </summary>
        public bool IsAdaptiveBufferingEnabled => enableAdaptiveBuffering;

        /// <summary>
        /// Average TTS latency across all measurements
        /// </summary>
        public float AverageTtsLatencyMs => _averageTtsLatencyMs;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            // Singleton pattern - only one instance allowed
            if (_instance != null && _instance != this)
            {
                Debug.LogError($"[AdaptiveBufferManager] Multiple instances detected! Destroying duplicate on {gameObject.name}");
                Destroy(gameObject);
                return;
            }

            _instance = this;

            // Only use DontDestroyOnLoad if explicitly configured (optional)
            // You may want to keep it per-scene instead
            // DontDestroyOnLoad(gameObject);

            // Initialize with target buffer
            _currentBufferDuration = targetBufferDuration;
            _targetBufferDuration = targetBufferDuration;
            _lastUpdateTime = Time.time;

            if(enableVerboseLogging)
                Debug.Log($"[AdaptiveBufferManager] Initialized with target buffer: {targetBufferDuration:F3}s, max: {maxBufferDuration:F3}s");
        }

        private void Update()
        {
            if (!enableAdaptiveBuffering)
                return;

            // Only update when we have a target that differs from current
            // No more constant polling - event-driven approach
            if (Mathf.Abs(_targetBufferDuration - _currentBufferDuration) > 0.005f)
            {
                if (Time.time - _lastUpdateTime > UPDATE_INTERVAL)
                {
                    UpdateBufferSmooth();
                    _lastUpdateTime = Time.time;
                }
            }

            // Optional: Decay back to default after long period without measurements
            // Uncomment if you want the buffer to slowly return to default after inactivity
            /*
            const float DECAY_TIME = 30f; // 30 seconds without updates
            if (Time.time - _lastMeasurementTime > DECAY_TIME && _measurementCount > 0)
            {
                // Slowly decay back toward default
                _targetBufferDuration = Mathf.Lerp(_targetBufferDuration, baseBufferDuration, 0.1f * Time.deltaTime);
            }
            */
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Process a buffer hint from the backend (event-driven, not polling)
        /// Only adjusts buffer after consistent measurements to avoid rapid changes
        /// </summary>
        public void ProcessBufferHint(string networkQuality, string recommendedSize, double ttsLatencyMs)
        {
            if (!enableAdaptiveBuffering)
                return;

            _lastMeasurementTime = Time.time;

            // Check for stability - need consistent network quality before changing
            if (networkQuality == _lastNetworkQuality)
            {
                _consistentMeasurements++;
            }
            else
            {
                _lastNetworkQuality = networkQuality;
                _consistentMeasurements = 1;
            }

            // Update network quality assessment
            UpdateNetworkQuality(networkQuality, ttsLatencyMs);

            // Calculate new target buffer
            var newTarget = CalculateOptimalBuffer(networkQuality, recommendedSize, ttsLatencyMs);

            // Only apply changes after consistent measurements (stability threshold)
            if (_consistentMeasurements >= STABILITY_THRESHOLD || newTarget > _targetBufferDuration)
            {
                var previousTarget = _targetBufferDuration;

                // Immediate increase for poor network, gradual decrease for improvements
                if (newTarget > _targetBufferDuration)
                {
                    _targetBufferDuration = newTarget;
                }
                else if (newTarget < _targetBufferDuration)
                {
                    _targetBufferDuration = Mathf.Lerp(_targetBufferDuration, newTarget, improvementRate);
                }

                if (enableVerboseLogging && Mathf.Abs(_targetBufferDuration - previousTarget) > 0.01f)
                {
                    Debug.Log($"[AdaptiveBufferManager] Buffer adjusted: {previousTarget:F3}s → {_targetBufferDuration:F3}s " +
                             $"({networkQuality} network, {_consistentMeasurements}/{STABILITY_THRESHOLD} consistent)");
                }
            }
            else if (enableVerboseLogging)
            {
                Debug.Log($"[AdaptiveBufferManager] Awaiting stability: {_consistentMeasurements}/{STABILITY_THRESHOLD} consistent {networkQuality} measurements");
            }
        }

        /// <summary>
        /// Process initial network measurement from WebSocket connection
        /// </summary>
        public void ProcessInitialMeasurement(float estimatedRttMs)
        {
            if (!enableAdaptiveBuffering)
                return;

            // Calculate initial buffer based on RTT
            var initialBuffer = CalculateBufferFromRtt(estimatedRttMs);

            _currentBufferDuration = initialBuffer;
            _targetBufferDuration = initialBuffer;

            Debug.Log($"[AdaptiveBufferManager] Initial buffer set to {initialBuffer:F3}s based on {estimatedRttMs:F1}ms RTT");
        }

        /// <summary>
        /// Get buffer statistics for debugging
        /// </summary>
        public string GetBufferStats()
        {
            return $"Buffer: {_currentBufferDuration:F3}s (target: {_targetBufferDuration:F3}s), " +
                   $"Network: {_currentNetworkQuality}, " +
                   $"Avg TTS: {_averageTtsLatencyMs:F0}ms, " +
                   $"Worst: {_worstCaseLatencyMs:F0}ms, " +
                   $"Samples: {_measurementCount}";
        }

        /// <summary>
        /// Reset buffer to target settings
        /// </summary>
        public void ResetBuffer()
        {
            _currentBufferDuration = targetBufferDuration;
            _targetBufferDuration = targetBufferDuration;
            _averageTtsLatencyMs = 0f;
            _measurementCount = 0;
            _worstCaseLatencyMs = 0f;
            _currentNetworkQuality = "Unknown";

            if (enableVerboseLogging)
            {
                Debug.Log($"[AdaptiveBufferManager] Buffer reset to {targetBufferDuration:F3}s");
            }
        }

        #endregion

        #region Private Methods

        private void UpdateNetworkQuality(string quality, double latencyMs)
        {
            _currentNetworkQuality = quality;

            // Update running average
            _measurementCount++;
            _averageTtsLatencyMs = (_averageTtsLatencyMs * (_measurementCount - 1) + (float)latencyMs) / _measurementCount;

            // Track worst case
            _worstCaseLatencyMs = Mathf.Max(_worstCaseLatencyMs, (float)latencyMs);
        }

        private float CalculateOptimalBuffer(string networkQuality, string recommendedSize, double latencyMs)
        {
            // Start with base calculation from network quality
            var multiplier = networkQuality switch
            {
                "Excellent" => 1.0f,
                "Good" => 1.5f,
                "Fair" => 2.0f,
                "Poor" => 3.0f,
                _ => 2.0f
            };

            // Adjust based on recommended size
            multiplier *= recommendedSize switch
            {
                "Small" => 0.8f,
                "Medium" => 1.0f,
                "Large" => 1.3f,
                _ => 1.0f
            };

            // Additional adjustment based on actual latency using configurable thresholds
            if (latencyMs > fairThresholdMs)
                multiplier *= 1.5f;
            else if (latencyMs > goodThresholdMs)
                multiplier *= 1.2f;
            else if (latencyMs <= excellentThresholdMs)
                multiplier *= 0.9f; // Slightly reduce buffer for excellent network

            var buffer = targetBufferDuration * multiplier;
            return Mathf.Clamp(buffer, minBufferDuration, maxBufferDuration);
        }

        private float CalculateBufferFromRtt(float rttMs)
        {
            // Formula: target + (RTT * multiplier) + jitter margin
            const float rttMultiplier = 0.0015f; // 1.5ms buffer per 1ms RTT
            const float jitterMargin = 0.05f; // 50ms base jitter margin

            var buffer = targetBufferDuration + (rttMs * rttMultiplier) + jitterMargin;
            return Mathf.Clamp(buffer, minBufferDuration, maxBufferDuration);
        }

        private void UpdateBufferSmooth()
        {
            // Smooth transition to target buffer
            var rate = _targetBufferDuration > _currentBufferDuration ? degradationRate : improvementRate;
            _currentBufferDuration = Mathf.Lerp(_currentBufferDuration, _targetBufferDuration, rate * UPDATE_INTERVAL);

            // Broadcast buffer update via event instead of Unity Atoms
            OnBufferUpdateEvent?.Invoke(_currentBufferDuration);

            if (enableVerboseLogging)
            {
                Debug.Log($"[AdaptiveBufferManager] Broadcasting buffer update: {_currentBufferDuration:F3}s via events");
            }
        }

        #endregion
    }
}