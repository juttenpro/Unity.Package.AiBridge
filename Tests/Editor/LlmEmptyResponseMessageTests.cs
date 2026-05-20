using Newtonsoft.Json;
using NUnit.Framework;
using Tsc.AIBridge.Messages;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: the aibridge package MUST be able to parse the
    /// LlmEmptyResponse signal that the backend started emitting in 2026-05-20.
    /// Without parsing the message, the Unity client cannot insert a placeholder
    /// turn into its ChatHistory, and the 2026-05-20 cascade (3 silent NPC turns)
    /// will repeat.
    ///
    /// WHY: this is the wire-format contract between backend and client. Any
    /// drift here breaks the entire empty-response feedback-loop fix.
    /// WHAT: round-trip + field-mapping tests for LlmEmptyResponseMessage,
    /// matching the JSON shape ApiOrchestrator's LlmEmptyResponseMessage emits.
    /// HOW: deserialize a JSON sample, assert each field maps; serialize, assert
    /// type field is "LlmEmptyResponse" (PascalCase per protocol).
    ///
    /// SUCCESS CRITERIA:
    /// - Type constant equals "LlmEmptyResponse" (matches backend constant).
    /// - All three payload fields (FinishReason, Reason, RequestId) round-trip.
    /// - Null FinishReason tolerated (some providers don't expose it).
    /// </summary>
    [TestFixture]
    public class LlmEmptyResponseMessageTests
    {
        [Test]
        public void Deserialize_BackendPayload_MapsAllFields()
        {
            // Wire format produced by ApiOrchestrator's LlmEmptyResponseMessage
            // (see Services/Telemetry/LlmEmptyResponseSignal.cs in the backend).
            var json = @"{
                ""type"": ""LlmEmptyResponse"",
                ""requestId"": ""req-eccc3c1b-4fba-429f-8cc5-ad1e9d767057"",
                ""timestamp"": 1715000000000,
                ""finishReason"": ""ContentFiltered"",
                ""reason"": ""LLM response blocked by provider content filter.""
            }";

            var msg = JsonConvert.DeserializeObject<LlmEmptyResponseMessage>(json);

            Assert.That(msg, Is.Not.Null);
            Assert.That(msg.Type, Is.EqualTo("LlmEmptyResponse"));
            Assert.That(msg.RequestId, Is.EqualTo("req-eccc3c1b-4fba-429f-8cc5-ad1e9d767057"));
            Assert.That(msg.Timestamp, Is.EqualTo(1715000000000L));
            Assert.That(msg.FinishReason, Is.EqualTo("ContentFiltered"));
            Assert.That(msg.Reason, Is.EqualTo("LLM response blocked by provider content filter."));
        }

        [Test]
        public void Deserialize_WithNullFinishReason_LeavesFieldNull()
        {
            // Provider didn't report a finishReason — message must still parse so
            // the client can insert a placeholder anyway.
            var json = @"{
                ""type"": ""LlmEmptyResponse"",
                ""requestId"": ""req-2"",
                ""finishReason"": null,
                ""reason"": ""LLM produced no content. Provider did not report a finish reason.""
            }";

            var msg = JsonConvert.DeserializeObject<LlmEmptyResponseMessage>(json);

            Assert.That(msg, Is.Not.Null);
            Assert.That(msg.FinishReason, Is.Null);
            Assert.That(msg.Reason, Is.Not.Null);
        }

        [Test]
        public void Serialize_ProducesWireShapeBackendExpects()
        {
            // Defensive — we don't currently send this client→server, but the
            // round-trip protects against accidental schema drift if someone adds
            // outbound use later.
            var msg = new LlmEmptyResponseMessage
            {
                Type = WebSocketMessageTypes.LlmEmptyResponse,
                RequestId = "req-1",
                Timestamp = 1715000000000L,
                FinishReason = "Safety",
                Reason = "Vertex AI flagged the response as unsafe.",
            };

            var json = JsonConvert.SerializeObject(msg);

            StringAssert.Contains("\"type\":\"LlmEmptyResponse\"", json);
            StringAssert.Contains("\"requestId\":\"req-1\"", json);
            StringAssert.Contains("\"finishReason\":\"Safety\"", json);
        }

        [Test]
        public void TypeConstant_MatchesBackendProtocol()
        {
            Assert.That(WebSocketMessageTypes.LlmEmptyResponse, Is.EqualTo("LlmEmptyResponse"),
                "Wire constant MUST match ApiOrchestrator.Models.WebSocket.WebSocketMessageTypes.LlmEmptyResponse — drift breaks the fix.");
        }
    }
}
