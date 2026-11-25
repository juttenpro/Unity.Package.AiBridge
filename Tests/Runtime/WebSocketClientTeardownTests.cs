using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Tsc.AIBridge.Auth;
using Tsc.AIBridge.WebSocket;
using UnityEngine;
using UnityEngine.TestTools;

namespace Tsc.AIBridge.Tests.Runtime
{
    /// <summary>
    /// BUSINESS REQUIREMENT: a connection attempt that is cut off because the client is destroyed (a scene unload, application quit) is NORMAL
    /// behavior and is not reported as an error. A connection that fails while the client lives still is.
    ///
    /// WHY: <see cref="WebSocketClient"/> connects as soon as it starts (for an instant first turn). Leaving the scene while that attempt is
    /// still connecting destroyed the client, which made the attempt return false, and EnsureConnectionAsync then logged
    /// "[UserError:Connection lost. Please try again.] [Recoverable] ..." with Debug.LogError. In the Training Platform ErrorHandler that is
    /// non-fatal but still goes to Oops (noise for something nobody has to act on); in a Unity test it is an unhandled error that fails
    /// whichever test is running (Assistant Platform, slot 7a-2, 24-11: the Dutch-start Play test failed on it three times).
    ///
    /// ALSO: OnDestroy disposes the connection semaphore while that attempt still holds it. The Release in its finally then threw
    /// ObjectDisposedException into the task.
    ///
    /// WHAT: these tests use the REAL ORDER of a scene unload, not a flag set first: a connect is really pending (a local server that accepts
    /// and never answers the WebSocket handshake), the client is destroyed while it is pending, and the attempt resumes afterwards. Test (b)
    /// guards the other side: a connection that fails while the client lives is still the Recoverable Error. Not covered: the real backend,
    /// TLS, a plain DisconnectAsync without a destroy (the attempt is then not abandoned by a destroyed client).
    /// </summary>
    public class WebSocketClientTeardownTests
    {
        const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
        const float WaitSeconds = 20f;

        GameObject _clientObject;
        WebSocketClient _client;
        StallingServer _server;

        /// <summary>
        /// Each test removes its own client at once (DestroyImmediate runs OnDestroy now): a deferred Destroy only takes effect at the end of the frame, and the
        /// next test's client then met a still-registered singleton and destroyed itself in Awake (first run in the Training Platform, 25-11).
        /// </summary>
        [TearDown]
        public void TearDown()
        {
            if (_clientObject != null)
                UnityEngine.Object.DestroyImmediate(_clientObject);
            _server?.Dispose();
            _server = null;
        }

        /// <summary>
        /// A client whose Start never runs (disabled before the object is activated) and whose auth always gives a token, so the test starts the connect itself.
        /// Awake runs when the object is activated and logs an Error when no API key provider is assigned (an unhandled Error fails the test: that is what
        /// failed all three tests on their first run in the Training Platform, 25-11): so the object is built inactive, the provider is assigned the way the
        /// Inspector does (SerializedObject), and only then is it activated.
        /// </summary>
        void CreateClient(int port)
        {
            _clientObject = new GameObject("TestWebSocketClient");
            _clientObject.SetActive(false);
            var provider = _clientObject.AddComponent<FakeApiKeyProvider>();
            _client = _clientObject.AddComponent<WebSocketClient>();
            AssignApiKeyProvider(_client, provider);
            _client.enabled = false;
            _clientObject.SetActive(true);
            // Awake registers this client as the singleton; when another one is registered this one destroys itself and the test would measure nothing
            Assert.That(WebSocketClient.Instance, Is.SameAs(_client), "another WebSocketClient is the singleton: a second one destroys itself in Awake");
            _client.SetApiBaseUrl("http://127.0.0.1:" + port);
            typeof(WebSocketClient).GetField("_authService", PrivateInstance).SetValue(_client, new TokenAuthentication());
        }

        static void AssignApiKeyProvider(WebSocketClient client, MonoBehaviour provider)
        {
#if UNITY_EDITOR
            var serialized = new UnityEditor.SerializedObject(client);
            var property = serialized.FindProperty("apiKeyProviderComponent");
            Assert.That(property, Is.Not.Null, "WebSocketClient.apiKeyProviderComponent (the Inspector field) was renamed: the test must follow");
            property.objectReferenceValue = provider;
            serialized.ApplyModifiedPropertiesWithoutUndo();
#else
            Assert.Ignore("assigning the Inspector field needs the Editor");
#endif
        }

        Task<bool> StartConnect()
        {
            var ensure = typeof(WebSocketClient).GetMethod("EnsureConnectionAsync", PrivateInstance);
            return (Task<bool>)ensure.Invoke(_client, new object[] { CancellationToken.None });
        }

        static IEnumerator WaitUntil(Func<bool> condition, string what)
        {
            var deadline = Time.realtimeSinceStartup + WaitSeconds;
            while (!condition() && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(condition(), Is.True, $"not within {WaitSeconds} s: {what}");
        }

        [UnityTest]
        public IEnumerator ADestroyedClient_WhoseConnectIsCutOff_ReportsAWarning_NotAnError()
        {
            _server = StallingServer.Start();
            CreateClient(_server.Port);
            var connect = StartConnect();
            yield return WaitUntil(() => _server.AcceptedCount > 0 || connect.IsCompleted, "the connect reached the server");
            Assert.That(connect.IsCompleted, Is.False, "precondition: the connect must still be pending (the server never answers the handshake)");

            LogAssert.Expect(LogType.Warning, new Regex(@"Connection attempt abandoned: the client was destroyed"));
            UnityEngine.Object.Destroy(_clientObject);   // what a scene unload does
            yield return WaitUntil(() => connect.IsCompleted, "the cut-off connect to finish");

            // an Error logged in the meantime fails this test (Unity's unhandled-log check): that is the failure this fixes
            Assert.That(connect.Status, Is.Not.EqualTo(TaskStatus.Canceled), "precondition: the attempt must have finished by itself");
        }

        [UnityTest]
        public IEnumerator ADestroyedClient_WhoseConnectIsCutOff_DoesNotThrowFromTheSemaphoreRelease()
        {
            _server = StallingServer.Start();
            CreateClient(_server.Port);
            var connect = StartConnect();
            yield return WaitUntil(() => _server.AcceptedCount > 0 || connect.IsCompleted, "the connect reached the server");
            Assert.That(connect.IsCompleted, Is.False, "precondition: the connect must still be pending");

            LogAssert.ignoreFailingMessages = true;   // this test is about the exception of the task, not about what is logged (test above)
            UnityEngine.Object.Destroy(_clientObject);
            yield return WaitUntil(() => connect.IsCompleted, "the cut-off connect to finish");

            Assert.That(connect.IsFaulted, Is.False,
                "OnDestroy disposes the connection semaphore while the attempt holds it; the release in EnsureConnectionAsync's finally threw: " + connect.Exception?.GetBaseException().GetType().Name);
        }

        [UnityTest]
        public IEnumerator ALivingClient_WhoseConnectFails_StillReportsTheRecoverableError()
        {
            var closedPort = StallingServer.FreePortNothingListensOn();
            CreateClient(closedPort);

            LogAssert.Expect(LogType.Error, new Regex(@"\[UserError:Connection lost\. Please try again\.\] \[Recoverable\] \[UnifiedWebSocket\] Failed to establish connection"));
            var connect = StartConnect();
            yield return WaitUntil(() => connect.IsCompleted, "the failing connect to finish");

            Assert.That(connect.IsFaulted, Is.False);
            Assert.That(connect.Result, Is.False, "nothing listens on the port: the connect must fail");
        }

        sealed class FakeApiKeyProvider : MonoBehaviour, IApiKeyProvider
        {
            public string GetOrchestratorApiKey() => "test-api-key";
        }

        sealed class TokenAuthentication : IAuthenticationService
        {
            public Task<string> GetAuthTokenAsync(string userId, string role, string apiKey) => Task.FromResult("test-token");
        }

        /// <summary>A local TCP server that accepts connections and never answers: a WebSocket handshake to it stays pending.</summary>
        sealed class StallingServer : IDisposable
        {
            readonly TcpListener _listener;
            readonly List<TcpClient> _accepted = new List<TcpClient>();
            bool _disposed;

            StallingServer(TcpListener listener)
            {
                _listener = listener;
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            }

            public int Port { get; }

            public int AcceptedCount
            {
                get { lock (_accepted) return _accepted.Count; }
            }

            public static StallingServer Start()
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var server = new StallingServer(listener);
                server.AcceptNext();
                return server;
            }

            public static int FreePortNothingListensOn()
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();
                return port;
            }

            void AcceptNext()
            {
                _listener.AcceptTcpClientAsync().ContinueWith(task =>
                {
                    if (task.IsFaulted || task.IsCanceled || _disposed) return;
                    lock (_accepted) _accepted.Add(task.Result);
                    AcceptNext();
                });
            }

            public void Dispose()
            {
                _disposed = true;
                try { _listener.Stop(); } catch (SocketException) { }
                lock (_accepted)
                {
                    foreach (var client in _accepted)
                        client.Close();
                    _accepted.Clear();
                }
            }
        }
    }
}
