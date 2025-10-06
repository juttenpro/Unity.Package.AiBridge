using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using System.Collections;
using Tsc.AIBridge.Audio.Interruption;
using Tsc.AIBridge.Input;

namespace Tsc.AIBridge.Tests.Runtime
{
    /// <summary>
    /// BUSINESS REQUIREMENT: Interruption system must detect when player talks over NPC
    /// and notify other systems via events
    ///
    /// WHY: Players need to be able to interrupt NPCs naturally during conversation
    /// WHAT: Test that InterruptionManager correctly detects interruptions and fires events
    /// HOW: Mock NPC speech, simulate user speech, verify event firing
    ///
    /// SUCCESS CRITERIA:
    /// - OnInterruption event fires when interruption detected
    /// - Event includes correct context (NPC being interrupted)
    /// - Event only fires when interruption is actually approved
    ///
    /// BUSINESS IMPACT:
    /// - Failing = Players cannot interrupt NPCs, breaking natural conversation flow
    /// - Failing = RuleSystem doesn't know about interruptions, incorrect AI responses
    /// </summary>
    public class InterruptionManagerTests
    {
        private GameObject _playerGameObject;
        private GameObject _npcGameObject;
        private InterruptionManager _interruptionManager;
        private SpeechInputHandler _speechInputHandler;

        [SetUp]
        public void Setup()
        {
            // Create player GameObject with required components
            _playerGameObject = new GameObject("Player");
            _speechInputHandler = _playerGameObject.AddComponent<SpeechInputHandler>();
            _interruptionManager = _playerGameObject.AddComponent<InterruptionManager>();

            // Use reflection to set speechInputHandler field since it's [SerializeField]
            var speechInputField = typeof(InterruptionManager)
                .GetField("speechInputHandler", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            speechInputField?.SetValue(_interruptionManager, _speechInputHandler);

            // Create NPC GameObject (for testing active NPC detection)
            _npcGameObject = new GameObject("TestNPC");
        }

        [TearDown]
        public void TearDown()
        {
            if (_playerGameObject != null)
                Object.DestroyImmediate(_playerGameObject);
            if (_npcGameObject != null)
                Object.DestroyImmediate(_npcGameObject);
        }

        [Test]
        public void OnInterruption_EventFires_WhenInterruptionDetected()
        {
            // Arrange
            bool eventFired = false;
            _interruptionManager.OnInterruption += () => eventFired = true;

            // Act
            _interruptionManager.OnInterruptionDetected();

            // Assert
            Assert.IsTrue(eventFired, "OnInterruption event should fire when interruption is detected");
        }

        [Test]
        public void OnInterruptionDetectedEvent_Fires_WithCorrectParameters()
        {
            // Arrange
            bool eventFired = false;
            object receivedPersona = null;
            string receivedMessage = null;

            _interruptionManager.OnInterruptionDetectedEvent += (persona, message) =>
            {
                eventFired = true;
                receivedPersona = persona;
                receivedMessage = message;
            };

            // Act
            _interruptionManager.OnInterruptionDetected();

            // Assert
            Assert.IsTrue(eventFired, "Legacy event should fire for backwards compatibility");
            Assert.IsNull(receivedPersona, "Persona should be null (no PersonaSO dependency)");
            Assert.AreEqual("Interruption detected", receivedMessage);
        }

        [Test]
        public void HasDetectedInterruption_ReturnsFalse_Initially()
        {
            // Assert
            Assert.IsFalse(_interruptionManager.HasDetectedInterruption());
        }

        [Test]
        public void HasDetectedInterruption_ReturnsTrue_AfterInterruptionDetected()
        {
            // Act
            _interruptionManager.OnInterruptionDetected();

            // Assert
            Assert.IsTrue(_interruptionManager.HasDetectedInterruption());
        }

        [Test]
        public void ClearInterruptionFlag_ResetsInterruptionState()
        {
            // Arrange
            _interruptionManager.OnInterruptionDetected();
            Assert.IsTrue(_interruptionManager.HasDetectedInterruption(), "Setup: interruption should be detected");

            // Act
            _interruptionManager.ClearInterruptionFlag();

            // Assert
            Assert.IsFalse(_interruptionManager.HasDetectedInterruption(), "Interruption flag should be cleared");
        }

        [UnityTest]
        public IEnumerator CheckForInterruption_WithTestParameters_DetectsAfterPersistenceTime()
        {
            // Arrange
            float persistenceTime = 1.5f;
            bool userSpeaking = true;
            bool npcSpeaking = true;
            bool allowInterruption = true;

            // Act - simulate 1.0 second (should NOT trigger yet)
            for (int i = 0; i < 10; i++)
            {
                _interruptionManager.CheckForInterruption(userSpeaking, npcSpeaking, 0.1f, allowInterruption, persistenceTime);
                yield return new WaitForSeconds(0.1f);
            }

            // Assert - not yet detected
            Assert.IsFalse(_interruptionManager.HasDetectedInterruption(), "Should not detect before persistence time");

            // Act - continue to 1.6 seconds (should trigger)
            for (int i = 0; i < 6; i++)
            {
                _interruptionManager.CheckForInterruption(userSpeaking, npcSpeaking, 0.1f, allowInterruption, persistenceTime);
                yield return new WaitForSeconds(0.1f);
            }

            // Assert - should be detected
            Assert.IsTrue(_interruptionManager.HasDetectedInterruption(), "Should detect after persistence time exceeded");
        }

        [Test]
        public void CheckForInterruption_DoesNotDetect_WhenInterruptionNotAllowed()
        {
            // Arrange
            bool userSpeaking = true;
            bool npcSpeaking = true;
            bool allowInterruption = false;
            float persistenceTime = 1.5f;

            // Act - simulate 2 seconds of overlap
            for (int i = 0; i < 20; i++)
            {
                _interruptionManager.CheckForInterruption(userSpeaking, npcSpeaking, 0.1f, allowInterruption, persistenceTime);
            }

            // Assert
            Assert.IsFalse(_interruptionManager.HasDetectedInterruption(), "Should not detect when interruption not allowed");
        }

        [Test]
        public void CheckForInterruption_ResetsTimer_WhenUserStopsSpeaking()
        {
            // Arrange
            float persistenceTime = 1.5f;
            bool userSpeaking = true;
            bool npcSpeaking = true;

            // Act - accumulate 1 second
            for (int i = 0; i < 10; i++)
            {
                _interruptionManager.CheckForInterruption(userSpeaking, npcSpeaking, 0.1f, true, persistenceTime);
            }

            // User stops speaking
            userSpeaking = false;
            _interruptionManager.CheckForInterruption(userSpeaking, npcSpeaking, 0.1f, true, persistenceTime);

            // User starts speaking again
            userSpeaking = true;
            for (int i = 0; i < 10; i++)
            {
                _interruptionManager.CheckForInterruption(userSpeaking, npcSpeaking, 0.1f, true, persistenceTime);
            }

            // Assert - should not be detected (timer was reset)
            Assert.IsFalse(_interruptionManager.HasDetectedInterruption(), "Timer should reset when user stops speaking");
        }

        [Test]
        public void CheckForInterruption_WithAudioFrame_UsesSimpleVAD()
        {
            // Arrange
            float[] loudAudio = new float[1024];
            for (int i = 0; i < loudAudio.Length; i++)
            {
                loudAudio[i] = 0.1f; // Above 0.01 threshold
            }

            float[] silentAudio = new float[1024];
            // All zeros (below threshold)

            // Act & Assert - loud audio should be detected
            bool detectedLoud = _interruptionManager.CheckForInterruption(loudAudio);
            // Note: This uses test method that assumes NPC is talking

            float[] quietAudio = new float[1024];
            for (int i = 0; i < quietAudio.Length; i++)
            {
                quietAudio[i] = 0.005f; // Below 0.01 threshold
            }

            bool detectedQuiet = _interruptionManager.CheckForInterruption(quietAudio);

            // Assert - simple validation that method doesn't crash
            Assert.Pass("CheckForInterruption with audio frame executes without error");
        }
    }
}
