using System;
using System.Collections.Generic;
using UnityEngine;

namespace Tsc.AIBridge.Core
{
    /// <summary>
    /// Routes WebSocket messages to the correct NPC based on RequestId.
    /// Thread-safe singleton that manages message routing without MonoBehaviour overhead.
    /// </summary>
    public class NpcMessageRouter
    {
        #region Singleton

        private static readonly object Lock = new();
        private static NpcMessageRouter _instance;

        /// <summary>
        /// Check if an instance exists without creating one
        /// </summary>
        public static bool HasInstance => _instance != null;

        /// <summary>
        /// Thread-safe singleton instance
        /// </summary>
        public static NpcMessageRouter Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (Lock)
                    {
                        if (_instance == null)
                        {
                            _instance = new NpcMessageRouter();
                            //Debug.Log("[NpcMessageRouter] Created singleton instance");
                        }
                    }
                }
                return _instance;
            }
        }

        /// <summary>
        /// Private constructor for singleton pattern
        /// </summary>
        private NpcMessageRouter()
        {
            // Initialize dictionaries
            _activeNpcsByRequestId = new Dictionary<string, NpcClientBase>();
            _npcsByName = new Dictionary<string, NpcClientBase>();
        }

        #endregion

        #region Private Fields

        // Track active NPCs and their current RequestId
        private Dictionary<string, NpcClientBase> _activeNpcsByRequestId;
        private Dictionary<string, NpcClientBase> _npcsByName;

        #endregion

        #region Public API

        /// <summary>
        /// Register an NPC client for message routing
        /// </summary>
        public void RegisterNpc(NpcClientBase npcClient)
        {
            if (npcClient == null) return;

            var npcName = npcClient.NpcName;
            if (!_npcsByName.ContainsKey(npcName))
            {
                _npcsByName[npcName] = npcClient;
                Debug.Log($"[NpcMessageRouter] Registered NPC: {npcName}");
            }
        }

        /// <summary>
        /// Unregister an NPC client
        /// </summary>
        public void UnregisterNpc(NpcClientBase npcClient)
        {
            if (npcClient == null) return;

            var npcName = npcClient.NpcName;
            if (_npcsByName.ContainsKey(npcName))
            {
                _npcsByName.Remove(npcName);

                // Also remove from active requests
                var keysToRemove = new List<string>();
                foreach (var kvp in _activeNpcsByRequestId)
                {
                    if (kvp.Value == npcClient)
                    {
                        keysToRemove.Add(kvp.Key);
                    }
                }
                foreach (var key in keysToRemove)
                {
                    _activeNpcsByRequestId.Remove(key);
                }

                //Debug.Log($"[NpcMessageRouter] Unregistered NPC: {npcName}");
            }
        }

        /// <summary>
        /// Associate a RequestId with an NPC for message routing
        /// </summary>
        public void SetActiveRequest(string requestId, string npcName)
        {
            if (string.IsNullOrEmpty(requestId) || string.IsNullOrEmpty(npcName))
                return;

            if (_npcsByName.TryGetValue(npcName, out var npcClient))
            {
                _activeNpcsByRequestId[requestId] = npcClient;
                //Debug.Log($"[NpcMessageRouter] Set active request {requestId} for NPC: {npcName}");
            }
            else
            {
                Debug.LogWarning($"[NpcMessageRouter] NPC not found: {npcName}");
            }
        }

        /// <summary>
        /// Route a WebSocket message to the appropriate NPC or system handler
        /// </summary>
        /// <param name="json">The JSON message to route</param>
        /// <param name="requestId">Optional RequestId for targeted routing</param>
        /// <param name="bufferHintOnly">If true, only process BufferHint messages and skip NPC routing</param>
        public void RouteMessage(string json, string requestId = null, bool bufferHintOnly = false)
        {
            if (string.IsNullOrEmpty(json))
                return;

            // Check if this is a BufferHint message - route to AdaptiveBufferManager
            // AdaptiveBufferManager: Adjusts audio buffer based on network quality
            var isBufferHint = IsBufferHintMessage(json);
            if (isBufferHint)
            {
                RouteBufferHintToAdaptiveBuffer(json);

                // If bufferHintOnly flag is set, don't route to NPC (prevents duplicate routing)
                if (bufferHintOnly)
                    return;

                // Otherwise continue to also route to NPC for TTS latency tracking
            }

            // Skip NPC routing if bufferHintOnly flag is set
            if (bufferHintOnly)
                return;

            // Try to extract RequestId from message if not provided
            if (string.IsNullOrEmpty(requestId))
            {
                requestId = ExtractRequestId(json);
            }

            // Find the target NPC

            if (!string.IsNullOrEmpty(requestId) && _activeNpcsByRequestId.TryGetValue(requestId, out var targetNpc))
            {
                // Route to specific NPC based on RequestId
                if (targetNpc.MetadataHandler != null)
                {
                    targetNpc.MetadataHandler.ProcessMessage(json);
                }
                else
                {
                    Debug.LogWarning($"[NpcMessageRouter] NPC {targetNpc.NpcName} has no metadata handler");
                }
            }
            else
            {
                // Fallback: Route to first active NPC (for backwards compatibility)
                foreach (var npc in _npcsByName.Values)
                {
                    if (npc.IsActive && npc.MetadataHandler != null)
                    {
                        npc.MetadataHandler.ProcessMessage(json);
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Clear request association when session ends
        /// </summary>
        public void ClearRequest(string requestId)
        {
            if (!string.IsNullOrEmpty(requestId) && _activeNpcsByRequestId.ContainsKey(requestId))
            {
                _activeNpcsByRequestId.Remove(requestId);
                //Debug.Log($"[NpcMessageRouter] Cleared request: {requestId}");
            }
        }

        /// <summary>
        /// Get the active RequestId for a given NPC name.
        /// Returns null if the NPC has no active request.
        /// </summary>
        /// <param name="npcName">The name of the NPC</param>
        /// <returns>The active RequestId, or null if not found</returns>
        public string GetActiveRequestForNpc(string npcName)
        {
            if (string.IsNullOrEmpty(npcName))
                return null;

            // Find the NPC client by name
            if (!_npcsByName.TryGetValue(npcName, out var npcClient))
                return null;

            // Find the RequestId that maps to this NPC client (reverse lookup)
            foreach (var kvp in _activeNpcsByRequestId)
            {
                if (kvp.Value == npcClient)
                {
                    return kvp.Key; // Return the requestId
                }
            }

            return null; // No active request for this NPC
        }

        /// <summary>
        /// Clear all registrations and reset state.
        /// Useful for testing or when entering/exiting play mode.
        /// </summary>
        public void Reset()
        {
            _activeNpcsByRequestId.Clear();
            _npcsByName.Clear();
            //Debug.Log("[NpcMessageRouter] Reset - all NPCs and requests cleared");
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Try to extract RequestId from JSON message
        /// </summary>
        private string ExtractRequestId(string json)
        {
            try
            {
                // Simple extraction without full deserialization
                // Look for "requestId":"..." or "RequestId":"..."
                var requestIdIndex = json.IndexOf("\"requestId\":", StringComparison.Ordinal);
                if (requestIdIndex < 0)
                    requestIdIndex = json.IndexOf("\"RequestId\":", StringComparison.Ordinal);

                if (requestIdIndex >= 0)
                {
                    var startQuote = json.IndexOf('"', requestIdIndex + 12);
                    if (startQuote >= 0)
                    {
                        var endQuote = json.IndexOf('"', startQuote + 1);
                        if (endQuote > startQuote)
                        {
                            return json.Substring(startQuote + 1, endQuote - startQuote - 1);
                        }
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
        /// Check if this is a BufferHint message
        /// </summary>
        private bool IsBufferHintMessage(string json)
        {
            // Quick check without full deserialization
            // Backend sends "bufferHint" (camelCase)
            return json.Contains("\"type\":\"bufferHint\"") ||
                   json.Contains("\"type\":\"BufferHint\"");
        }

        /// <summary>
        /// Route BufferHint messages directly to AdaptiveBufferManager
        /// Backend already calculates optimal buffer recommendations - we just apply them
        /// </summary>
        private void RouteBufferHintToAdaptiveBuffer(string json)
        {
            try
            {
                // Only process if AdaptiveBufferManager exists (optional component)
                if (!Audio.Playback.AdaptiveBufferManager.HasInstance)
                {
                    return; // Silently skip - AdaptiveBufferManager is optional
                }

                // Deserialize the BufferHint message
                var bufferHint = Newtonsoft.Json.JsonConvert.DeserializeObject<Messages.BufferHintMessage>(json);
                if (bufferHint != null)
                {
                    // Route directly to AdaptiveBufferManager - it knows what to do with backend recommendations
                    var bufferManager = Audio.Playback.AdaptiveBufferManager.Instance;
                    bufferManager?.ProcessBufferHint(
                        bufferHint.NetworkQuality,
                        bufferHint.RecommendedBufferSize,
                        bufferHint.TtsLatencyMs
                    );
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[NpcMessageRouter] Error processing BufferHint: {ex.Message}");
            }
        }

        #endregion
    }
}