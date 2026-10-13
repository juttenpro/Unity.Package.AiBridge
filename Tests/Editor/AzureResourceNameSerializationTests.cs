using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Tsc.AIBridge.Messages;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// WHY: the AI API Template names the Azure OpenAI resource of the main LLM, but only the
    /// fallback sent it, so every main call ran on the backend's default resource whatever the
    /// creator filled in. SessionStartMessage (audio turns) and ConversationContext (NPC-initiated
    /// turns, analysis) must carry it as "azureResourceName", the key the ApiOrchestrator reads.
    /// Null is omitted, like the fallback's resource name, so a turn without one sends no key.
    ///
    /// NOT COVERED: the mapping from ConversationRequest onto these messages inside
    /// RequestOrchestrator (built inline in the send coroutine), which needs a real session.
    /// </summary>
    [TestFixture]
    public class AzureResourceNameSerializationTests
    {
        private const string WestEurope = "ConversationReactions-WestEurope";

        [Test]
        public void SessionStartMessage_Serializes_AzureResourceName_With_Backend_Key()
        {
            var message = new SessionStartMessage { LlmProvider = "azure-openai", AzureResourceName = WestEurope };

            var parsed = JObject.Parse(JsonConvert.SerializeObject(message));

            Assert.That(parsed["azureResourceName"]?.Value<string>(), Is.EqualTo(WestEurope),
                "the backend reads 'azureResourceName'; any other key runs the turn on the default Azure resource");
        }

        [Test]
        public void ConversationContext_Serializes_AzureResourceName_With_Backend_Key()
        {
            var context = new ConversationContext { llmProvider = "azure-openai", azureResourceName = WestEurope };

            var parsed = JObject.Parse(JsonConvert.SerializeObject(context));

            Assert.That(parsed["azureResourceName"]?.Value<string>(), Is.EqualTo(WestEurope),
                "NPC-initiated turns and analysis travel in ConversationContext and must carry the same resource");
        }

        [Test]
        public void Null_AzureResourceName_Is_Omitted_From_Both_Messages()
        {
            var sessionStart = JObject.Parse(JsonConvert.SerializeObject(new SessionStartMessage()));
            var context = JObject.Parse(JsonConvert.SerializeObject(new ConversationContext()));

            Assert.That(sessionStart.ContainsKey("azureResourceName"), Is.False,
                "SessionStart must not send an empty resource key");
            Assert.That(context.ContainsKey("azureResourceName"), Is.False,
                "ConversationContext must not send an empty resource key");
        }
    }
}
