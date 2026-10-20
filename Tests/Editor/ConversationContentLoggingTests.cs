using System.Collections.Generic;
using NUnit.Framework;
using Tsc.AIBridge.Core;
using Tsc.AIBridge.Messages;
using Tsc.AIBridge.WebSocket;
using UnityEngine;
using UnityEngine.TestTools;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// WHY: the STT transcript was logged on every turn (LatencyTracker), and raw backend messages on parse errors. In a
    /// medical app speech must stay out of the logs. With content logging off, the log keeps the event and its timing but
    /// not the words. On by default: apps that relied on these lines see no change.
    ///
    /// NOT COVERED: the WebSocketClient and NetworkMessageController lines (need a live socket), RequestOrchestrator's
    /// message dumps (need a session); they use the same switch.
    /// </summary>
    [TestFixture]
    public class ConversationContentLoggingTests
    {
        const string Secret = "het klopt geheim zinnetje";
        readonly List<string> _logs = new();

        [SetUp]
        public void SetUp()
        {
            _logs.Clear();
            Application.logMessageReceived += Capture;
            LogAssert.ignoreFailingMessages = true;
        }

        [TearDown]
        public void TearDown()
        {
            Application.logMessageReceived -= Capture;
            ConversationContentLogging.Enabled = true;
            LogAssert.ignoreFailingMessages = false;
        }

        void Capture(string condition, string stackTrace, LogType type) => _logs.Add(condition);

        static void LogTranscript()
        {
            var tracker = new LatencyTracker("persona");
            tracker.StartMeasurement();
            tracker.MarkSttComplete(Secret, new SttTiming { TotalProcessingMs = 120 });
        }

        [Test]
        public void Off_TranscriptIsNotLogged_ButTheSttEventIs()
        {
            ConversationContentLogging.Enabled = false;

            LogTranscript();

            Assert.That(_logs, Has.None.Contains(Secret), "the transcript reached the log with content logging off");
            Assert.That(_logs, Has.Some.Contains("STT:"), "the STT timing line itself must stay");
        }

        [Test]
        public void Default_TranscriptIsLoggedAsBefore()
        {
            LogTranscript();

            Assert.That(_logs, Has.Some.Contains(Secret), "the default changed: other apps lose their transcript log line");
        }

        [TestCase("{\"text\": \"" + Secret + "\"}")]   // no type field
        [TestCase("{ broken " + Secret)]                // not JSON
        public void Off_RawMessageIsNotLogged(string message)
        {
            ConversationContentLogging.Enabled = false;
            // Set here, not only in SetUp: the test framework resets it when the test starts, and broken JSON logs an error.
            LogAssert.ignoreFailingMessages = true;

            new ConversationMetadataHandler("persona", null).ProcessMessage(message);

            Assert.That(_logs, Has.None.Contains(Secret), "a raw backend message reached the log with content logging off");
            Assert.That(_logs, Is.Not.Empty, "the warning or error itself must stay");
        }

        /// <summary>
        /// "Off" must hold even when someone switches verbose logging on to debug: verbose lines are where transcripts and
        /// raw messages were logged most.
        /// </summary>
        [Test]
        public void Off_WithVerboseLogging_NpcClientLogsNoTranscriptOrRawMessage()
        {
            ConversationContentLogging.Enabled = false;
            var go = new GameObject("npc");
            try
            {
                var npc = go.AddComponent<VerboseNpc>();

                npc.Hear(Secret, "turn-1");
                npc.OnTextMessage("{\"type\":\"Unknown\",\"text\":\"" + Secret + "\"}");

                Assert.That(_logs, Has.None.Contains(Secret),
                    "verbose logging put conversation content in the log while content logging was off");
                Assert.That(_logs, Has.Some.Contains("Transcription"), "the verbose event line itself must stay");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        sealed class VerboseNpc : NpcClientBase
        {
            public override string NpcName => "verbose-npc";
            protected override void ValidateConfiguration() { }
            public void Hear(string transcript, string requestId)
            {
                enableVerboseLogging = true; // Awake does not run for components added in EditMode
                HandleTranscription(transcript, requestId);
            }
        }
    }
}
