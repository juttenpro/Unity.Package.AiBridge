using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using Tsc.AIBridge.Auth;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: a token request that never reached the server is repeated exactly once, after a short wait;
    /// an answer from the server is never repeated.
    ///
    /// WHY: Training Platform production report of 2026-11-12 (Monday 3357003238). A Quest 3S failed its first token
    /// request with "Cannot resolve destination host" and showed "Authentication failed"; Cloud Run shows the same network
    /// getting a token 8 s later. A repeat on a 401 would only double the load of a wrong key and delay the real error.
    /// WHAT: the order of sends, the wait and the warning, with distinct attempts so the returned one can be told apart.
    /// NOT COVERED: JwtAuthenticationService's mapping of UnityWebRequest results (ConnectionError = never reached the
    /// server) and its per-attempt timeout; real DNS behaviour on a headset. A repeat does not help when a firewall
    /// blocks the host: both attempts fail.
    /// </summary>
    [TestFixture]
    public class TokenRequestRetryTests
    {
        sealed class Attempt
        {
            public readonly string Name;
            public readonly bool ReachedServer;
            public Attempt(string name, bool reachedServer) { Name = name; ReachedServer = reachedServer; }
        }

        List<string> _events;

        [SetUp]
        public void SetUp() => _events = new List<string>();

        Attempt Run(params Attempt[] attempts)
        {
            var next = 0;
            return TokenRequestRetry.SendAsync(
                    send: () =>
                    {
                        _events.Add("send");
                        if (next >= attempts.Length)
                            Assert.Fail($"Token requested {next + 1} times; at most {attempts.Length} were expected.");
                        return Task.FromResult(attempts[next++]);
                    },
                    neverReachedServer: a => !a.ReachedServer,
                    onRetry: a => _events.Add("warn " + a.Name),
                    delay: d =>
                    {
                        _events.Add("wait " + d.TotalSeconds + "s");
                        return Task.CompletedTask;
                    })
                .GetAwaiter().GetResult();
        }

        [Test]
        public void NoAnswerFirst_IsRepeatedOnceAfterTwoSeconds_AndTheRepeatCounts()
        {
            var result = Run(new Attempt("dns-failure", false), new Attempt("token", true));

            Assert.That(_events, Is.EqualTo(new[] { "send", "warn dns-failure", "wait 2s", "send" }),
                "A headset whose network was not ready yet must try once more after 2 s; otherwise it shows " +
                "'Authentication failed' while the network works seconds later.");
            Assert.That(result.Name, Is.EqualTo("token"), "The repeat got a token, but the caller got the failed first attempt.");
        }

        [Test]
        public void AnswerFromTheServer_IsNeverRepeated()
        {
            var result = Run(new Attempt("401", true));

            Assert.That(_events, Is.EqualTo(new[] { "send" }),
                "A refusal by the server (wrong key, 4xx, 5xx) was repeated: double load and a later error, nothing gained.");
            Assert.That(result.Name, Is.EqualTo("401"));
        }

        [Test]
        public void NoAnswerTwice_StopsAfterOneRepeat_AndReturnsTheRepeat()
        {
            var result = Run(new Attempt("dns-failure-1", false), new Attempt("dns-failure-2", false));

            Assert.That(_events, Is.EqualTo(new[] { "send", "warn dns-failure-1", "wait 2s", "send" }),
                "More than one repeat: start-up waits longer on a network that blocks the server (school firewall).");
            Assert.That(result.Name, Is.EqualTo("dns-failure-2"), "The error shown must be the last attempt's.");
        }

        [Test]
        public void FirstAttemptSucceeds_NoWaitAndNoWarning()
        {
            var result = Run(new Attempt("token", true));

            Assert.That(_events, Is.EqualTo(new[] { "send" }), "A working start-up got slower or logged a warning.");
            Assert.That(result.Name, Is.EqualTo("token"));
        }
    }
}
