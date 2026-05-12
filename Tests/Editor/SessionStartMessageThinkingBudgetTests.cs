using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Tsc.AIBridge.Messages;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: SessionStartMessage must forward the content creator's
    /// thinkingBudget choice (from the AI API Template) over the wire on the very first
    /// message of a real-time NPC dialogue session, so the ApiOrchestrator backend can
    /// apply it to Vertex AI's <c>generationConfig.thinkingConfig.thinkingBudget</c> for
    /// every LLM turn within that session.
    ///
    /// WHY: ConversationContext.thinkingBudget already exists for analysis + text-input
    /// flows, but real-time audio-driven dialogue (the dominant flow for VR training
    /// scenarios) goes through SessionStartMessage. Without the field on
    /// SessionStartMessage the backend session is initialised with the provider default
    /// (dynamic thinking) and every dialogue turn within that session pays the reasoning-
    /// token cost — even for personas configured with <c>llmThinkingBudget = 0</c> in
    /// the template. Sessions inherit thinkingBudget once at SessionStart, not per turn,
    /// so omitting the field here defeats the entire per-template control.
    ///
    /// WHAT: Validates the JSON shape this package produces for SessionStartMessage with
    /// thinkingBudget set to each of its documented semantic values (null/omitted, 0, -1,
    /// positive int) and confirms backend-shape JSON deserialises into the right field.
    ///
    /// HOW: Build a SessionStartMessage with thinkingBudget configured, serialise via
    /// Newtonsoft.Json (the production serialiser used by WebSocketClient), inspect the
    /// JSON keys/values, then round-trip a known backend-shape payload back into the
    /// Unity model.
    ///
    /// SUCCESS CRITERIA:
    /// - ThinkingBudget left null  → "thinkingBudget" key MUST be absent from JSON
    ///   (NullValueHandling.Ignore — backward compatibility with all existing scenarios
    ///   that do not opt into thinking control).
    /// - ThinkingBudget = 0        → JSON contains <c>"thinkingBudget":0</c>
    ///   (disables thinking on gemini-2.5-flash / -flash-lite; the Placebo + AcuteZorg
    ///   real-time dialogue case).
    /// - ThinkingBudget = -1       → JSON contains <c>"thinkingBudget":-1</c>
    ///   (model decides budget per query — dynamic).
    /// - ThinkingBudget = 2048     → JSON contains <c>"thinkingBudget":2048</c>
    ///   (explicit reservation — caller is responsible for ensuring maxTokens accommodates).
    /// - Backend-shape JSON containing "thinkingBudget":0 round-trips to ThinkingBudget == 0.
    /// - Legacy backend JSON without the key deserialises to ThinkingBudget == null.
    ///
    /// BUSINESS IMPACT:
    /// - Falen = per-template thinkingBudget keuze van content creators (e.g. Placebo
    ///   persona "Defensive", AcuteZorg "Aggressive") wordt genegeerd voor real-time
    ///   dialogue. Resultaat: ~700 thinking tokens per turn op gemini-2.5-flash met
    ///   maxTokens=500, response truncatie / vertraging, en de truncation bug die
    ///   v1.16.0 voor de analysis flow oploste keert terug voor live dialogue.
    /// - Wire drift (PascalCase, JsonUtility i.p.v. JsonConvert, vergeten
    ///   NullValueHandling) zou de backend silently null laten zien en zonder error
    ///   gewoon dynamic thinking gebruiken — exact het scenario dat de creator wilde
    ///   uitschakelen.
    /// </summary>
    [TestFixture]
    public class SessionStartMessageThinkingBudgetTests
    {
        [Test]
        public void SessionStartMessage_OmitsThinkingBudget_WhenNull()
        {
            var message = new SessionStartMessage
            {
                LanguageCode = "nl-NL",
                VoiceId = "Rebecca",
                // ThinkingBudget intentionally left unset (null)
            };

            var json = JsonConvert.SerializeObject(message);
            var parsed = JObject.Parse(json);

            Assert.That(parsed.ContainsKey("thinkingBudget"), Is.False,
                "When ThinkingBudget is null the field MUST be omitted from the wire payload " +
                "(NullValueHandling.Ignore) so legacy scenarios behave identically to before " +
                "the field existed — backend then applies its provider default.");
        }

        [Test]
        public void SessionStartMessage_SerializesThinkingBudgetZero_AsLowerCamelCase()
        {
            var message = new SessionStartMessage
            {
                LanguageCode = "nl-NL",
                VoiceId = "Rebecca",
                LlmProvider = "vertexai",
                LlmModel = "gemini-2.5-flash",
                ThinkingBudget = 0,
            };

            var json = JsonConvert.SerializeObject(message);
            var parsed = JObject.Parse(json);

            Assert.That(parsed.ContainsKey("thinkingBudget"), Is.True,
                "ThinkingBudget=0 is a meaningful signal (disable thinking on flash/-lite) — " +
                "it MUST be present on the wire, not collapsed to absent.");
            Assert.That(parsed["thinkingBudget"]!.Type, Is.EqualTo(JTokenType.Integer),
                "thinkingBudget must serialise as a JSON number, not a string.");
            Assert.That(parsed["thinkingBudget"]!.Value<int>(), Is.EqualTo(0));
        }

        [Test]
        public void SessionStartMessage_SerializesThinkingBudgetDynamic_AsLowerCamelCase()
        {
            var message = new SessionStartMessage
            {
                LanguageCode = "nl-NL",
                VoiceId = "Rebecca",
                LlmProvider = "vertexai",
                LlmModel = "gemini-2.5-pro",
                ThinkingBudget = -1,
            };

            var json = JsonConvert.SerializeObject(message);
            var parsed = JObject.Parse(json);

            Assert.That(parsed.ContainsKey("thinkingBudget"), Is.True);
            Assert.That(parsed["thinkingBudget"]!.Value<int>(), Is.EqualTo(-1),
                "-1 = dynamic thinking (model decides per query). Required for gemini-2.5-pro " +
                "which rejects 0.");
        }

        [Test]
        public void SessionStartMessage_SerializesExplicitThinkingBudget_AsLowerCamelCase()
        {
            var message = new SessionStartMessage
            {
                LanguageCode = "nl-NL",
                VoiceId = "Rebecca",
                LlmProvider = "vertexai",
                LlmModel = "gemini-2.5-flash",
                MaxTokens = 4096,
                ThinkingBudget = 2048,
            };

            var json = JsonConvert.SerializeObject(message);
            var parsed = JObject.Parse(json);

            Assert.That(parsed.ContainsKey("thinkingBudget"), Is.True);
            Assert.That(parsed["thinkingBudget"]!.Value<int>(), Is.EqualTo(2048),
                "Explicit token reservation must round-trip verbatim — backend reserves exactly " +
                "this number of thinking tokens out of maxTokens.");
        }

        [Test]
        public void SessionStartMessage_RoundTripsThinkingBudgetFromBackendShape()
        {
            // Exact wire payload the backend would consume — pin it so both sides have
            // a reference for the contract.
            const string backendJson =
                "{\"type\":\"sessionStart\"," +
                "\"languageCode\":\"nl-NL\"," +
                "\"voiceId\":\"Rebecca\"," +
                "\"llmProvider\":\"vertexai\"," +
                "\"llmModel\":\"gemini-2.5-flash\"," +
                "\"thinkingBudget\":0}";

            var deserialized = JsonConvert.DeserializeObject<SessionStartMessage>(backendJson);

            Assert.That(deserialized, Is.Not.Null);
            Assert.That(deserialized!.ThinkingBudget, Is.EqualTo(0),
                "Backend-shape JSON with thinkingBudget=0 must populate ThinkingBudget — the " +
                "contract is symmetric for replay / debugging tooling.");
        }

        [Test]
        public void SessionStartMessage_ToleratesMissingThinkingBudget_FromOlderBackends()
        {
            var legacyJson =
                "{\"type\":\"sessionStart\",\"languageCode\":\"nl-NL\",\"voiceId\":\"Rebecca\"}";

            var deserialized = JsonConvert.DeserializeObject<SessionStartMessage>(legacyJson);

            Assert.That(deserialized, Is.Not.Null);
            Assert.That(deserialized!.ThinkingBudget, Is.Null,
                "Missing 'thinkingBudget' key must deserialise to null so a SessionStartMessage " +
                "without the field keeps working against any backend version.");
        }
    }
}
