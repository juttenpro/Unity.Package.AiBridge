using System;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using Tsc.AIBridge.Auth;
using Tsc.AIBridge.Messages;
using Tsc.AIBridge.WebSocket;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: telemetry never disturbs a conversation turn. Without a connection the client turn metrics
    /// are dropped (false) without a connection attempt, and a send that fails (the socket closes while sending) stays
    /// inside: false, no exception.
    ///
    /// WHY: lead review of 5.22.0 (717c40c), 2026-11-04. The first version called EnsureConnectionAsync, so a listen that
    /// ended after a disconnect opened a connection (JWT request, handshake) just to send telemetry, against its own doc.
    /// And the app sends it fire-and-forget: an exception from SendJsonAsync had nobody to catch it.
    /// WHAT: a WebSocketClient without a connection, with a counting auth service: false and no token request. The send
    /// path with a send that throws synchronously and one that fails asynchronously: false, nothing escapes.
    /// NOT COVERED: a real socket closing mid-send (the helper is what the client calls; the socket is not faked).
    /// </summary>
    [TestFixture]
    public class ClientTurnMetricsSendTests
    {
        const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

        sealed class CountingAuth : IAuthenticationService
        {
            public int Requests;
            public Task<string> GetAuthTokenAsync(string userId, string role, string apiKey)
            {
                Requests++;
                return Task.FromResult<string>(null);
            }
        }

        static ClientTurnMetricsMessage Metrics() => new() { RequestId = "r1", Outcome = ClientTurnOutcome.Stopped };

        [Test]
        public void WithoutConnection_ReturnsFalse_AndMakesNoConnectionAttempt()
        {
            var go = new GameObject("ClientTurnMetricsSendTests");
            try
            {
                // EditMode AddComponent runs no Awake: install the collaborators a connection attempt would use.
                var client = go.AddComponent<WebSocketClient>();
                var auth = new CountingAuth();
                typeof(WebSocketClient).GetField("_authService", PrivateInstance).SetValue(client, auth);
                typeof(WebSocketClient).GetField("_apiKeyProvider", PrivateInstance).SetValue(client, new SimpleApiKeyProvider(string.Empty));
                // A connection attempt with this fake auth fails with an error log; the count below is the evidence.
                LogAssert.ignoreFailingMessages = true;

                var sent = client.SendClientTurnMetricsAsync(Metrics()).GetAwaiter().GetResult();

                Assert.That(sent, Is.False);
                Assert.That(auth.Requests, Is.Zero, "Telemetry opened a connection (a JWT request) just to send itself.");
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void ThrowingSend_ReturnsFalse_AndNothingEscapes()
        {
            Exception reported = null;

            var sync = WebSocketClient.SendTelemetryAsync(() => true, () => throw new InvalidOperationException("closed"), e => reported = e);
            Assert.That(() => sync.GetAwaiter().GetResult(), Throws.Nothing, "A send that throws escaped into the turn.");
            Assert.That(sync.Result, Is.False);
            Assert.That(reported, Is.TypeOf<InvalidOperationException>(), "The failure was not reported to the verbose log.");

            var failing = WebSocketClient.SendTelemetryAsync(() => true, () => Task.FromException(new ObjectDisposedException("socket")), _ => { });
            Assert.That(() => failing.GetAwaiter().GetResult(), Throws.Nothing, "A send that fails asynchronously escaped into the turn.");
            Assert.That(failing.Result, Is.False);
        }

        [Test]
        public void NotConnected_DoesNotSend()
        {
            var sends = 0;

            var sent = WebSocketClient.SendTelemetryAsync(() => false, () => { sends++; return Task.CompletedTask; }, _ => { });

            Assert.That(sent.GetAwaiter().GetResult(), Is.False);
            Assert.That(sends, Is.Zero);
        }
    }
}
