using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Tsc.AIBridge.Messages
{
    /// <summary>
    /// A persona's listener reactions (DA3): what the NPC may do while the player holds the talk button and pauses, such
    /// as a nod, a frown or a short "uhu". Carried on <see cref="SessionStartMessage.ListenerReactions"/>; the backend
    /// chooses one per pause (<see cref="ListenerReactionRequestMessage"/>), the client plays it. Null = the persona has
    /// none and the backend chooses nothing. Wire shape: ApiOrchestrator Models/ListenerReactionSet.cs.
    /// </summary>
    [Serializable]
    public class ListenerReactionSet
    {
        /// <summary>One sentence about the scene and the NPC's role, the context the choice reads.</summary>
        [JsonProperty("scene")]
        public string Scene { get; set; }

        /// <summary>The question to the model (DA3b). Null = the backend's neutral default.</summary>
        [JsonProperty("instruction", NullValueHandling = NullValueHandling.Ignore)]
        public string Instruction { get; set; }

        /// <summary>When the NPC does nothing, in plain words (DA3b). Null = the backend's neutral default.</summary>
        [JsonProperty("noneDescription", NullValueHandling = NullValueHandling.Ignore)]
        public string NoneDescription { get; set; }

        /// <summary>The model's probability must reach this or the NPC does nothing. Null = the backend default (0.7).</summary>
        [JsonProperty("minConfidence", NullValueHandling = NullValueHandling.Ignore)]
        public double? MinConfidence { get; set; }

        /// <summary>At least this many seconds between two reactions of one turn (DA3b; a safety net). Null = the backend default (1.5).</summary>
        [JsonProperty("minSecondsBetween", NullValueHandling = NullValueHandling.Ignore)]
        public double? MinSecondsBetween { get; set; }

        [JsonProperty("options")]
        public List<ListenerReactionOption> Options { get; set; } = new();
    }

    [Serializable]
    public class ListenerReactionOption
    {
        /// <summary>The id the client plays by (gesture plus optional clip); the backend answers with it.</summary>
        [JsonProperty("id")]
        public string Id { get; set; } = "";

        /// <summary>When this reaction fits, in plain words; the model chooses on this.</summary>
        [JsonProperty("description")]
        public string Description { get; set; } = "";

        /// <summary>The words of the clip ("uhu", "nee"), or null for a gesture only. The backend measures leaks with it.</summary>
        [JsonProperty("clipText")]
        public string ClipText { get; set; }

        /// <summary>This option's own threshold (DA3b; e.g. higher for "huh?" or a head shake). Null = the set's.</summary>
        [JsonProperty("minConfidence", NullValueHandling = NullValueHandling.Ignore)]
        public double? MinConfidence { get; set; }
    }
}
