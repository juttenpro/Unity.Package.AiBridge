using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Tsc.AIBridge.Messages;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: SessionStartMessage and ConversationContext must serialize
    /// BaseEmotion as lowerCamelCase "baseEmotion" so the ApiOrchestrator backend can
    /// read the grondtoon configured by scenario authors and forward it to Cartesia TTS.
    ///
    /// WHY: The backend maintains its own contract test (BaseEmotionUnityContractTests)
    /// that pins the JSON key to "baseEmotion". If this Unity-side mirror drifts (writes
    /// "BaseEmotion", omits non-null values, uses JsonUtility which ignores JsonProperty,
    /// etc.), the backend silently sees null and Cartesia voices stay flat even though
    /// the scenario author configured a grondtoon. No error surfaces.
    ///
    /// WHAT: Validates the JSON shape this package produces matches the backend contract.
    ///
    /// HOW: Serialize a SessionStartMessage and ConversationContext with BaseEmotion set,
    /// inspect the resulting JSON, then round-trip it back to confirm deserialization.
    ///
    /// SUCCESS CRITERIA:
    /// - JSON key is lowerCamelCase "baseEmotion" (not "BaseEmotion").
    /// - A non-null string value round-trips cleanly.
    /// - Missing key from the wire deserializes to null (backwards compat with older
    ///   backends that don't emit the field).
    ///
    /// BUSINESS IMPACT:
    /// - Silent drift breaks the emotion feature entirely — scenarios configured with a
    ///   grondtoon still produce flat Cartesia audio. This test is the wire-level gate.
    /// </summary>
    [TestFixture]
    public class BaseEmotionSerializationTests
    {
        [Test]
        public void SessionStartMessage_Serializes_BaseEmotion_As_lowerCamelCase()
        {
            var message = new SessionStartMessage
            {
                LanguageCode = "nl-NL",
                VoiceId = "Rebecca",
                BaseEmotion = "anxious"
            };

            var json = JsonConvert.SerializeObject(message);
            var parsed = JObject.Parse(json);

            Assert.That(parsed.ContainsKey("baseEmotion"), Is.True,
                "SessionStartMessage must serialize BaseEmotion as 'baseEmotion' — " +
                "PascalCase would be ignored by the backend.");
            Assert.That(parsed["baseEmotion"]!.Value<string>(), Is.EqualTo("anxious"));
        }

        [Test]
        public void SessionStartMessage_Roundtrips_BaseEmotion_From_Backend_Shape()
        {
            var originalJson =
                "{\"type\":\"sessionStart\",\"languageCode\":\"nl-NL\"," +
                "\"voiceId\":\"Rebecca\",\"baseEmotion\":\"anxious\"}";

            var deserialized = JsonConvert.DeserializeObject<SessionStartMessage>(originalJson);

            Assert.That(deserialized, Is.Not.Null);
            Assert.That(deserialized!.BaseEmotion, Is.EqualTo("anxious"));
        }

        [Test]
        public void SessionStartMessage_Tolerates_Missing_BaseEmotion_From_Older_Backends()
        {
            var legacyJson =
                "{\"type\":\"sessionStart\",\"languageCode\":\"nl-NL\",\"voiceId\":\"Rebecca\"}";

            var deserialized = JsonConvert.DeserializeObject<SessionStartMessage>(legacyJson);

            Assert.That(deserialized, Is.Not.Null);
            Assert.That(deserialized!.BaseEmotion, Is.Null,
                "Missing key must deserialize to null so older backends keep working.");
        }

        [Test]
        public void ConversationContext_Serializes_BaseEmotion_As_lowerCamelCase()
        {
            var context = new ConversationContext
            {
                voiceId = "Rebecca",
                llmProvider = "vertexai",
                llmModel = "gemini-2.5-flash",
                baseEmotion = "calm"
            };

            var json = JsonConvert.SerializeObject(context);
            var parsed = JObject.Parse(json);

            Assert.That(parsed.ContainsKey("baseEmotion"), Is.True);
            Assert.That(parsed["baseEmotion"]!.Value<string>(), Is.EqualTo("calm"));
        }

        [Test]
        public void ConversationContext_Tolerates_Missing_BaseEmotion()
        {
            var legacyJson =
                "{\"voiceId\":\"Rebecca\",\"llmProvider\":\"vertexai\",\"llmModel\":\"gemini-2.5-flash\"}";

            var deserialized = JsonConvert.DeserializeObject<ConversationContext>(legacyJson);

            Assert.That(deserialized, Is.Not.Null);
            Assert.That(deserialized!.baseEmotion, Is.Null);
        }
    }
}
