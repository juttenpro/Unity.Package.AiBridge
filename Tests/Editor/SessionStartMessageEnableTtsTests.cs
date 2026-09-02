using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Tsc.AIBridge.Messages;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: SessionStartMessage must be able to declare a turn that
    /// transcribes and reasons about the player's speech WITHOUT speaking a reply —
    /// <c>enableTts: false</c>, usually together with <c>responseFormat: "json_object"</c>.
    /// That is how a PromptComposer graph scores what the player just said (PlayerAudio
    /// entry + JsonAnalysis exit) in ONE turn.
    ///
    /// WHY: the audio path always ran STT → LLM → TTS. A scoring turn therefore paid for an
    /// ElevenLabs call and its first-audio latency for speech nobody plays, and could not ask
    /// the model for clean JSON at all (responseFormat existed only on the analysis/text-input
    /// flows). The backend gained both fields on 2026-09-02; without them on this message the
    /// client cannot reach that behaviour.
    ///
    /// WHAT: pins the wire shape of both fields, including the backward-compatibility default —
    /// every deployed scenario omits enableTts and MUST keep its spoken NPC answer.
    ///
    /// SUCCESS CRITERIA:
    /// - A fresh message reports EnableTts == true (nothing goes silent by accident).
    /// - EnableTts = false serialises as <c>"enableTts":false</c> (lower camelCase, a JSON bool).
    /// - ResponseFormat left null → the key is ABSENT from the payload.
    /// - ResponseFormat = "json_object" round-trips verbatim.
    /// - Backend-shape JSON populates both fields; legacy JSON without them keeps TTS on.
    ///
    /// BUSINESS IMPACT: wire drift here is silent — the backend would simply keep synthesising
    /// speech, so the turn still "works" while costing a TTS call per score and returning prose
    /// where the graph expects JSON.
    /// </summary>
    [TestFixture]
    public class SessionStartMessageEnableTtsTests
    {
        [Test]
        public void SessionStartMessage_DefaultsEnableTts_ToTrue()
        {
            var message = new SessionStartMessage
            {
                LanguageCode = "nl-NL",
                VoiceId = "Rebecca",
            };

            Assert.That(message.EnableTts, Is.True,
                "Every existing scenario builds a SessionStartMessage without touching this field " +
                "and must keep its spoken NPC answer.");
        }

        [Test]
        public void SessionStartMessage_SerializesEnableTtsFalse_AsLowerCamelCase()
        {
            var message = new SessionStartMessage
            {
                LanguageCode = "nl-NL",
                LlmProvider = "vertexai",
                LlmModel = "gemini-2.5-flash",
                EnableTts = false,
            };

            var json = JsonConvert.SerializeObject(message);
            var parsed = JObject.Parse(json);

            Assert.That(parsed.ContainsKey("enableTts"), Is.True,
                "enableTts=false is the whole signal — it MUST reach the backend.");
            Assert.That(parsed["enableTts"]!.Type, Is.EqualTo(JTokenType.Boolean),
                "enableTts must serialise as a JSON bool, not a string.");
            Assert.That(parsed["enableTts"]!.Value<bool>(), Is.False);
        }

        [Test]
        public void SessionStartMessage_OmitsResponseFormat_WhenNull()
        {
            var message = new SessionStartMessage
            {
                LanguageCode = "nl-NL",
                VoiceId = "Rebecca",
            };

            var json = JsonConvert.SerializeObject(message);
            var parsed = JObject.Parse(json);

            Assert.That(parsed.ContainsKey("responseFormat"), Is.False,
                "An absent responseFormat means free text — the key must not appear at all, so " +
                "dialogue turns behave exactly as before the field existed.");
        }

        [Test]
        public void SessionStartMessage_SerializesJsonObjectResponseFormat()
        {
            var message = new SessionStartMessage
            {
                LanguageCode = "nl-NL",
                LlmProvider = "vertexai",
                LlmModel = "gemini-2.5-flash",
                EnableTts = false,
                ResponseFormat = "json_object",
            };

            var json = JsonConvert.SerializeObject(message);
            var parsed = JObject.Parse(json);

            Assert.That(parsed["responseFormat"]!.Value<string>(), Is.EqualTo("json_object"),
                "The value is passed to the provider verbatim; any rewriting here would be " +
                "invisible to the graph author.");
        }

        [Test]
        public void SessionStartMessage_RoundTripsTtsLessTurnFromBackendShape()
        {
            // The exact payload a scoring turn puts on the wire — no voiceId at all, because
            // nothing is spoken. Pinned so both sides share one reference.
            const string backendJson =
                "{\"type\":\"sessionStart\"," +
                "\"languageCode\":\"nl-NL\"," +
                "\"llmProvider\":\"vertexai\"," +
                "\"llmModel\":\"gemini-2.5-flash\"," +
                "\"enableTts\":false," +
                "\"responseFormat\":\"json_object\"}";

            var deserialized = JsonConvert.DeserializeObject<SessionStartMessage>(backendJson);

            Assert.That(deserialized, Is.Not.Null);
            Assert.That(deserialized!.EnableTts, Is.False);
            Assert.That(deserialized.ResponseFormat, Is.EqualTo("json_object"));
            Assert.That(deserialized.VoiceId, Is.Null,
                "A TTS-less turn names no voice — the backend stopped requiring one.");
        }

        [Test]
        public void SessionStartMessage_ToleratesMissingFields_FromOlderScenarios()
        {
            var legacyJson =
                "{\"type\":\"sessionStart\",\"languageCode\":\"nl-NL\",\"voiceId\":\"Rebecca\"}";

            var deserialized = JsonConvert.DeserializeObject<SessionStartMessage>(legacyJson);

            Assert.That(deserialized, Is.Not.Null);
            Assert.That(deserialized!.EnableTts, Is.True,
                "A payload without enableTts must keep speech ON — the opposite default would " +
                "mute every deployed scenario.");
            Assert.That(deserialized.ResponseFormat, Is.Null);
        }
    }
}
