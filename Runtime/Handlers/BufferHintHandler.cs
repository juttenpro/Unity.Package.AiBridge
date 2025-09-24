using System;
using SimulationCrew.AIBridge.Audio.Playback;
using UnityEngine;
using SimulationCrew.AIBridge.Messages;
using SimulationCrew.AIBridge.Core;

namespace SimulationCrew.AIBridge.Handlers
{
    /// <summary>
    /// Handles buffer hint messages from the server to optimize audio buffering.
    /// Updates latency tracking and manages adaptive buffer settings.
    /// Extracted from StreamingApiClient for better separation of concerns.
    /// </summary>
    public class BufferHintHandler
    {
        private readonly string _personaName;
        private readonly LatencyTracker _latencyTracker;
        private readonly bool _enableVerboseLogging;
        
        /// <summary>
        /// Event fired when a buffer hint is processed
        /// </summary>
        public event Action<BufferHintMessage> OnBufferHintProcessed;
        
        public BufferHintHandler(
            string personaName,
            LatencyTracker latencyTracker,
            bool enableVerboseLogging = false)
        {
            _personaName = personaName ?? "Unknown";
            _latencyTracker = latencyTracker; // Can be null if metrics are disabled
            _enableVerboseLogging = enableVerboseLogging;
        }
        
        /// <summary>
        /// Process a buffer hint message from the server
        /// </summary>
        /// <param name="message">The buffer hint containing latency and network quality info</param>
        public void ProcessBufferHint(BufferHintMessage message)
        {
            if (message == null)
            {
                Debug.LogWarning($"[{_personaName}] Received null buffer hint message");
                return;
            }
            
            // Update TTS latency in the latency tracker (if metrics are enabled)
            _latencyTracker?.UpdateTtsLatency(message.TtsLatencyMs, message.LatencyLevel);
            
            // Send buffer hint to centralized AdaptiveBufferManager
            // This ensures all NPCs share the same buffer settings based on network quality
            var bufferManager = AdaptiveBufferManager.Instance;
            if (bufferManager)
            {
                ProcessBufferHintInManager(bufferManager, message);
            }
            else
            {
                Debug.LogWarning($"[{_personaName}] Buffer hint received but AdaptiveBufferManager not available");
            }
            
            // Fire event for any additional handling
            OnBufferHintProcessed?.Invoke(message);
        }
        
        /// <summary>
        /// Process the buffer hint in the AdaptiveBufferManager
        /// </summary>
        private void ProcessBufferHintInManager(AdaptiveBufferManager bufferManager, BufferHintMessage message)
        {
            // Special handling for initial measurement
            if (IsInitialMeasurement(message))
            {
                // This is the initial network measurement from connection establishment
                bufferManager.ProcessInitialMeasurement((float)message.TtsLatencyMs);
                
                if (_enableVerboseLogging)
                {
                    Debug.Log($"[{_personaName}] Initial network measurement processed: {message.TtsLatencyMs}ms");
                }
            }
            else
            {
                // Regular buffer hint from TTS streaming
                bufferManager.ProcessBufferHint(
                    message.NetworkQuality,
                    message.RecommendedBufferSize,
                    message.TtsLatencyMs
                );
            }
            
            if (_enableVerboseLogging)
            {
                Debug.Log($"[{_personaName}] Buffer hint sent to manager: {message.NetworkQuality} network " +
                         $"(TTS: {message.TtsLatencyMs}ms)");
                Debug.Log($"[AdaptiveBufferManager] {bufferManager.GetBufferStats()}");
            }
        }
        
        /// <summary>
        /// Check if this is an initial network measurement
        /// </summary>
        private bool IsInitialMeasurement(BufferHintMessage message)
        {
            return message.RequestId == "initial-measurement" && message.SentenceIndex == -1;
        }
    }
}