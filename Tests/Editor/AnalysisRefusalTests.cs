using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NUnit.Framework;
using Tsc.AIBridge.Services;
using UnityEngine;
using UnityEngine.TestTools;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: when the backend refuses an analysis request, the caller learns that
    /// immediately and learns WHY.
    ///
    /// WHY: the request waited out its full 30-second timeout and then reported "timed out". The player
    /// sat for 34.8 seconds and was given the wrong reason, while the real one had been on screen for
    /// half a minute (session log 2026-09-08 10:36 — refused at :10, reported at :40). And the real
    /// reason mattered: "Vertex AI conversation requires at least one user message" tells a content
    /// creator exactly which prompt to fix. "Timed out" tells nobody anything.
    ///
    /// The delivery path already existed. AnalysisService registers ITSELF as the handler for its
    /// requestId, exactly as an NpcClient does, and every other message for a requestId reaches its
    /// handler that way. WebSocketClient's error branch was the one place that logged a message and then
    /// returned without delivering it.
    /// </summary>
    [TestFixture]
    public class AnalysisRefusalTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

        private AnalysisService _service;

        [SetUp]
        public void SetUp()
        {
            // Singleton with a private constructor. The tests below only touch the pending-request
            // dictionary and the refusal path, so the shared instance is fine — TearDown empties it.
            _service = AnalysisService.Instance;
        }

        [TearDown]
        public void TearDown() => PendingRequests().Clear();

        [Test]
        public void ARefusalCompletesThePendingRequestWithTheBackendsOwnWords()
        {
            var tcs = PendRequest("analysis-1");

            LogAssert.Expect(LogType.Warning, new Regex("The backend refused analysis analysis-1"));
            Assert.IsTrue(_service.TryFailPendingRequestFromError(ErrorJson("analysis-1",
                "Vertex AI conversation requires at least one user message.")));

            Assert.IsTrue(tcs.Task.IsFaulted, "The request must end now, not on its 30-second timeout.");
            var refusal = tcs.Task.Exception?.GetBaseException() as AnalysisRefusedException;
            Assert.IsNotNull(refusal, "A refusal is not a timeout and must not be reported as one.");
            StringAssert.Contains("at least one user message", refusal.Message,
                "The backend's text is the only thing that says what to fix — pass it through verbatim.");
        }

        [Test]
        public void DetailsAreCarriedAlongWhenThereAreAny()
        {
            var tcs = PendRequest("analysis-1");

            LogAssert.Expect(LogType.Warning, new Regex("refused analysis"));
            _service.TryFailPendingRequestFromError(
                "{\"type\":\"Error\",\"requestId\":\"analysis-1\",\"message\":\"Bad prompt\",\"details\":\"messages[0] was empty\"}");

            var message = tcs.Task.Exception?.GetBaseException().Message ?? "";
            StringAssert.Contains("Bad prompt", message);
            StringAssert.Contains("messages[0] was empty", message);
        }

        [Test]
        public void AnErrorForSomeoneElsesRequestIsLeftAlone()
        {
            // Errors name one turn. Failing our own request because a different one broke would be worse
            // than doing nothing.
            var tcs = PendRequest("analysis-1");

            Assert.IsFalse(_service.TryFailPendingRequestFromError(ErrorJson("some-other-turn", "Boom")));
            Assert.IsFalse(tcs.Task.IsCompleted);
        }

        [Test]
        public void AnErrorWithNoRequestIdIsLeftAlone()
        {
            var tcs = PendRequest("analysis-1");

            Assert.IsFalse(_service.TryFailPendingRequestFromError("{\"type\":\"Error\",\"message\":\"Boom\"}"));
            Assert.IsFalse(tcs.Task.IsCompleted);
        }

        [Test]
        public void AnAnalysisResponseIsNotMistakenForAnError()
        {
            // The happy path still has to walk past this check untouched.
            var tcs = PendRequest("analysis-1");

            Assert.IsFalse(_service.TryFailPendingRequestFromError(
                "{\"type\":\"analysisResponse\",\"requestId\":\"analysis-1\",\"analysis\":\"{}\"}"));
            Assert.IsFalse(tcs.Task.IsCompleted);
        }

        [Test]
        public void MalformedErrorJsonDoesNotThrowIntoTheMessagePump()
        {
            // This runs from WebSocketClient's message handling. An exception here would take the whole
            // socket dispatch down.
            PendRequest("analysis-1");

            LogAssert.Expect(LogType.Warning, new Regex("Could not read a backend error message"));
            Assert.IsFalse(_service.TryFailPendingRequestFromError("{\"type\":\"Error\", this is not json"));
        }

        #region The wiring — which is what actually broke

        [Test]
        public void AnErrorArrivingAsAMessageFailsThePendingRequest()
        {
            // The tests above call the refusal helper directly and stay green even with the call site
            // removed. This one goes through OnTextMessage, which is what WebSocketClient invokes.
            var tcs = PendRequest("analysis-1");

            LogAssert.Expect(LogType.Warning, new Regex("refused analysis analysis-1"));
            _service.OnTextMessage(ErrorJson("analysis-1", "Vertex AI conversation requires at least one user message."));

            Assert.IsTrue(tcs.Task.IsFaulted,
                "Reaching the refusal path from the real entry point is the behaviour. Checking the " +
                "helper in isolation passes whether or not anything calls it.");
        }

        [Test]
        public void WebSocketClientDeliversAnErrorToTheHandlerWaitingOnThatRequest()
        {
            // The other half, and the original defect: the error branch logged the message and returned
            // without delivering it, so nothing waiting on that requestId ever heard. AnalysisService
            // registers ITSELF as the handler for its requestId, exactly as an NpcClient does.
            var go = new GameObject("TestWebSocketClient");
            try
            {
                var socket = go.AddComponent<Tsc.AIBridge.WebSocket.WebSocketClient>();
                var handler = new RecordingHandler();
                socket.RegisterNpc("analysis-1", handler);

                LogAssert.ignoreFailingMessages = true; // the branch logs the error itself
                typeof(Tsc.AIBridge.WebSocket.WebSocketClient)
                    .GetMethod("HandleTextMessage", PrivateInstance)
                    .Invoke(socket, new object[] { ErrorJson("analysis-1", "Boom") });
                LogAssert.ignoreFailingMessages = false;

                Assert.AreEqual(1, handler.Received.Count,
                    "An error names one turn, and the handler for that turn is waiting on it. Logging it " +
                    "and returning is how a refused analysis sat for its full 30-second timeout.");
                StringAssert.Contains("Boom", handler.Received[0]);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void AnErrorForAnUnrelatedRequestReachesNobody()
        {
            var go = new GameObject("TestWebSocketClient");
            try
            {
                var socket = go.AddComponent<Tsc.AIBridge.WebSocket.WebSocketClient>();
                var handler = new RecordingHandler();
                socket.RegisterNpc("analysis-1", handler);

                LogAssert.ignoreFailingMessages = true;
                typeof(Tsc.AIBridge.WebSocket.WebSocketClient)
                    .GetMethod("HandleTextMessage", PrivateInstance)
                    .Invoke(socket, new object[] { ErrorJson("a-different-turn", "Boom") });
                LogAssert.ignoreFailingMessages = false;

                Assert.AreEqual(0, handler.Received.Count,
                    "No broadcast fallback: telling every NPC about one turn's error is worse than " +
                    "telling none.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        private sealed class RecordingHandler : Tsc.AIBridge.WebSocket.INpcMessageHandler
        {
            public readonly System.Collections.Generic.List<string> Received =
                new System.Collections.Generic.List<string>();

            public void OnTextMessage(string json) => Received.Add(json);
            public void OnBinaryMessage(byte[] data) { }
            public void OnRequestComplete(string requestId) { }
        }

        #endregion

        // --- helpers ---------------------------------------------------------------------------------

        private static string ErrorJson(string requestId, string message) =>
            "{\"type\":\"Error\",\"requestId\":\"" + requestId + "\",\"message\":\"" + message + "\"}";

        /// <summary>Puts a request in flight, the way RequestAnalysisAsync does.</summary>
        private TaskCompletionSource<AnalysisService.AnalysisResponse> PendRequest(string requestId)
        {
            var tcs = new TaskCompletionSource<AnalysisService.AnalysisResponse>();
            Assert.IsTrue(PendingRequests().TryAdd(requestId, tcs));
            return tcs;
        }

        private ConcurrentDictionary<string, TaskCompletionSource<AnalysisService.AnalysisResponse>> PendingRequests()
        {
            var field = typeof(AnalysisService).GetField("_pendingRequests", PrivateInstance);
            Assert.IsNotNull(field, "_pendingRequests not found on AnalysisService");
            return (ConcurrentDictionary<string, TaskCompletionSource<AnalysisService.AnalysisResponse>>)
                field.GetValue(_service);
        }
    }
}
