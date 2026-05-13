using System.Reflection;
using NUnit.Framework;
using Tsc.AIBridge.Core;
using UnityEngine;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: After the backend signals a turn is complete (conversationComplete
    /// message), the client must clear its per-turn session state so the next user-turn starts
    /// from a clean slate with a fresh RequestId.
    ///
    /// WHY: Production incident 2026-05-11 — a VR training session locked up because the Unity
    /// client kept sending EndOfSpeech for an already-completed session-ID for 3.5 minutes.
    /// Backend session was cleaned up correctly (per-turn lifecycle), but the client's
    /// _currentSession state slot was never cleared on the happy path. Every subsequent
    /// recording-stopped event then sent EndOfSpeech for the stale RequestId, getting
    /// "Session not found" each time. Symptom: NPC silent, animation kept playing.
    ///
    /// WHAT: Tests that RequestOrchestrator clears _currentSession, _isRequestActive, and
    /// _isProcessingRequest when its internal HandleConversationCompleted hook is invoked.
    /// Also verifies that IsProcessingRequest() returns false after cleanup, allowing the
    /// next PTT to proceed.
    ///
    /// HOW: Uses reflection to set state then invoke the private HandleConversationCompleted
    /// method, mirroring the pattern used by DisconnectActiveRequestTests.
    ///
    /// SUCCESS CRITERIA:
    /// - _currentSession is null after conversationComplete cleanup
    /// - _isRequestActive is false after cleanup (prevents EndOfSpeech for stale session)
    /// - _isProcessingRequest is false after cleanup
    /// - IsProcessingRequest() returns false so RuleSystem can start the next turn
    /// - No exception when cleanup runs with no active session (defensive)
    ///
    /// BUSINESS IMPACT:
    /// - Failure = NPC permanently silent mid-training while animation keeps running,
    ///   user must reboot the headset to recover (incident 2026-05-11 cost ~2 hours of
    ///   downtime for a paying customer running a job-interview training)
    /// - Affects any long-running conversation; the longer the session, the higher the
    ///   chance of triggering a stale-state path
    /// </summary>
    [TestFixture]
    public class ConversationCompleteCleanupTests
    {
        private GameObject _orchestratorObject;
        private RequestOrchestrator _orchestrator;

        private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

        [SetUp]
        public void SetUp()
        {
            _orchestratorObject = new GameObject("TestOrchestrator");
            _orchestrator = _orchestratorObject.AddComponent<RequestOrchestrator>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_orchestratorObject != null)
                Object.DestroyImmediate(_orchestratorObject);
        }

        [Test]
        public void HandleConversationCompleted_WithActiveSession_ClearsCurrentSession()
        {
            // Arrange
            SetField("_currentSession", new ConversationSession("TestNpc", "test-request-id"));
            SetField("_isRequestActive", true);

            // Act
            InvokeConversationCompleted(audioReceived: true);

            // Assert
            Assert.IsNull(GetField<ConversationSession>("_currentSession"),
                "_currentSession must be cleared so the next PTT does not send EndOfSpeech for the stale RequestId");
        }

        [Test]
        public void HandleConversationCompleted_WithActiveSession_ClearsIsRequestActive()
        {
            // Arrange
            SetField("_currentSession", new ConversationSession("TestNpc", "test-request-id"));
            SetField("_isRequestActive", true);

            // Act
            InvokeConversationCompleted(audioReceived: true);

            // Assert
            Assert.IsFalse(GetField<bool>("_isRequestActive"),
                "_isRequestActive must be cleared so HandleRecordingStopped's guard rejects subsequent EndOfSpeech sends");
        }

        [Test]
        public void HandleConversationCompleted_WithActiveSession_ClearsIsProcessingRequest()
        {
            // Arrange
            SetField("_currentSession", new ConversationSession("TestNpc", "test-request-id"));
            SetField("_isProcessingRequest", true);

            // Act
            InvokeConversationCompleted(audioReceived: true);

            // Assert
            Assert.IsFalse(GetField<bool>("_isProcessingRequest"),
                "_isProcessingRequest must be cleared so IsProcessingRequest() returns false after the turn");
        }

        [Test]
        public void HandleConversationCompleted_WithActiveSession_IsProcessingRequestReturnsFalse()
        {
            // Arrange
            SetField("_currentSession", new ConversationSession("TestNpc", "test-request-id"));
            SetField("_isRequestActive", true);
            SetField("_isProcessingRequest", true);

            // Act
            InvokeConversationCompleted(audioReceived: true);

            // Assert
            Assert.IsFalse(_orchestrator.IsProcessingRequest(),
                "IsProcessingRequest() must return false after the turn so RuleSystem unblocks the next PTT");
        }

        [Test]
        public void HandleConversationCompleted_WithoutActiveSession_DoesNotThrow()
        {
            // Arrange: no session, no active request — equivalent to a duplicate / late
            // conversationComplete arriving after another cleanup path (disconnect, cancel) already ran.
            // Defensive: must not crash the orchestrator.

            // Act + Assert
            Assert.DoesNotThrow(() => InvokeConversationCompleted(audioReceived: false),
                "Cleanup must be idempotent — a duplicate conversationComplete must not throw");
        }

        [Test]
        public void HandleConversationCompleted_AudioReceivedFalse_StillClearsState()
        {
            // Arrange: backend can send conversationComplete with audioReceived=false (e.g. on
            // interruption, no-transcript path). State cleanup must happen regardless of the flag.
            SetField("_currentSession", new ConversationSession("TestNpc", "test-request-id"));
            SetField("_isRequestActive", true);

            // Act
            InvokeConversationCompleted(audioReceived: false);

            // Assert
            Assert.IsNull(GetField<ConversationSession>("_currentSession"),
                "_currentSession must be cleared regardless of whether audio was received");
            Assert.IsFalse(GetField<bool>("_isRequestActive"),
                "_isRequestActive must be cleared regardless of whether audio was received");
        }

        #region Helpers

        private void SetField(string fieldName, object value)
        {
            var field = typeof(RequestOrchestrator).GetField(fieldName, PrivateInstance);
            Assert.IsNotNull(field, $"Field '{fieldName}' not found on RequestOrchestrator");
            field.SetValue(_orchestrator, value);
        }

        private T GetField<T>(string fieldName)
        {
            var field = typeof(RequestOrchestrator).GetField(fieldName, PrivateInstance);
            Assert.IsNotNull(field, $"Field '{fieldName}' not found on RequestOrchestrator");
            return (T)field.GetValue(_orchestrator);
        }

        private void InvokeConversationCompleted(bool audioReceived)
        {
            var method = typeof(RequestOrchestrator).GetMethod("HandleConversationCompleted", PrivateInstance);
            Assert.IsNotNull(method,
                "HandleConversationCompleted method not found on RequestOrchestrator — fix not yet implemented");
            method.Invoke(_orchestrator, new object[] { audioReceived });
        }

        #endregion
    }
}
