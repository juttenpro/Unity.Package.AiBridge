using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Tsc.AIBridge.Auth;
using Tsc.AIBridge.Core;
using Tsc.AIBridge.Messages;
using Tsc.AIBridge.WebSocket;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// Listener reactions (DA3): while the player holds the talk button and pauses, the NPC may nod or say "uhu". The set
    /// rides on SessionStart, the client asks per pause, the backend answers with the reaction to play.
    /// WHY: the wire must match ApiOrchestrator (d319a03) exactly; a wrong key is silently ignored there and the NPC just
    /// never reacts. Old personas without a set must send nothing new. Asking must never disturb the turn.
    /// NOT COVERED: the copy ConversationRequest.ListenerReactions → SessionStart inside RequestOrchestrator's coroutine
    /// (same one-line copy as LlmFallback; the TP demo shows it); playing the reaction and the pause detection (TP
    /// client); a real socket.
    /// </summary>
    [TestFixture]
    public class ListenerReactionTests
    {
        static ListenerReactionSet RecruiterSet() => new()
        {
            Scene = "Sollicitatiegesprek; de NPC is de recruiter.",
            MinConfidence = 0.8,
            Options = new List<ListenerReactionOption>
            {
                new() { Id = "R-Confirm", Description = "Instemmen", ClipText = "uhu" },
                new() { Id = "R-Confused", Description = "Niet begrijpen" },
            },
        };

        [Test]
        public void SessionStart_without_a_set_sends_no_listenerReactions_key()
        {
            var json = JObject.Parse(JsonConvert.SerializeObject(new SessionStartMessage { LanguageCode = "nl-NL" }));

            Assert.That(json.ContainsKey("listenerReactions"), Is.False,
                "A persona without a set announced listener reactions: the backend would start choosing for it.");
        }

        [Test]
        public void SessionStart_carries_the_set_in_the_backend_shape()
        {
            var json = JObject.Parse(JsonConvert.SerializeObject(new SessionStartMessage { ListenerReactions = RecruiterSet() }));

            var set = (JObject)json["listenerReactions"];
            Assert.That(set, Is.Not.Null, "The set never reached the backend: the NPC never reacts.");
            Assert.That((string)set["scene"], Is.EqualTo("Sollicitatiegesprek; de NPC is de recruiter."));
            Assert.That((double)set["minConfidence"], Is.EqualTo(0.8));
            var first = (JObject)set["options"][0];
            Assert.That((string)first["id"], Is.EqualTo("R-Confirm"));
            Assert.That((string)first["description"], Is.EqualTo("Instemmen"));
            Assert.That((string)first["clipText"], Is.EqualTo("uhu"), "Without clipText the backend cannot measure the clip leaking into the transcript.");
        }

        [Test]
        public void A_set_without_its_own_threshold_leaves_the_backend_default()
        {
            var set = RecruiterSet();
            set.MinConfidence = null;

            var json = JObject.Parse(JsonConvert.SerializeObject(set));

            Assert.That(json.ContainsKey("minConfidence"), Is.False, "A missing threshold went out as 0: every guess would play.");
        }

        [Test]
        public void SessionStart_carries_the_DA3b_content_fields_in_the_backend_shape()
        {
            var set = RecruiterSet();
            set.Instruction = "Welke reactie past nu bij een luisteraar?";
            set.NoneDescription = "Geen reactie: niets nieuws.";
            set.MinSecondsBetween = 1.5;
            set.Options[1].MinConfidence = 0.75;

            var json = (JObject)JObject.Parse(JsonConvert.SerializeObject(new SessionStartMessage { ListenerReactions = set }))["listenerReactions"];

            // DA3b (ApiOrchestrator 5ec7bbb): everything that steers the choice is content. A key the backend does not
            // know is silently ignored there, and the backend's neutral default is used instead of the creator's text.
            Assert.That((string)json["instruction"], Is.EqualTo("Welke reactie past nu bij een luisteraar?"),
                "The set's question never reached the backend: the creator's wording is ignored.");
            Assert.That((string)json["noneDescription"], Is.EqualTo("Geen reactie: niets nieuws."),
                "The set's none-text never reached the backend: the NPC reacts by the backend's default, not the content.");
            Assert.That((double)json["minSecondsBetween"], Is.EqualTo(1.5), "The set's gap never reached the backend.");
            Assert.That((double)json["options"][1]["minConfidence"], Is.EqualTo(0.75),
                "An option's own threshold never reached the backend: a wrong 'huh?' plays at the set's lower threshold.");
        }

        [Test]
        public void Empty_DA3b_fields_are_left_out_so_the_backend_uses_its_defaults()
        {
            var json = (JObject)JObject.Parse(JsonConvert.SerializeObject(new SessionStartMessage { ListenerReactions = RecruiterSet() }))["listenerReactions"];

            Assert.That(json.ContainsKey("instruction"), Is.False, "An empty question went out as a value instead of the backend default.");
            Assert.That(json.ContainsKey("noneDescription"), Is.False, "An empty none-text went out as a value instead of the backend default.");
            Assert.That(json.ContainsKey("minSecondsBetween"), Is.False, "An unset gap went out as a value instead of the backend default.");
            Assert.That(((JObject)json["options"][0]).ContainsKey("minConfidence"), Is.False,
                "An option without its own threshold sent one: the set's threshold no longer applies to it.");
        }

        [Test]
        public void The_request_names_the_turn_and_carries_nothing_else()
        {
            var json = JObject.Parse(JsonConvert.SerializeObject(new ListenerReactionRequestMessage { RequestId = "turn-7" }));

            Assert.That((string)json["type"], Is.EqualTo("ListenerReactionRequest"));
            Assert.That((string)json["requestId"], Is.EqualTo("turn-7"), "Without the turn id the backend cannot find the session.");
        }

        [Test]
        public void The_answer_from_the_backend_reads_back_the_reaction_and_its_confidence()
        {
            const string backend = @"{""type"":""ListenerReaction"",""requestId"":""turn-7"",""reactionId"":""R-Confirm"",""confidence"":0.91}";

            var message = JsonConvert.DeserializeObject<ListenerReactionMessage>(backend);

            Assert.That(message.ReactionId, Is.EqualTo("R-Confirm"));
            Assert.That(message.Confidence, Is.EqualTo(0.91).Within(1e-9));
            Assert.That(message.RequestId, Is.EqualTo("turn-7"));
        }

        [Test]
        public void Type_constants_match_the_backend()
        {
            Assert.That(WebSocketMessageTypes.ListenerReactionRequest, Is.EqualTo("ListenerReactionRequest"));
            Assert.That(WebSocketMessageTypes.ListenerReaction, Is.EqualTo("ListenerReaction"));
        }

        [Test]
        public void An_incoming_reaction_reaches_the_event_once_and_other_messages_do_not()
        {
            var handler = new ConversationMetadataHandler("TestNpc", new LatencyTracker("TestNpc"), false);
            var received = new List<ListenerReactionMessage>();
            handler.OnListenerReaction += received.Add;

            handler.ProcessMessage(@"{""type"":""NoTranscript"",""requestId"":""turn-7"",""reason"":""silence""}");
            handler.ProcessMessage(@"{""type"":""ListenerReaction"",""requestId"":""turn-7"",""reactionId"":""R-Confused"",""confidence"":0.75}");

            Assert.That(received, Has.Count.EqualTo(1), "A reaction was dropped or another message was taken for one.");
            Assert.That(received[0].ReactionId, Is.EqualTo("R-Confused"));
        }

        sealed class CountingAuth : IAuthenticationService
        {
            public int Requests;
            public Task<string> GetAuthTokenAsync(string userId, string role, string apiKey)
            {
                Requests++;
                return Task.FromResult<string>(null);
            }
        }

        [Test]
        public void Asking_without_a_connection_is_false_and_opens_no_connection()
        {
            const BindingFlags privateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
            var go = new GameObject("ListenerReactionTests");
            try
            {
                // EditMode AddComponent runs no Awake: install the collaborators a connection attempt would use.
                var client = go.AddComponent<WebSocketClient>();
                var auth = new CountingAuth();
                typeof(WebSocketClient).GetField("_authService", privateInstance).SetValue(client, auth);
                typeof(WebSocketClient).GetField("_apiKeyProvider", privateInstance).SetValue(client, new SimpleApiKeyProvider(string.Empty));
                LogAssert.ignoreFailingMessages = true;

                var sent = client.SendListenerReactionRequestAsync("turn-7").GetAwaiter().GetResult();

                Assert.That(sent, Is.False);
                Assert.That(auth.Requests, Is.Zero, "A pause opened a connection (a JWT request) mid-turn just to ask for a nod.");
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
                Object.DestroyImmediate(go);
            }
        }
    }
}
