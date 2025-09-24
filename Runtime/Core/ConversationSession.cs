using System;
using UnityEngine;

namespace SimulationCrew.AIBridge.Core
{
    /// <summary>
    /// State of a conversation session
    /// </summary>
    public enum SessionState
    {
        /// <summary>
        /// Session is active and can receive audio
        /// </summary>
        Active,
        
        /// <summary>
        /// Backend is processing the request
        /// </summary>
        Processing,
        
        /// <summary>
        /// Session completed normally
        /// </summary>
        Completed,
        
        /// <summary>
        /// Session was cancelled/interrupted
        /// </summary>
        Cancelled
    }
    
    /// <summary>
    /// Simplified conversation session state.
    /// SIMPLICITY FIRST: Just the essential session data, no complex lifecycle management.
    /// This replaces the complex AudioSession + SessionManager architecture.
    /// </summary>
    public class ConversationSession
    {
        /// <summary>
        /// Unique identifier for this session (used as RequestId)
        /// </summary>
        public string SessionId { get; }
        
        /// <summary>
        /// When the session started
        /// </summary>
        public DateTime StartTime { get; }
        
        /// <summary>
        /// Current state of the session
        /// </summary>
        public SessionState State { get; set; }

        // REMOVED: CanEnqueueAudio - audio queuing no longer used

        /// <summary>
        /// Helper property to check if session is active (for backwards compatibility)
        /// </summary>
        public bool IsActive => State == SessionState.Active;
        
        /// <summary>
        /// Is this NPC session currently listening to player audio?
        /// Set to true when player presses PTT, false when released.
        /// Used by SendLoop to know when to stop sending audio chunks.
        /// </summary>
        public bool IsListening { get; set; }
        
        /// <summary>
        /// Alias for IsListening for backwards compatibility.
        /// The name "IsRecording" was confusing as NPCs don't record - they listen.
        /// </summary>
        public bool IsRecording 
        { 
            get => IsListening; 
            set => IsListening = value; 
        }
        
        /// <summary>
        /// NPC name associated with this session
        /// </summary>
        public string NpcName { get; set; }
        
        /// <summary>
        /// Number of audio chunks sent
        /// </summary>
        public int ChunksSent { get; private set; }
        
        /// <summary>
        /// Number of audio streams received
        /// </summary>
        public int StreamsReceived { get; set; }
        
        /// <summary>
        /// Is an interruption currently active?
        /// </summary>
        public bool IsInterruptionActive { get; set; }
        
        // REMOVED: Audio queue - no longer used after refactoring
        // Audio is now sent directly via events instead of queuing
        
        /// <summary>
        /// Create a new conversation session
        /// </summary>
        public ConversationSession(string npcName = null)
        {
            SessionId = Guid.NewGuid().ToString();
            StartTime = DateTime.Now;
            State = SessionState.Active;  // Sessions start active
            IsListening = false;  // NPC starts not listening until PTT is pressed
            NpcName = npcName ?? "Unknown";
            ChunksSent = 0;
            StreamsReceived = 0;
            IsInterruptionActive = false;
        }
        
        // Track last warning time to prevent log spam
        //private readonly float _lastInactiveWarningTime = -1f;
        //private const float WARNING_COOLDOWN = 1.0f; // Only warn once per second
        
        // REMOVED: EnqueueAudio and TryDequeueAudio - audio queuing no longer used
        // Audio is now sent directly via events in the refactored architecture
        
        /// <summary>
        /// Increment the chunks sent counter
        /// </summary>
        public void IncrementChunksSent()
        {
            ChunksSent++;
        }
        
        /// <summary>
        /// Complete the session
        /// </summary>
        public void Complete()
        {
            State = SessionState.Completed;
            IsRecording = false;
            
            // REMOVED: Queue clearing - no longer needed
            //Debug.Log($"[Session {SessionId}] Completed - Chunks sent: {ChunksSent}, Streams received: {StreamsReceived}");
        }
        
        /// <summary>
        /// Cancel the session
        /// </summary>
        public void Cancel()
        {
            State = SessionState.Cancelled;
            IsRecording = false;
            
            // REMOVED: Queue clearing - no longer needed
            
            //Debug.Log($"[Session {SessionId}] Cancelled");
        }
    }
}