using System.Collections.Generic;
using Newtonsoft.Json;

namespace Tsc.AIBridge.Messages
{
    /// <summary>
    /// The scores one integer variable may get, for the Decisions API (analysis only). Carried per variable name in
    /// <see cref="ConversationContext.decisionsScales"/>; the name is exactly the variable's key in the prompt's JSON
    /// FORMAT block. The client reads the scale from the prompt texts it knows (TASK, header, instructions); the backend
    /// checks it and turns it into Decisions levels.
    /// </summary>
    public class DecisionsScale
    {
        /// <summary>The allowed answers: ascending, unique whole numbers (2-10 of them).</summary>
        [JsonProperty("levels")]
        public List<int> Levels { get; set; }

        /// <summary>True when 0 means "not assessable" (then Levels starts with 0).</summary>
        [JsonProperty("zeroNotAssessable")]
        public bool ZeroNotAssessable { get; set; }
    }
}
