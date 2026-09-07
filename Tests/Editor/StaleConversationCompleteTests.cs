using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Tsc.AIBridge.Core;
using Tsc.AIBridge.WebSocket;
using UnityEngine;
using UnityEngine.TestTools;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: a late <c>conversationComplete</c> for an OLD turn must never tear
    /// down the turn that is currently active.
    ///
    /// WHY: 2026-06-12 robustness audit, client critical C4. The v1.17.1 fix added a RequestId
    /// check around <c>CompleteCurrentSession()</c>, but <c>OnConversationComplete</c> was still
    /// raised UNCONDITIONALLY — including in the "old session, ignoring cleanup" branch. The
    /// orchestrator's cleanup hook (HandleConversationCompleted) clears _micSession /
    /// _isRequestActive without any RequestId knowledge, so the chain was: user interrupts the
    /// NPC and immediately starts talking (turn N+1 active, recording); the backend still sends
    /// turn N's conversationComplete; the event fires; the orchestrator wipes turn N+1's state.
    /// PTT release then finds "no active request" → no EndOfSpeech, no transcript, no SttFailed →
    /// RuleSystem stays busy and the NPC is permanently mute until the player switches NPCs.
    ///
    /// WHAT: the handler only raises OnConversationComplete when the message's RequestId matches
    /// the orchestrator's current session (or when no orchestrator exists — the left-the-scene
    /// legacy path). Both subscribers want exactly that scope: the orchestrator's state cleanup,
    /// and NpcClient's voice-fallback subtitle (which must not fire for a stale turn either).
    ///
    /// SUCCESS CRITERIA:
    /// - stale complete (RequestId != current session): event NOT raised, active session intact
    /// - matching complete: event raised; audioReceived reflects StreamsReceived
    /// - no orchestrator instance: event still raised with audioReceived=false (legacy behaviour)
    /// </summary>
    [TestFixture]
    public class StaleConversationCompleteTests
    {
        private GameObject _orchestratorObject;
        private RequestOrchestrator _orchestrator;
        private ConversationMetadataHandler _handler;

        private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly FieldInfo SingletonField =
            typeof(RequestOrchestrator).GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly FieldInfo QuittingField =
            typeof(RequestOrchestrator).GetField("_isQuitting", BindingFlags.NonPublic | BindingFlags.Static);

        [SetUp]
        public void SetUp()
        {
            _orchestratorObject = new GameObject("TestOrchestrator");
            _orchestrator = _orchestratorObject.AddComponent<RequestOrchestrator>();
            // EditMode AddComponent does not run Awake's singleton assignment; install explicitly.
            SingletonField.SetValue(null, _orchestrator);

            // HasInstance is gated on the static _isQuitting too. OnApplicationQuit sets it true when a
            // prior Editor PlayMode session exits and nothing ever resets it, so it leaks into the
            // edit-mode domain these tests share. Clear it so HasInstance reflects "orchestrator present";
            // otherwise the handler takes the no-orchestrator branch and every assertion here flips.
            Assert.IsNotNull(QuittingField, "Field '_isQuitting' not found on RequestOrchestrator");
            QuittingField.SetValue(null, false);

            _handler = new ConversationMetadataHandler("TestNpc", new LatencyTracker("TestNpc"));
        }

        [TearDown]
        public void TearDown()
        {
            SingletonField.SetValue(null, null);
            if (_orchestratorObject != null)
                Object.DestroyImmediate(_orchestratorObject);
        }

        [Test]
        public void StaleConversationComplete_DoesNotRaiseEvent_AndLeavesActiveSessionIntact()
        {
            // Arrange: turn-2 is the ACTIVE session; turn-1's completion arrives late.
            SetCurrentSession("turn-2");
            var raised = false;
            _handler.OnConversationComplete += (_, __) => raised = true;

            // Act
            _handler.ProcessMessage(CompleteJson("turn-1"));

            // Assert
            Assert.IsFalse(raised,
                "a stale conversationComplete must not raise OnConversationComplete — the orchestrator's " +
                "cleanup hook clears state unconditionally and would kill the active turn");
            Assert.AreEqual("turn-2", _orchestrator.GetMicrophoneSessionId(),
                "the active session must survive a stale completion");
        }

        [Test]
        public void MatchingConversationComplete_NoAudio_RaisesEventWithAudioFalse_AndCompletesSession()
        {
            SetCurrentSession("turn-1");
            bool? audioReceived = null;
            _handler.OnConversationComplete += (_, received) => audioReceived = received;

            _handler.ProcessMessage(CompleteJson("turn-1"));

            Assert.IsNotNull(audioReceived, "a completion for the CURRENT session must raise the event");
            Assert.IsFalse(audioReceived.Value, "no streams were received, so audioReceived must be false");
            Assert.IsNull(_orchestrator.GetMicrophoneSessionId(),
                "the no-audio path completes the session via CompleteCurrentSession (pre-existing behaviour)");
        }

        [Test]
        public void MatchingConversationComplete_WithAudio_RaisesEventWithAudioTrue()
        {
            var session = SetCurrentSession("turn-1");
            session.StreamsReceived = 2;
            bool? audioReceived = null;
            _handler.OnConversationComplete += (_, received) => audioReceived = received;

            _handler.ProcessMessage(CompleteJson("turn-1"));

            Assert.IsNotNull(audioReceived, "a completion for the CURRENT session must raise the event");
            Assert.IsTrue(audioReceived.Value,
                "streams were received, so the voice-fallback subscriber must see audioReceived=true");
        }

        [Test]
        public void ConversationComplete_WithoutOrchestrator_StillRaisesEvent()
        {
            // Left-the-lesson-scene path: orchestrator destroyed, socket still connected.
            SingletonField.SetValue(null, null);
            Object.DestroyImmediate(_orchestratorObject);
            _orchestratorObject = null;

            bool? audioReceived = null;
            _handler.OnConversationComplete += (_, received) => audioReceived = received;

            _handler.ProcessMessage(CompleteJson("turn-1"));

            Assert.IsNotNull(audioReceived, "the legacy no-orchestrator path must keep raising the event");
            Assert.IsFalse(audioReceived.Value);
        }

        #region Helpers

        private ConversationSession SetCurrentSession(string requestId)
        {
            var session = new ConversationSession("TestNpc", requestId);
            var field = typeof(RequestOrchestrator).GetField("_micSession", PrivateInstance);
            Assert.IsNotNull(field, "Field '_micSession' not found on RequestOrchestrator");
            field.SetValue(_orchestrator, session);
            return session;
        }

        [Test]
        public void ConversationCompleteWithoutARequestId_IsIgnored()
        {
            // The dangerous shape: no id in the message AND no current session. The gate compares
            // currentSessionId to completeRequestId, and null == null is TRUE, so the completion used to
            // be accepted — on every NPC listening to this handler at once.
            var raised = false;
            _handler.OnConversationComplete += (_, __) => raised = true;

            LogAssert.Expect(LogType.Warning, new Regex("without a requestId"));
            _handler.ProcessMessage("{\"type\":\"conversationComplete\"}");

            Assert.IsFalse(raised,
                "A completion that names no turn must not be delivered as if it named this one.");
        }

        [Test]
        public void ConversationCompleteWithoutARequestId_LeavesAnActiveTurnAlone()
        {
            SetCurrentSession("turn-1");
            LogAssert.Expect(LogType.Warning, new Regex("without a requestId"));

            _handler.ProcessMessage("{\"type\":\"conversationComplete\"}");

            Assert.AreEqual("turn-1", _orchestrator.GetMicrophoneSessionId(),
                "An unidentifiable completion must never release the turn that is actually running.");
        }

        private static string CompleteJson(string requestId) =>
            "{\"type\":\"conversationComplete\",\"requestId\":\"" + requestId + "\"}";

        #endregion
    }
}
