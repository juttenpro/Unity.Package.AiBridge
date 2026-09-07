namespace Tsc.AIBridge.Audio.Interruption
{
    /// <summary>
    /// The interruption settings of the NPC an interruption decision is about.
    ///
    /// Value type on purpose: it lets the resolution between "the NPC the player is addressing", "the NPC
    /// a request was most recently started for" and the built-in fallback be a pure function, testable
    /// without faking the whole <see cref="Core.INpcConfiguration"/> surface.
    /// </summary>
    public readonly struct InterruptionTarget
    {
        /// <summary>An absent target — no NPC known at this level of the resolution.</summary>
        public static readonly InterruptionTarget None = default;

        public InterruptionTarget(bool allowInterruption, float persistenceTime)
        {
            IsPresent = true;
            AllowInterruption = allowInterruption;
            PersistenceTime = persistenceTime;
        }

        /// <summary>False for <see cref="None"/>: this level of the resolution had no NPC.</summary>
        public bool IsPresent { get; }

        /// <summary>Whether this NPC may be interrupted at all (PersonaSO.allowInterruption).</summary>
        public bool AllowInterruption { get; }

        /// <summary>How long the player must keep talking over this NPC (PersonaSO.persistenceTime).</summary>
        public float PersistenceTime { get; }
    }
}
