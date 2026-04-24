using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Tsc.AIBridge.Messages;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: SessionStartMessage, DirectTTSMessage, and ConversationContext
    /// must serialize the observability correlation IDs as lowerCamelCase keys exactly as the
    /// ApiOrchestrator backend expects, so per-lesson / per-organization aggregation in the
    /// observability dashboard works end-to-end.
    ///
    /// WHY: The backend's ObservabilityContext model uses JsonProperty("appLogId"),
    /// ("lessonId"), ("courseId"), ("organizationId"), ("appMode"). If this Unity-side mirror
    /// drifts (PascalCase keys, JsonUtility which ignores JsonProperty, missing the wrapper
    /// key "observability"), the backend silently sees null IDs and the dashboard cannot
    /// aggregate costs or health metrics per lesson / course / organization. No error
    /// surfaces — the feature just quietly stops working.
    ///
    /// WHAT: Validates the JSON shape this package produces matches the backend contract
    /// and round-trips through deserialization.
    ///
    /// HOW: Serialize messages with an ObservabilityContext populated, inspect the JSON,
    /// confirm the "observability" wrapper key exists with the expected inner keys.
    /// Also round-trip the backend's shape back into the Unity model.
    ///
    /// SUCCESS CRITERIA:
    /// - Wrapper JSON key is "observability" (lowerCamelCase).
    /// - Inner keys are "appLogId", "lessonId", "courseId", "organizationId", "appMode".
    /// - organizationId is serialized as a number (int), not string — matches backend int?.
    /// - Populated values round-trip cleanly (Unity → JSON → Unity).
    /// - Missing "observability" key from the wire deserializes Observability to null
    ///   (backwards compat with older backends that do not echo the field back).
    /// - UserId is NOT a field on ObservabilityContext — compile-time + reflection check.
    ///
    /// BUSINESS IMPACT:
    /// - Silent wire drift = observability dashboard shows "unknown" for all sessions,
    ///   breaking the entire per-lesson / per-organization reporting feature.
    /// - UserId leak = GDPR incident. See feedback_privacy_logging.md.
    /// </summary>
    [TestFixture]
    public class ObservabilityContextSerializationTests
    {
        [Test]
        public void SessionStartMessage_Serializes_Observability_Wrapper_With_lowerCamelCase_Keys()
        {
            var message = new SessionStartMessage
            {
                LanguageCode = "nl-NL",
                VoiceId = "Rebecca",
                Observability = new ObservabilityContext
                {
                    AppLogId = "log-abc-123",
                    LessonId = "lesson-42",
                    CourseId = "Occupationalhealth",
                    OrganizationId = 7,
                    AppMode = "Production"
                }
            };

            var json = JsonConvert.SerializeObject(message);
            var parsed = JObject.Parse(json);

            Assert.That(parsed.ContainsKey("observability"), Is.True,
                "SessionStartMessage must wrap observability IDs under the 'observability' key — " +
                "omitting the wrapper would leave the backend unable to find them.");

            var inner = (JObject)parsed["observability"]!;
            Assert.That(inner["appLogId"]!.Value<string>(), Is.EqualTo("log-abc-123"));
            Assert.That(inner["lessonId"]!.Value<string>(), Is.EqualTo("lesson-42"));
            Assert.That(inner["courseId"]!.Value<string>(), Is.EqualTo("Occupationalhealth"));
            Assert.That(inner["organizationId"]!.Value<int>(), Is.EqualTo(7),
                "organizationId must serialize as a number (int) to match backend int? type.");
            Assert.That(inner["appMode"]!.Value<string>(), Is.EqualTo("Production"));
        }

        [Test]
        public void SessionStartMessage_Tolerates_Missing_Observability_From_Older_Backends()
        {
            var legacyJson =
                "{\"type\":\"sessionStart\",\"languageCode\":\"nl-NL\",\"voiceId\":\"Rebecca\"}";

            var deserialized = JsonConvert.DeserializeObject<SessionStartMessage>(legacyJson);

            Assert.That(deserialized, Is.Not.Null);
            Assert.That(deserialized!.Observability, Is.Null,
                "Missing 'observability' key must deserialize to null so a SessionStartMessage " +
                "without client-supplied IDs keeps working against any backend version.");
        }

        [Test]
        public void DirectTTSMessage_Serializes_Observability_Under_Same_Wrapper_Key()
        {
            var message = new DirectTTSMessage
            {
                RequestId = "req-1",
                Text = "Hello",
                Observability = new ObservabilityContext { AppLogId = "log-1", OrganizationId = 5 }
            };

            var json = JsonConvert.SerializeObject(message);
            var parsed = JObject.Parse(json);

            Assert.That(parsed.ContainsKey("observability"), Is.True);
            Assert.That(parsed["observability"]!["appLogId"]!.Value<string>(), Is.EqualTo("log-1"));
            Assert.That(parsed["observability"]!["organizationId"]!.Value<int>(), Is.EqualTo(5));
        }

        [Test]
        public void ConversationContext_Serializes_Observability_For_TextInput_And_Analysis()
        {
            // TextInputMessage and AnalysisRequestMessage carry observability via their
            // Context field — one test covers both.
            var context = new ConversationContext
            {
                voiceId = "Rebecca",
                llmProvider = "vertexai",
                llmModel = "gemini-2.5-flash",
                observability = new ObservabilityContext
                {
                    AppLogId = "log-1",
                    CourseId = "AICoach",
                    LessonId = "",   // AI Coach standalone signals "no lesson" explicitly
                    OrganizationId = 3
                }
            };

            var json = JsonConvert.SerializeObject(context);
            var parsed = JObject.Parse(json);

            Assert.That(parsed.ContainsKey("observability"), Is.True);
            var inner = (JObject)parsed["observability"]!;
            Assert.That(inner["appLogId"]!.Value<string>(), Is.EqualTo("log-1"));
            Assert.That(inner["courseId"]!.Value<string>(), Is.EqualTo("AICoach"));
            Assert.That(inner["lessonId"]!.Value<string>(), Is.EqualTo(""),
                "Empty string is a meaningful signal from AI Coach — it must round-trip, not collapse to null.");
            Assert.That(inner["organizationId"]!.Value<int>(), Is.EqualTo(3));
        }

        [Test]
        public void ObservabilityContext_HasNoUserIdField_PrivacyGate()
        {
            // Compile-time AND reflection check: any future regression that adds a UserId
            // field or property to ObservabilityContext will break this test.
            var type = typeof(ObservabilityContext);
            var fields = type.GetFields();
            var properties = type.GetProperties();

            foreach (var field in fields)
            {
                Assert.That(field.Name.ToLowerInvariant(), Is.Not.EqualTo("userid"),
                    "ObservabilityContext must not expose a UserId field. " +
                    "UserId is a personal identifier (GDPR) and may never enter the observability pipeline. " +
                    "See memory/feedback_privacy_logging.md.");
            }

            foreach (var property in properties)
            {
                Assert.That(property.Name.ToLowerInvariant(), Is.Not.EqualTo("userid"),
                    "ObservabilityContext must not expose a UserId property.");
            }
        }

        [Test]
        public void ObservabilityContext_RoundTripsFromBackendShape()
        {
            // Exact wire payload the backend would produce/consume — pin it here so both
            // sides can reference this string as the contract.
            const string backendJson =
                "{\"appLogId\":\"log-1\"," +
                "\"lessonId\":\"lesson-1\"," +
                "\"courseId\":\"course-1\"," +
                "\"organizationId\":9," +
                "\"appMode\":\"Development\"}";

            var deserialized = JsonConvert.DeserializeObject<ObservabilityContext>(backendJson);

            Assert.That(deserialized, Is.Not.Null);
            Assert.That(deserialized!.AppLogId, Is.EqualTo("log-1"));
            Assert.That(deserialized.LessonId, Is.EqualTo("lesson-1"));
            Assert.That(deserialized.CourseId, Is.EqualTo("course-1"));
            Assert.That(deserialized.OrganizationId, Is.EqualTo(9));
            Assert.That(deserialized.AppMode, Is.EqualTo("Development"));
        }
    }
}
