using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Tsc.AIBridge.Messages;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: SessionStartMessage (and ConversationContext) must forward the content
    /// creator's Gemini 3.x <c>thinkingLevel</c> choice (from the AI API Template) over the wire, so
    /// the ApiOrchestrator backend can apply it to Vertex AI's
    /// <c>generationConfig.thinkingConfig.thinkingLevel</c>.
    ///
    /// WHY: Gemini 3.x models (e.g. gemini-3.1-flash-lite, becoming the default analysis model) do
    /// NOT use thinkingBudget — they use thinkingLevel (minimal|low|medium|high) and thinking cannot
    /// be fully disabled. The budget-0 "disable thinking" path silently NO-OPs on 3.x, so without a
    /// thinkingLevel wire field the per-template reasoning-depth choice is lost. This mirrors the
    /// thinkingBudget wire coverage (SessionStartMessageThinkingBudgetTests) for the new field.
    ///
    /// WHAT: Validates the JSON shape this package produces for thinkingLevel — omitted when null
    /// (NullValueHandling.Ignore), serialised as a lowerCamelCase string when set, and round-tripping
    /// from a backend-shape payload. Parsing/validation of the level value happens on the backend
    /// (VertexThinkingConfig), not here — this layer only carries the string verbatim.
    ///
    /// SUCCESS CRITERIA:
    /// - ThinkingLevel null    → "thinkingLevel" key MUST be absent (backward compatibility).
    /// - ThinkingLevel "minimal"/"low"/"medium"/"high" → JSON string value present, verbatim.
    /// - Backend-shape JSON with "thinkingLevel":"minimal" round-trips to ThinkingLevel == "minimal".
    /// - ConversationContext.thinkingLevel serialises with the same wire contract.
    ///
    /// BUSINESS IMPACT:
    /// - Falen = gemini-3.1-flash-lite ignores per-template reasoning-depth choices; analysis JSON
    ///   can be truncated by uncontrolled thinking, or latency suffers from over-thinking.
    /// </summary>
    [TestFixture]
    public class SessionStartMessageThinkingLevelTests
    {
        [Test]
        public void SessionStartMessage_OmitsThinkingLevel_WhenNull()
        {
            var message = new SessionStartMessage
            {
                LanguageCode = "nl-NL",
                VoiceId = "Rebecca",
                // ThinkingLevel intentionally left unset (null)
            };

            var json = JsonConvert.SerializeObject(message);
            var parsed = JObject.Parse(json);

            Assert.That(parsed.ContainsKey("thinkingLevel"), Is.False,
                "When ThinkingLevel is null the field MUST be omitted from the wire payload " +
                "(NullValueHandling.Ignore) so non-Gemini-3.x scenarios behave identically.");
        }

        [TestCase("minimal")]
        [TestCase("low")]
        [TestCase("medium")]
        [TestCase("high")]
        public void SessionStartMessage_SerializesThinkingLevel_AsLowerCamelCaseString(string level)
        {
            var message = new SessionStartMessage
            {
                LanguageCode = "nl-NL",
                VoiceId = "Rebecca",
                LlmProvider = "vertexai",
                LlmModel = "gemini-3.1-flash-lite",
                ThinkingLevel = level,
            };

            var json = JsonConvert.SerializeObject(message);
            var parsed = JObject.Parse(json);

            Assert.That(parsed.ContainsKey("thinkingLevel"), Is.True,
                "A set thinkingLevel MUST be present on the wire.");
            Assert.That(parsed["thinkingLevel"]!.Type, Is.EqualTo(JTokenType.String),
                "thinkingLevel must serialise as a JSON string, not a number.");
            Assert.That(parsed["thinkingLevel"]!.Value<string>(), Is.EqualTo(level),
                "The level string must round-trip verbatim — the backend parses it.");
        }

        [Test]
        public void SessionStartMessage_RoundTripsThinkingLevelFromBackendShape()
        {
            const string backendJson =
                "{\"type\":\"sessionStart\"," +
                "\"languageCode\":\"nl-NL\"," +
                "\"voiceId\":\"Rebecca\"," +
                "\"llmProvider\":\"vertexai\"," +
                "\"llmModel\":\"gemini-3.1-flash-lite\"," +
                "\"thinkingLevel\":\"minimal\"}";

            var deserialized = JsonConvert.DeserializeObject<SessionStartMessage>(backendJson);

            Assert.That(deserialized, Is.Not.Null);
            Assert.That(deserialized!.ThinkingLevel, Is.EqualTo("minimal"),
                "Backend-shape JSON with thinkingLevel must populate ThinkingLevel — symmetric contract.");
        }

        [Test]
        public void SessionStartMessage_ToleratesMissingThinkingLevel_FromOlderBackends()
        {
            var legacyJson =
                "{\"type\":\"sessionStart\",\"languageCode\":\"nl-NL\",\"voiceId\":\"Rebecca\"}";

            var deserialized = JsonConvert.DeserializeObject<SessionStartMessage>(legacyJson);

            Assert.That(deserialized, Is.Not.Null);
            Assert.That(deserialized!.ThinkingLevel, Is.Null,
                "Missing 'thinkingLevel' key must deserialise to null so the message keeps working " +
                "against any backend version.");
        }

        [Test]
        public void ConversationContext_OmitsThinkingLevel_WhenNull_AndSerializesWhenSet()
        {
            var contextNull = new ConversationContext { llmProvider = "vertexai", llmModel = "gemini-3.1-flash-lite" };
            var jsonNull = JObject.Parse(JsonConvert.SerializeObject(contextNull));
            Assert.That(jsonNull.ContainsKey("thinkingLevel"), Is.False,
                "ConversationContext must omit thinkingLevel when unset (NullValueHandling.Ignore).");

            var contextSet = new ConversationContext
            {
                llmProvider = "vertexai",
                llmModel = "gemini-3.1-flash-lite",
                thinkingLevel = "low",
            };
            var jsonSet = JObject.Parse(JsonConvert.SerializeObject(contextSet));
            Assert.That(jsonSet["thinkingLevel"]!.Value<string>(), Is.EqualTo("low"),
                "ConversationContext must serialise thinkingLevel as the verbatim string for the analysis/text flows.");
        }
    }
}
