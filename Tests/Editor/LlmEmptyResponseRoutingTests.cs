using System;
using System.Collections.Generic;
using NUnit.Framework;
using Tsc.AIBridge.Core;
using Tsc.AIBridge.Messages;
using Tsc.AIBridge.WebSocket;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: <see cref="ConversationMetadataHandler"/> MUST route
    /// inbound <c>LlmEmptyResponse</c> WebSocket messages to its public
    /// <see cref="ConversationMetadataHandler.OnLlmEmptyResponse"/> event. This
    /// is the only path through which Training-Platform-side <c>NpcClient</c>
    /// learns the LLM produced no content, and therefore the only path through
    /// which a ChatHistory placeholder can be inserted to break the 2026-05-20
    /// empty-response feedback loop.
    ///
    /// WHY: a silent regression here (typo in case label, deserialization bug,
    /// missing event invocation) re-introduces the exact production incident.
    /// WHAT: end-to-end routing test that feeds a JSON envelope through
    /// <c>ProcessMessage</c> and asserts the typed message arrives at the event
    /// subscriber with all fields intact.
    /// HOW: a stub subscriber records every dispatch; assert exactly-once
    /// invocation with the expected payload across multiple finish-reason
    /// variants and the null-finish-reason case.
    ///
    /// SUCCESS CRITERIA:
    /// - Event fired exactly once per inbound message.
    /// - <c>FinishReason</c>, <c>Reason</c>, <c>RequestId</c> preserved.
    /// - Routing also succeeds when finishReason field is absent / null (some
    ///   providers don't expose one).
    /// - Other message types do NOT spuriously fire the event.
    ///
    /// BUSINESS IMPACT:
    /// - Missing dispatch ⇒ client never inserts placeholder ⇒ next turn
    ///   stacks user messages ⇒ filter re-triggers ⇒ silent NPC cascade.
    /// </summary>
    [TestFixture]
    public class LlmEmptyResponseRoutingTests
    {
        private ConversationMetadataHandler _handler;
        private List<LlmEmptyResponseMessage> _received;

        [SetUp]
        public void SetUp()
        {
            _handler = new ConversationMetadataHandler(
                personaName: "TestNpc",
                latencyTracker: new LatencyTracker("TestNpc"),
                enableVerboseLogging: false);
            _received = new List<LlmEmptyResponseMessage>();
            _handler.OnLlmEmptyResponse += msg => _received.Add(msg);
        }

        [Test]
        public void ProcessMessage_LlmEmptyResponse_RoutesToOnLlmEmptyResponse()
        {
            var json = @"{
                ""type"": ""LlmEmptyResponse"",
                ""requestId"": ""req-1"",
                ""finishReason"": ""ContentFiltered"",
                ""reason"": ""LLM response blocked by provider content filter.""
            }";

            _handler.ProcessMessage(json);

            Assert.That(_received, Has.Count.EqualTo(1));
            Assert.That(_received[0].RequestId, Is.EqualTo("req-1"));
            Assert.That(_received[0].FinishReason, Is.EqualTo("ContentFiltered"));
            Assert.That(_received[0].Reason, Is.EqualTo("LLM response blocked by provider content filter."));
        }

        [Test]
        public void ProcessMessage_LlmEmptyResponse_WithNullFinishReason_StillRoutes()
        {
            var json = @"{
                ""type"": ""LlmEmptyResponse"",
                ""requestId"": ""req-2"",
                ""finishReason"": null,
                ""reason"": ""LLM produced no content. Provider did not report a finish reason.""
            }";

            _handler.ProcessMessage(json);

            Assert.That(_received, Has.Count.EqualTo(1));
            Assert.That(_received[0].FinishReason, Is.Null);
            Assert.That(_received[0].Reason, Is.Not.Null);
        }

        [Test]
        public void ProcessMessage_LlmEmptyResponse_WithSafetyFinishReason_ForwardsVerbatim()
        {
            // Vertex AI uses Safety / Recitation / ProhibitedContent. Client
            // branches on this to decide UI behaviour — strings MUST pass through
            // unchanged.
            var json = @"{
                ""type"": ""LlmEmptyResponse"",
                ""requestId"": ""req-vertex"",
                ""finishReason"": ""Safety"",
                ""reason"": ""Vertex AI flagged the response as unsafe.""
            }";

            _handler.ProcessMessage(json);

            Assert.That(_received, Has.Count.EqualTo(1));
            Assert.That(_received[0].FinishReason, Is.EqualTo("Safety"));
        }

        [Test]
        public void ProcessMessage_NoTranscript_DoesNotFireLlmEmptyResponseEvent()
        {
            // Cross-talk guard: an unrelated NoTranscript should NOT route to the
            // new event.
            var json = @"{
                ""type"": ""NoTranscript"",
                ""requestId"": ""req-3"",
                ""reason"": ""No speech detected"",
                ""audioDuration"": 1100,
                ""sttProvider"": ""azure""
            }";

            _handler.ProcessMessage(json);

            Assert.That(_received, Is.Empty,
                "NoTranscript must not bleed into the LlmEmptyResponse event channel.");
        }

        [Test]
        public void ProcessMessage_MultipleLlmEmptyResponse_FiresOncePerMessage()
        {
            // Simulate two empty turns back-to-back. Each must produce exactly one
            // event so the client can insert one placeholder per turn.
            var json1 = @"{ ""type"": ""LlmEmptyResponse"", ""requestId"": ""req-a"", ""finishReason"": ""ContentFiltered"", ""reason"": ""block 1"" }";
            var json2 = @"{ ""type"": ""LlmEmptyResponse"", ""requestId"": ""req-b"", ""finishReason"": ""ContentFiltered"", ""reason"": ""block 2"" }";

            _handler.ProcessMessage(json1);
            _handler.ProcessMessage(json2);

            Assert.That(_received, Has.Count.EqualTo(2));
            Assert.That(_received[0].RequestId, Is.EqualTo("req-a"));
            Assert.That(_received[1].RequestId, Is.EqualTo("req-b"));
        }
    }
}
