using System;

namespace Tsc.AIBridge.Core
{
    /// <summary>
    /// One conversation turn's client-side bookkeeping.
    ///
    /// Everything here is read by production code. The type used to carry a lifecycle it never had:
    /// a SessionState enum with Active/Processing/Completed/Cancelled plus IsActive, Complete() and
    /// Cancel() that only wrote that state; IsListening with an IsRecording alias; a ChunksSent
    /// counter; an IsInterruptionActive flag whose only reader had no callers of its own; and a
    /// StartTime stamped at construction. None of it was ever read, and it read as session plumbing
    /// to migrate when the turn bookkeeping moves per-request.
    /// See Docs/Architecture/Concurrent-Turns-Plan.md, step 1.
    /// </summary>
    public class ConversationSession
    {
        /// <summary>
        /// Unique identifier for this request (matches the WebSocket requestId field). This is the id
        /// the backend echoes on every message and every audio frame of the turn.
        /// </summary>
        public string RequestId { get; }

        /// <summary>
        /// NPC name associated with this session. A display name, so not safe as an identity key —
        /// asset names are not unique in this project. Use <see cref="NpcId"/> to compare NPCs.
        /// </summary>
        public string NpcName { get; set; }

        /// <summary>
        /// The provider key of the NPC this turn belongs to — what INpcConfiguration.Id and
        /// ConversationRequest.NpcId use. This is the identity; NpcName is for humans.
        /// </summary>
        public string NpcId { get; }

        /// <summary>
        /// What to do with this turn's unfinished answer if the player turns to another NPC. Carried on
        /// the session because the decision is made when the turn is ABANDONED, by which time its
        /// request record is long gone.
        /// </summary>
        public PlayerTurnsAwayPolicy OnPlayerTurnsAway { get; }

        /// <summary>
        /// Number of audio streams received. Sole input to the "was there audio?" branch that decides
        /// whether conversationComplete has to clean the session up itself.
        /// </summary>
        public int StreamsReceived { get; set; }

        /// <summary>
        /// Whether the backend has shown ANY sign of life for this turn — a transcript, or the first
        /// audio chunk. Once true, this turn's watchdog is over for good, so it can never cut off a
        /// long answer mid-stream.
        ///
        /// This was one field on the orchestrator holding "the id a signal was last seen for". With one
        /// turn at a time that was the same thing; with several it is not. A signal for turn B silenced
        /// turn A's watchdog, because A only ever compared its own id against that single slot.
        /// </summary>
        public bool FirstSignalSeen { get; set; }

        /// <param name="npcName">Name of the NPC</param>
        /// <param name="requestId">Optional request ID. If null, a new GUID will be generated</param>
        public ConversationSession(string npcName = null, string requestId = null, string npcId = null,
            PlayerTurnsAwayPolicy onPlayerTurnsAway = PlayerTurnsAwayPolicy.LetAnswerFinish)
        {
            RequestId = requestId ?? Guid.NewGuid().ToString();
            NpcName = npcName ?? "Unknown";
            NpcId = npcId;
            OnPlayerTurnsAway = onPlayerTurnsAway;
            StreamsReceived = 0;
        }
    }
}
