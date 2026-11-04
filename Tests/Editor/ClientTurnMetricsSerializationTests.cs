using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Tsc.AIBridge.Messages;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: the client's telemetry about one listen reaches the ApiOrchestrator in exactly the shape it
    /// validates, and carries nothing but numbers, ids, the outcome and the device.
    ///
    /// WHY: Assistant Platform, 2026-11-04: on a colleague's phone the patient had to talk loudly and the VAD never fired.
    /// The backend (ClientTurnMetricsEvent) drops a frame with a missing field or an outcome in another case without
    /// answering, so wire drift here would only show as an empty Cloud Logging filter. A user id or text field in this
    /// message would be a data breach.
    /// WHAT: the JSON keys are exactly the backend's, the type and outcome are the wire strings, and a listen without an
    /// answer sends null rather than 0 for the answer time.
    /// NOT COVERED: the socket send (WebSocketClient.SendClientTurnMetricsAsync) and the backend side.
    /// </summary>
    [TestFixture]
    public class ClientTurnMetricsSerializationTests
    {
        static readonly string[] BackendKeys =
        {
            "type", "requestId", "timestamp", "outcome", "listenMs", "vadStarted", "speechMs", "bytesSent", "peakRms",
            "thresholdStart", "thresholdEnd", "noiseFloorStart", "noiseFloorEnd", "endOfSpeechToAnswerMs", "deviceModel",
            "os", "appVersion",
        };

        static JObject Serialize(ClientTurnMetricsMessage message) => JObject.Parse(JsonConvert.SerializeObject(message));

        [Test]
        public void Message_HasExactlyTheBackendsKeys_AndTheWireStrings()
        {
            var json = Serialize(new ClientTurnMetricsMessage
            {
                RequestId = "3f2b8c1e-5d4a-4e6f-9b7a-1c2d3e4f5a6b",
                Outcome = ClientTurnOutcome.NoSpeech,
                ListenMs = 9000,
                PeakRms = 0.0041,
                ThresholdStart = 0.006,
                ThresholdEnd = 0.006,
                DeviceModel = "OnePlus LE2113",
            });

            Assert.That(json.Properties().Select(p => p.Name), Is.EquivalentTo(BackendKeys),
                "The keys differ from what the backend validates: the frame is dropped, or carries a field it should not.");
            Assert.That(json["type"]!.Value<string>(), Is.EqualTo("clientTurnMetrics"));
            Assert.That(json["outcome"]!.Value<string>(), Is.EqualTo("NoSpeech"), "The backend reads the outcome by its case-sensitive name.");
            Assert.That(json["peakRms"]!.Value<double>(), Is.EqualTo(0.0041));
        }

        [Test]
        public void Listen_WithoutAnswer_SendsNull_NotZero()
        {
            var json = Serialize(new ClientTurnMetricsMessage { RequestId = "r1", Outcome = ClientTurnOutcome.Stopped });

            Assert.That(json["endOfSpeechToAnswerMs"]!.Type, Is.EqualTo(JTokenType.Null),
                "0 ms would read as an instant answer in Cloud Logging.");
            Assert.That(json["noiseFloorStart"]!.Type, Is.EqualTo(JTokenType.Null));
        }
    }
}
