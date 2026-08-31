using System;
using System.Collections.Generic;
using NUnit.Framework;
using Tsc.AIBridge.WebSocket;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: some inbound messages are owned by a CAPABILITY, not by the conversation
    /// turn they happen to arrive on. <c>prosodyresult</c> is the first: it describes the PLAYER's
    /// voice and is meaningless to the NPC whose requestId carries it.
    ///
    /// WHY this class exists: <see cref="WebSocketClient"/> could only answer "who owns this
    /// requestId", and that is a Dictionary with exactly one handler per id. A player-scoped consumer
    /// therefore had nowhere to register, so prosody was bolted onto the active NpcClient — which in
    /// turn needed a scene reference to reach the rule system. That reference pointed into a
    /// FeatureFilter container, the container was destroyed on AI-coach builds, and the vocal
    /// measurement silently produced nothing for a week (2026-08-28). The routing gap is the root
    /// cause; this router closes it.
    ///
    /// SUCCESS CRITERIA:
    /// - A type subscriber receives messages of its type and nothing else.
    /// - A message that merely CONTAINS the type string elsewhere (transcript text, nested field) is
    ///   NOT dispatched — the cheap substring scan must always be confirmed against the real "type".
    /// - Unhandled types report false so the caller falls through to the existing requestId routing.
    /// - One throwing subscriber never starves the others and never escapes into the socket loop.
    /// </summary>
    [TestFixture]
    public class MessageTypeRoutingTests
    {
        private const string ProsodyType = "prosodyresult";

        private MessageTypeRouter _router;
        private List<string> _received;

        [SetUp]
        public void SetUp()
        {
            _router = new MessageTypeRouter();
            _received = new List<string>();
        }

        private static string Envelope(string type, string requestId = "req-1", string text = "")
            => $"{{\"type\":\"{type}\",\"requestId\":\"{requestId}\",\"text\":\"{text}\"}}";

        [Test]
        public void TryDispatch_SubscribedType_DeliversAndReportsHandled()
        {
            _router.Subscribe(ProsodyType, json => _received.Add(json));

            var handled = _router.TryDispatch(Envelope(ProsodyType));

            Assert.IsTrue(handled, "A subscribed type must report handled so the caller stops routing.");
            Assert.AreEqual(1, _received.Count);
        }

        [Test]
        public void TryDispatch_UnsubscribedType_ReportsUnhandledAndDeliversNothing()
        {
            _router.Subscribe(ProsodyType, json => _received.Add(json));

            var handled = _router.TryDispatch(Envelope("aiResponse"));

            Assert.IsFalse(handled, "An unowned type must fall through to requestId routing.");
            CollectionAssert.IsEmpty(_received);
        }

        [Test]
        public void TryDispatch_NoSubscribersAtAll_ReportsUnhandled()
        {
            Assert.IsFalse(_router.TryDispatch(Envelope(ProsodyType)));
        }

        /// <summary>
        /// The guard that makes the cheap pre-scan safe: a transcript that quotes the type name must
        /// not be mistaken for the message itself. Without the confirm step this hijacks the turn.
        /// </summary>
        [Test]
        public void TryDispatch_TypeNameOnlyInPayloadText_IsNotDispatched()
        {
            _router.Subscribe(ProsodyType, json => _received.Add(json));

            var handled = _router.TryDispatch(Envelope("transcription", text: "we bespraken de prosodyresult"));

            Assert.IsFalse(handled, "Only the 'type' field decides ownership, never a substring hit.");
            CollectionAssert.IsEmpty(_received);
        }

        [Test]
        public void TryDispatch_WireTypeCasingDiffers_StillDispatches()
        {
            _router.Subscribe(ProsodyType, json => _received.Add(json));

            Assert.IsTrue(_router.TryDispatch(Envelope("ProsodyResult")));
            Assert.AreEqual(1, _received.Count);
        }

        [Test]
        public void TryDispatch_MultipleSubscribers_AllReceive()
        {
            var second = new List<string>();
            _router.Subscribe(ProsodyType, json => _received.Add(json));
            _router.Subscribe(ProsodyType, json => second.Add(json));

            _router.TryDispatch(Envelope(ProsodyType));

            Assert.AreEqual(1, _received.Count);
            Assert.AreEqual(1, second.Count);
        }

        [Test]
        public void Unsubscribe_StopsDelivery_AndReleasesTheType()
        {
            Action<string> handler = json => _received.Add(json);
            _router.Subscribe(ProsodyType, handler);
            _router.Unsubscribe(ProsodyType, handler);

            var handled = _router.TryDispatch(Envelope(ProsodyType));

            Assert.IsFalse(handled, "The last unsubscribe must hand the type back to requestId routing.");
            CollectionAssert.IsEmpty(_received);
        }

        [Test]
        public void Unsubscribe_UnknownHandler_LeavesRemainingSubscriberIntact()
        {
            _router.Subscribe(ProsodyType, json => _received.Add(json));
            _router.Unsubscribe(ProsodyType, json => { });

            Assert.IsTrue(_router.TryDispatch(Envelope(ProsodyType)));
            Assert.AreEqual(1, _received.Count);
        }

        /// <summary>
        /// A consumer throwing must not starve the next consumer, and must never escape into the
        /// WebSocket receive loop — one bad subscriber would otherwise kill the whole connection.
        /// </summary>
        [Test]
        public void TryDispatch_SubscriberThrows_OtherSubscribersStillRunAndNothingEscapes()
        {
            _router.Subscribe(ProsodyType, json => throw new InvalidOperationException("boom"));
            _router.Subscribe(ProsodyType, json => _received.Add(json));

            bool handled = false;
            Assert.DoesNotThrow(() => handled = _router.TryDispatch(Envelope(ProsodyType)));

            Assert.IsTrue(handled);
            Assert.AreEqual(1, _received.Count, "The healthy subscriber must still have been served.");
        }

        [Test]
        public void TryDispatch_MalformedJson_ReportsUnhandledWithoutThrowing()
        {
            _router.Subscribe(ProsodyType, json => _received.Add(json));

            bool handled = true;
            Assert.DoesNotThrow(() => handled = _router.TryDispatch("{\"type\":\"prosodyresult\""));

            Assert.IsFalse(handled);
            CollectionAssert.IsEmpty(_received);
        }

        [Test]
        public void TryDispatch_NullOrEmpty_ReportsUnhandled()
        {
            _router.Subscribe(ProsodyType, json => _received.Add(json));

            Assert.IsFalse(_router.TryDispatch(null));
            Assert.IsFalse(_router.TryDispatch(string.Empty));
        }

        [Test]
        public void Subscribe_NullTypeOrHandler_IsIgnored()
        {
            Assert.DoesNotThrow(() => _router.Subscribe(null, json => _received.Add(json)));
            Assert.DoesNotThrow(() => _router.Subscribe(ProsodyType, null));

            Assert.IsFalse(_router.TryDispatch(Envelope(ProsodyType)));
        }
    }
}
