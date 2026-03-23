using System;
using UnityEngine;
using Tsc.AIBridge.Messages;
using Tsc.AIBridge.Core;

namespace Tsc.AIBridge.Handlers
{
    /// <summary>
    /// Handles NoTranscript messages from the WebSocket when speech detection fails.
    /// Manages proper session continuation during interruptions.
    /// Extracted from StreamingApiClient for better separation of concerns.
    /// </summary>
    public class NoTranscriptHandler
    {
        private readonly string _personaName;
        private readonly bool _enableVerboseLogging;
        
        /// <summary>
        /// Event fired when no transcript is detected in a non-interruption scenario
        /// </summary>
        public event Action<NoTranscriptMessage> OnNoTranscriptProcessed;
        
        public NoTranscriptHandler(
            string personaName,
            bool enableVerboseLogging = false)
        {
            _personaName = personaName ?? "Unknown";
            _enableVerboseLogging = enableVerboseLogging;
        }
        
        /// <summary>
        /// Process a NoTranscript message from the server
        /// </summary>
        /// <param name="message">The NoTranscript message containing reason and duration</param>
        public void ProcessNoTranscript(NoTranscriptMessage message)
        {
            if (message == null)
            {
                Debug.LogWarning($"[{_personaName}] Received null NoTranscript message");
                return;
            }
            
            Debug.LogWarning($"[{_personaName}] No transcript detected - Reason: {message.Reason}, " +
                           $"Duration: {message.AudioDuration}ms, Provider: {message.SttProvider}");
            
            // CRITICAL: Check if this is during an interruption attempt
            // If an interruption just occurred, the session should continue for the NPC response
            // The "no transcript" is expected during interruption (user briefly speaks to trigger it)
            if (IsInterruptionActive())
            {
                Debug.Log($"[{_personaName}] No transcript during interruption - session continues for NPC response");
                // DON'T complete the session - it needs to continue for the response
                return;
            }
            
            // Just log - let the SendLoop timeout handle session completion if needed
            // The backend will send conversationComplete when it's done
            Debug.Log($"[{_personaName}] No transcript received - SendLoop timeout will handle session completion if no response comes");
            
            // Fire event for any additional handling needed
            OnNoTranscriptProcessed?.Invoke(message);
        }
        
        /// <summary>
        /// Check if an interruption is currently active via RequestOrchestrator
        /// </summary>
        private bool IsInterruptionActive()
        {
            if (!RequestOrchestrator.HasInstance)
            {
                if (_enableVerboseLogging)
                    Debug.Log($"[{_personaName}] No RequestOrchestrator - assuming no interruption");
                return false;
            }

            return RequestOrchestrator.Instance.IsInterruptionActive();
        }
    }
}