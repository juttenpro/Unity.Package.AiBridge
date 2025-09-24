using System.Collections.Generic;
using UnityEngine;
using SimulationCrew.AIBridge.WebSocket;

namespace SimulationCrew.AIBridge.Core
{
    /// <summary>
    /// Routes WebSocket messages to the correct NPC based on RequestId.
    /// Replaces the old NpcMessageHandler functionality.
    /// </summary>
    public class NpcMessageRouter : MonoBehaviour
    {
        #region Singleton

        private static NpcMessageRouter _instance;
        public static NpcMessageRouter Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = FindFirstObjectByType<NpcMessageRouter>();
                    if (_instance == null && Application.isPlaying)
                    {
                        var go = new GameObject("NpcMessageRouter");
                        _instance = go.AddComponent<NpcMessageRouter>();
                        DontDestroyOnLoad(go);
                        Debug.Log("[NpcMessageRouter] Created singleton instance");
                    }
                }
                return _instance;
            }
        }

        #endregion

        #region Private Fields

        // Track active NPCs and their current RequestId
        private Dictionary<string, NpcClientBase> _activeNpcsByRequestId = new Dictionary<string, NpcClientBase>();
        private Dictionary<string, NpcClientBase> _npcsByName = new Dictionary<string, NpcClientBase>();

        #endregion

        #region Lifecycle

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
            }
        }

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

                Debug.Log($"[NpcMessageRouter] Unregistered NPC: {npcName}");
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
                Debug.Log($"[NpcMessageRouter] Set active request {requestId} for NPC: {npcName}");
            }
            else
            {
                Debug.LogWarning($"[NpcMessageRouter] NPC not found: {npcName}");
            }
        }

        /// <summary>
        /// Route a WebSocket message to the appropriate NPC
        /// </summary>
        public void RouteMessage(string json, string requestId = null)
        {
            if (string.IsNullOrEmpty(json))
                return;

            // Try to extract RequestId from message if not provided
            if (string.IsNullOrEmpty(requestId))
            {
                requestId = ExtractRequestId(json);
            }

            // Find the target NPC
            NpcClientBase targetNpc = null;

            if (!string.IsNullOrEmpty(requestId) && _activeNpcsByRequestId.TryGetValue(requestId, out targetNpc))
            {
                // Route to specific NPC based on RequestId
                if (targetNpc._metadataHandler != null)
                {
                    targetNpc._metadataHandler.ProcessMessage(json);
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
                    if (npc.IsActive && npc._metadataHandler != null)
                    {
                        npc._metadataHandler.ProcessMessage(json);
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
                Debug.Log($"[NpcMessageRouter] Cleared request: {requestId}");
            }
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
                var requestIdIndex = json.IndexOf("\"requestId\":");
                if (requestIdIndex < 0)
                    requestIdIndex = json.IndexOf("\"RequestId\":");

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

        #endregion
    }
}