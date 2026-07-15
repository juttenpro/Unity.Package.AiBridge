using System.Collections.Generic;
using Newtonsoft.Json;

namespace Tsc.AIBridge.Messages
{
    /// <summary>
    /// Client-owned vocal-delivery baseline (measurement layer v2). OPAQUE round-trip state: Unity holds
    /// it per case (on <c>SpeechInputHandler</c>), sends it on each SessionStart, and stores the server's
    /// updated copy from the <c>prosodyresult</c> message. The client only ever SETS <see cref="Frozen"/>
    /// (at the warmup→escalation transition, via the "Freeze Vocal Baseline" rule node); the Welford
    /// internals are server-computed and never touched here. The shape matches the ApiOrchestrator
    /// ProsodyBaseline DTO so it deserialises/serialises verbatim.
    /// </summary>
    public class ProsodyBaselineState
    {
        [JsonProperty("version")] public int Version { get; set; } = 2;
        [JsonProperty("frozen")] public bool Frozen { get; set; }
        [JsonProperty("features")] public Dictionary<string, WelfordState> Features { get; set; } = new Dictionary<string, WelfordState>();
    }

    /// <summary>One feature's running mean/variance (Welford). Server-computed; carried verbatim.</summary>
    public class WelfordState
    {
        [JsonProperty("count")] public int Count { get; set; }
        [JsonProperty("mean")] public double Mean { get; set; }
        [JsonProperty("m2")] public double M2 { get; set; }
    }
}
