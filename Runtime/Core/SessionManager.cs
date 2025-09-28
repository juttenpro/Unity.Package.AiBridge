using UnityEngine;
using System.Collections.Generic;
using System.Linq;

namespace Tsc.AIBridge.Core
{
    /// <summary>
    /// Session manager - tracks all active conversation sessions.
    /// Simple singleton pattern without MonoBehaviour.
    /// Uses dictionary approach: session exists = session is active.
    /// No complex lifecycle management, no adapters, no interfaces.
    /// SIMPLICITY FIRST!
    /// </summary>
    public class SessionManager
    {
        private static SessionManager _instance;
        private static readonly object Lock = new object();
        
        /// <summary>
        /// Singleton instance
        /// </summary>
        public static SessionManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (Lock)
                    {
                        if (_instance == null)
                        {
                            _instance = new SessionManager();
                        }
                    }
                }
                return _instance;
            }
        }
        
        /// <summary>
        /// Private constructor for singleton
        /// </summary>
        private SessionManager()
        {
            // Private constructor to enforce singleton
        }
        
        /// <summary>
        /// Dictionary of all active sessions by SessionId
        /// Session exists in dictionary = session is active
        /// </summary>
        internal readonly Dictionary<string, ConversationSession> ActiveSessions = new();
        
        /// <summary>
        /// The most recently created session (for backwards compatibility)
        /// </summary>
        public ConversationSession CurrentSession => ActiveSessions.Values.LastOrDefault();
        
        /// <summary>
        /// Get or create a session for the given NPC.
        /// Continues with existing session during interruption.
        /// </summary>
        public ConversationSession GetOrCreateSession(string npcName)
        {
            // Find most recent active session for this NPC
            var existingSession = ActiveSessions.Values
                .Where(s => s.NpcName == npcName && s.State == SessionState.Active)
                .OrderByDescending(s => s.StartTime)
                .FirstOrDefault();
            
            // During interruption, continue with existing session
            if (existingSession != null && existingSession.IsInterruptionActive)
            {
                //Debug.Log($"[SessionManager] Continuing interrupted session {existingSession.SessionId}");
                return existingSession;
            }
            
            // If we have an active session, continue with it
            if (existingSession != null)
            {
                Debug.LogWarning($"[SessionManager] ⚠️ REUSING active session {existingSession.SessionId} for {npcName}");
                Debug.LogWarning($"[SessionManager] This indicates StartAudioRequest was called without EndAudioRequest!");
                Debug.LogWarning($"[SessionManager] Session state: {existingSession.State}, ChunksSent: {existingSession.ChunksSent}");
                return existingSession;
            }
            
            // Otherwise create new session
            return CreateNewSession(npcName);
        }
        
        /// <summary>
        /// Create a new session, canceling any existing ones for this NPC
        /// </summary>
        public ConversationSession CreateNewSession(string npcName)
        {
            // Cancel previous sessions for this NPC
            var existingSessions = ActiveSessions.Values
                .Where(s => s.NpcName == npcName)
                .ToList();
            
            foreach (var session in existingSessions)
            {
                session.Cancel();
                ActiveSessions.Remove(session.SessionId);
                //Debug.Log($"[SessionManager] Cancelled existing session {session.SessionId}");
            }
            
            // Create new session
            var newSession = new ConversationSession(npcName);
            ActiveSessions[newSession.SessionId] = newSession;

            //Debug.Log($"[SessionManager] ✨ NEW SESSION: {newSession.SessionId} for {npcName} (Active sessions: {_activeSessions.Count})");
            
            return newSession;
        }
        
        /// <summary>
        /// Mark interruption as started for a specific session
        /// </summary>
        public void StartInterruption(string sessionId = null)
        {
            var session = GetSession(sessionId) ?? CurrentSession;
            if (session != null)
            {
                session.IsInterruptionActive = true;
                //Debug.Log($"[SessionManager] Interruption started for session {session.SessionId}");
            }
        }
        
        /// <summary>
        /// Mark interruption as ended for a specific session
        /// </summary>
        public void EndInterruption(string sessionId = null)
        {
            var session = GetSession(sessionId) ?? CurrentSession;
            if (session != null)
            {
                session.IsInterruptionActive = false;
                //Debug.Log($"[SessionManager] Interruption ended for session {session.SessionId}");
            }
        }
        
        /// <summary>
        /// Complete a specific session or the most recent one
        /// </summary>
        public void CompleteSession(string sessionId = null)
        {
            var session = GetSession(sessionId) ?? CurrentSession;
            if (session != null)
            {
                session.Complete();
                ActiveSessions.Remove(session.SessionId);
                //Debug.Log($"[SessionManager] Removed completed session {session.SessionId} from active sessions");
            }
        }
        
        /// <summary>
        /// Complete the current session (backwards compatibility)
        /// </summary>
        public void CompleteCurrentSession()
        {
            CompleteSession();
        }
        
        /// <summary>
        /// Get a specific session by ID
        /// </summary>
        public ConversationSession GetSession(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
                return null;
                
            ActiveSessions.TryGetValue(sessionId, out var session);
            return session;
        }
        
        /// <summary>
        /// Get all active sessions
        /// </summary>
        public IEnumerable<ConversationSession> GetActiveSessions()
        {
            return ActiveSessions.Values.AsEnumerable();
        }
        
        /// <summary>
        /// Clean up completed/cancelled sessions periodically
        /// </summary>
        public void CleanupInactiveSessions()
        {
            var inactiveSessions = ActiveSessions.Values
                .Where(s => s.State == SessionState.Completed || s.State == SessionState.Cancelled)
                .ToList();
            
            foreach (var session in inactiveSessions)
            {
                ActiveSessions.Remove(session.SessionId);
                //Debug.Log($"[SessionManager] Cleaned up inactive session {session.SessionId}");
            }
        }
        
        /// <summary>
        /// Reset the session manager (useful for testing or scene changes)
        /// </summary>
        public static void Reset()
        {
            lock (Lock)
            {
                if (_instance != null)
                {
                    // Complete all active sessions
                    foreach (var session in _instance.ActiveSessions.Values.ToList())
                    {
                        session.Complete();
                    }
                    _instance.ActiveSessions.Clear();
                }
                _instance = null;
            }
        }
    }
}