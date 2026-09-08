namespace Tsc.AIBridge.Core
{
    /// <summary>
    /// What happens to an NPC's unfinished answer when the player turns to a different NPC.
    ///
    /// This is content policy, not a client decision. Three behaviours are all defensible in a room
    /// with several NPCs: the abandoned NPC falls silent, it answers anyway, or it holds its answer
    /// until the player looks at it again. Which one is right depends on the scenario, so the RuleSystem
    /// configures it per turn.
    ///
    /// Note what is NOT configurable: the previous RECORDING is always closed. Upstream microphone audio
    /// carries no request id, so the backend attributes incoming speech to the most recently opened
    /// session — exactly one microphone turn may be live, whatever the policy says about answers.
    /// See Docs/Architecture/Concurrent-Turns-Plan.md, decisions 2 and 2a.
    /// </summary>
    public enum PlayerTurnsAwayPolicy
    {
        /// <summary>
        /// Let the abandoned NPC finish. The player hears the answer even after turning away — the
        /// natural interaction when asking one team member something and immediately turning to another.
        ///
        /// **This is deliberately the enum's zero**, so anything that does not set the policy at all
        /// gets it. Cancelling was the default when the setting was introduced, on the reasoning that it
        /// preserved existing behaviour — but that behaviour was never a decision, only what the code
        /// happened to do, and an NPC falling silent because the player glanced away is wrong on its
        /// own terms. Changed while nothing had it serialized yet: the field appeared in no asset,
        /// prefab or scene, so no content carried a value to migrate. That window does not come back.
        /// </summary>
        LetAnswerFinish = 0,

        /// <summary>
        /// Stop the abandoned NPC's answer. Content asks for this when turning away should read as
        /// cutting someone off.
        /// </summary>
        CancelAnswer = 1,
    }
}
