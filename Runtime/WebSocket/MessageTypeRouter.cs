using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Tsc.AIBridge.WebSocket
{
    /// <summary>
    /// Routes inbound messages by their wire <c>type</c> to a capability that owns that type,
    /// independent of the conversation turn the message arrived on.
    ///
    /// WHY this exists next to the requestId routing in <see cref="WebSocketClient"/>: that map holds
    /// exactly ONE <c>INpcMessageHandler</c> per requestId — the NPC that owns the turn. Some messages
    /// are not the turn's to own. <c>prosodyresult</c> describes the PLAYER's voice; it carries the
    /// turn's requestId only because the player's audio happened to travel on that session. With no
    /// second slot available, such a consumer had to be bolted onto the active NpcClient, which then
    /// needed a scene reference to reach the rule system — and that reference is what silently died
    /// when a FeatureFilter destroyed the container it pointed into (2026-08-28).
    ///
    /// PRECEDENCE: <see cref="WebSocketClient"/> consults this router AFTER the protocol-level cases
    /// (bufferHint broadcast, error logging) and BEFORE requestId routing. Protocol messages are
    /// deliberately out of reach so a subscriber cannot hijack them; everything else falls through
    /// unchanged when no capability claims the type.
    /// </summary>
    internal sealed class MessageTypeRouter
    {
        private readonly Dictionary<string, List<Action<string>>> _subscribers =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new();

        /// <summary>Claim a wire type. Several consumers may share one type; each gets every message.</summary>
        public void Subscribe(string messageType, Action<string> handler)
        {
            if (string.IsNullOrEmpty(messageType) || handler == null) return;

            lock (_lock)
            {
                if (!_subscribers.TryGetValue(messageType, out var handlers))
                {
                    handlers = new List<Action<string>>();
                    _subscribers[messageType] = handlers;
                }

                if (!handlers.Contains(handler))
                    handlers.Add(handler);
            }
        }

        /// <summary>
        /// Release a claim. When the last handler for a type goes, the type is dropped entirely so
        /// messages fall back to requestId routing rather than being silently swallowed.
        /// </summary>
        public void Unsubscribe(string messageType, Action<string> handler)
        {
            if (string.IsNullOrEmpty(messageType) || handler == null) return;

            lock (_lock)
            {
                if (!_subscribers.TryGetValue(messageType, out var handlers)) return;

                handlers.Remove(handler);
                if (handlers.Count == 0)
                    _subscribers.Remove(messageType);
            }
        }

        /// <summary>
        /// Deliver <paramref name="json"/> to the capability owning its type.
        /// Returns true when this router owned the message — the caller must then stop routing it.
        ///
        /// Cost: a substring scan per claimed type, and a single parse only once one of them hits.
        /// The parse is not an optimisation detail but the correctness step: a transcript quoting
        /// "prosodyresult" must never be mistaken for the message itself, so the scan only ever
        /// nominates a candidate and the real <c>type</c> field decides.
        /// </summary>
        public bool TryDispatch(string json)
        {
            if (string.IsNullOrEmpty(json)) return false;

            List<Action<string>> targets = null;
            lock (_lock)
            {
                if (_subscribers.Count == 0) return false;
                if (!ContainsAnyClaimedType(json)) return false;

                var wireType = ReadMessageType(json);
                if (string.IsNullOrEmpty(wireType)) return false;
                if (!_subscribers.TryGetValue(wireType, out var handlers)) return false;

                targets = new List<Action<string>>(handlers);
            }

            foreach (var handler in targets)
            {
                try
                {
                    handler(json);
                }
                catch (Exception ex)
                {
                    // Warning, not Error: the host project treats LogError as fatal (it shows the
                    // restart popup and quits). A capability consumer blowing up must degrade that
                    // capability, never end the training session or the receive loop.
                    Debug.LogWarning($"[MessageTypeRouter] Subscriber for '{ReadMessageType(json)}' threw: {ex.Message}");
                }
            }

            return true;
        }

        // Caller holds _lock.
        private bool ContainsAnyClaimedType(string json)
        {
            foreach (var claimedType in _subscribers.Keys)
            {
                if (json.IndexOf(claimedType, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        private static string ReadMessageType(string json)
        {
            try
            {
                return JObject.Parse(json)["type"]?.ToString();
            }
            catch
            {
                // Malformed frame: not ours to claim. The existing routing logs protocol errors.
                return null;
            }
        }
    }
}
