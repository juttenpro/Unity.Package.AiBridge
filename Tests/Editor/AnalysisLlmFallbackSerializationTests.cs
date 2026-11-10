using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Tsc.AIBridge.Messages;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// WHY: an analysis request (Decisions on Azure EU, or any unary analysis) carries its LLM fallback in
    /// ConversationContext.llmFallback. The backend reads it with the same type as SessionStartMessage.llmFallback,
    /// so the keys must be identical: a renamed key means the analysis silently has no fallback. Without a
    /// fallback the key must be absent, so the backend keeps today's behaviour (no fallback).
    ///
    /// NOT COVERED: what the backend does with it (ApiOrchestrator tests), and the mapping from the AI API
    /// Template onto this object (Training Platform tests).
    /// </summary>
    [TestFixture]
    public class AnalysisLlmFallbackSerializationTests
    {
        private static LlmFallbackConfig FullFallback() => new LlmFallbackConfig
        {
            Provider = "vertexai",
            Model = "gemini-2.5-flash-lite",
            Temperature = 0.25f,
            TopP = 0.75f,
            FrequencyPenalty = 0.125f,
            PresencePenalty = 0.375f,
            MaxTokens = 640,
            ThinkingLevel = "low",
            Location = "europe-west4",
            ResponseFormat = "json_object",
            AzureResourceName = "ConversationReactions-WestEurope"
        };

        [Test]
        public void ContextWithoutFallback_SendsNoLlmFallbackKey()
        {
            var context = JObject.Parse(JsonConvert.SerializeObject(new ConversationContext { llmProvider = "azure-decisions" }));

            Assert.That(context.ContainsKey("llmFallback"), Is.False,
                "an analysis without a fallback must not send 'llmFallback'; the backend would treat null as a configured fallback");
        }

        [Test]
        public void ContextFallback_HasExactlyTheKeysAndValuesOfTheSessionStartFallback()
        {
            var context = JObject.Parse(JsonConvert.SerializeObject(new ConversationContext { llmFallback = FullFallback() }));
            var sessionStart = JObject.Parse(JsonConvert.SerializeObject(new SessionStartMessage { LlmFallback = FullFallback() }));

            var fromContext = context["llmFallback"] as JObject;
            var fromSessionStart = sessionStart["llmFallback"] as JObject;

            Assert.That(fromContext, Is.Not.Null, "the analysis fallback did not reach the wire under 'llmFallback'");
            Assert.That(fromSessionStart, Is.Not.Null, "fixture: SessionStart lost its fallback");
            Assert.That(JToken.DeepEquals(fromContext, fromSessionStart), Is.True,
                "the analysis fallback differs from the dialogue fallback; the backend parses both with one type.\n" +
                $"analysis: {fromContext}\ndialogue: {fromSessionStart}");
        }
    }
}
