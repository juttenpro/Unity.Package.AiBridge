using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Tsc.AIBridge.Messages;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: SessionStartMessage and ConversationContext must carry a persona's
    /// Cartesia pronunciation dictionary as "pronunciationDictId", the key the ApiOrchestrator reads
    /// (its PronunciationDictionaryFlowTests feed exactly this JSON through to the Cartesia payload).
    ///
    /// WHY: a different key is ignored by the backend without any error, and course terms are
    /// mispronounced again although the content creator picked a dictionary.
    ///
    /// NOT COVERED: the mapping from ConversationRequest onto these messages inside
    /// RequestOrchestrator (built inline in the send coroutine) — that needs a real session.
    /// </summary>
    [TestFixture]
    public class PronunciationDictSerializationTests
    {
        [Test]
        public void SessionStartMessage_Serializes_PronunciationDictId_With_Backend_Key()
        {
            var message = new SessionStartMessage { LanguageCode = "nl-NL", VoiceId = "v-1", PronunciationDictId = "pdict_jargon" };

            var parsed = JObject.Parse(JsonConvert.SerializeObject(message));

            Assert.That(parsed["pronunciationDictId"]?.Value<string>(), Is.EqualTo("pdict_jargon"),
                "the backend reads 'pronunciationDictId'; any other key leaves the NPC on default pronunciation");
        }

        [Test]
        public void ConversationContext_Serializes_PronunciationDictId_With_Backend_Key()
        {
            var context = new ConversationContext { voiceId = "v-1", pronunciationDictId = "pdict_jargon" };

            var parsed = JObject.Parse(JsonConvert.SerializeObject(context));

            Assert.That(parsed["pronunciationDictId"]?.Value<string>(), Is.EqualTo("pdict_jargon"),
                "NPC-initiated turns travel in ConversationContext and must carry the same dictionary");
        }
    }
}
