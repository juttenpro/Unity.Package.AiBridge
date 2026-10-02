using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Tsc.AIBridge.Core;
using Tsc.AIBridge.WebSocket;
using UnityEngine;
using UnityEngine.TestTools;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: a backend error on one conversation turn never ends the session.
    ///
    /// WHY: the host project's ErrorHandler turns every Debug.LogError into the fatal "the application
    /// needs to be restarted" popup, whose only button quits the app. It lets one prefix through as
    /// non-fatal: "[BACKEND ERROR", the line WebSocketClient's error branch writes. Since v5.11.0 that
    /// branch also delivers the error to the turn's handler, and ConversationMetadataHandler logged it a
    /// second time as a [UserError:...] LogError — which is fatal. Reported 2026-10-02 (Agressietraining,
    /// VR): Voxtral's guardrail refused one NPC sentence, the "voice unavailable" caption showed the text
    /// as designed, and the popup behind it closed the app.
    ///
    /// NOT COVERED: the host side (that "[BACKEND ERROR" is registered as non-fatal is pinned by
    /// ErrorHandlerTests in the Training Platform), and NpcClientBase.OnTextMessage itself — the handler
    /// below forwards to a real ConversationMetadataHandler the same way, without the MonoBehaviour.
    /// </summary>
    [TestFixture]
    public class BackendTurnErrorSeverityTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

        [Test]
        public void ARefusedTtsSentence_LogsOneErrorLine_TheBackendErrorTheHostTreatsAsNonFatal()
        {
            var logs = new List<(LogType Type, string Text)>();
            Application.LogCallback capture = (condition, _, type) => logs.Add((type, condition));

            var go = new GameObject("TestWebSocketClient");
            try
            {
                var socket = go.AddComponent<WebSocketClient>();
                socket.RegisterNpc("turn-1", new ForwardingNpcHandler("LuciusDeVries"));

                LogAssert.ignoreFailingMessages = true; // the error branch logs the error itself
                Application.logMessageReceived += capture;
                typeof(WebSocketClient)
                    .GetMethod("HandleTextMessage", PrivateInstance)
                    .Invoke(socket, new object[] { TtsGuardrailErrorJson("turn-1") });
            }
            finally
            {
                Application.logMessageReceived -= capture;
                LogAssert.ignoreFailingMessages = false;
                Object.DestroyImmediate(go);
            }

            var errors = logs.Where(l => l.Type == LogType.Error).Select(l => l.Text).ToList();
            Assert.AreEqual(1, errors.Count,
                "One refused TTS sentence must leave exactly one Error line. Every other Error line is a " +
                "fatal popup in the host app that quits on close — the session ends over a sentence the " +
                "caption already showed. Error lines:\n" + string.Join("\n", errors));
            StringAssert.StartsWith("[BACKEND ERROR", errors[0],
                "The host lets backend errors through as non-fatal by this prefix only.");

            Assert.IsTrue(logs.Any(l => l.Type == LogType.Warning
                                        && l.Text.Contains("LuciusDeVries") && l.Text.Contains("TTS_ERROR")),
                "The turn's own line, naming the NPC and the error code, must stay in the log as a warning: " +
                "the [BACKEND ERROR] line does not say which NPC it was.");
        }

        private static string TtsGuardrailErrorJson(string requestId) =>
            "{\"type\":\"Error\",\"requestId\":\"" + requestId + "\",\"code\":\"TTS_ERROR\"," +
            "\"message\":\"Voxtral TTS error (Forbidden): Request blocked by guardrail policy\"}";

        /// <summary>Forwards text the way NpcClientBase.OnTextMessage does.</summary>
        private sealed class ForwardingNpcHandler : INpcMessageHandler
        {
            private readonly ConversationMetadataHandler _metadata;

            public ForwardingNpcHandler(string personaName) =>
                _metadata = new ConversationMetadataHandler(personaName, new LatencyTracker(personaName));

            public void OnTextMessage(string json) => _metadata.ProcessMessage(json);
            public void OnBinaryMessage(byte[] data) { }
            public void OnRequestComplete(string requestId) { }
        }
    }
}
