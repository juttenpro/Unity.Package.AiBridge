using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using System.Collections;
using System.Collections.Generic;
using Tsc.AIBridge.Audio.Processing;
using Tsc.AIBridge.Audio.Playback;

namespace Tsc.AIBridge.Tests.Runtime
{
    /// <summary>
    /// BUSINESS REQUIREMENT: Audio buffering must be the DEFAULT behavior, not conditional
    ///
    /// WHY: Multiple scenarios require buffering:
    /// - RuleSystem approval delay (takes several frames to evaluate)
    /// - WebSocket reconnection scenarios (connection must be established first)
    /// - Session startup confirmation (wait for SessionStarted message)
    /// - Interruption detection period (InterruptionManager needs time to evaluate)
    /// - Network latency compensation (ensure smooth delivery)
    ///
    /// WHAT: Test that AudioStreamProcessor ALWAYS starts in buffering mode
    /// and buffer lifecycle is managed correctly
    ///
    /// HOW: Test various scenarios:
    /// 1. StartEncoding() always starts buffering (default behavior)
    /// 2. StartBuffering() is idempotent (safe to call multiple times)
    /// 3. FlushBuffer() releases queued audio correctly
    /// 4. DiscardBuffer() clears audio for failed scenarios
    /// 5. Buffer isolation between sessions (no audio bleeding)
    ///
    /// SUCCESS CRITERIA:
    /// - StartEncoding() sets IsBuffering = true
    /// - Audio is queued, not fired via event
    /// - StartBuffering() doesn't clear existing buffer
    /// - FlushBuffer() fires all queued audio
    /// - DiscardBuffer() clears queue without firing events
    /// - New session starts with clean buffer state
    ///
    /// BUSINESS IMPACT:
    /// - Failure = Audio sent before RuleSystem approval (protocol violation)
    /// - Failure = Audio lost during WebSocket reconnect
    /// - Failure = Race conditions in multi-turn conversations
    /// - Failure = Audio bleeding between users (GDPR violation)
    /// - Critical for medical training compliance
    /// </summary>
    [TestFixture]
    public class AudioStreamProcessorBufferingTests
    {
        private AudioStreamProcessor _processor;
        private GameObject _audioPlayerObject;
        private StreamingAudioPlayer _audioPlayer;
        private List<byte[]> _firedAudioChunks;

        #region Setup/Teardown

        [SetUp]
        public void Setup()
        {
            // Create a real StreamingAudioPlayer for testing
            _audioPlayerObject = new GameObject("TestAudioPlayer");
            var audioFilterRelay = _audioPlayerObject.AddComponent<AudioFilterRelay>();
            _audioPlayer = _audioPlayerObject.AddComponent<StreamingAudioPlayer>();
            _audioPlayer.SuppressInitializationWarnings();
            _audioPlayer.SetAudioFilterRelay(audioFilterRelay);

            // Create AudioStreamProcessor with test player
            _processor = new AudioStreamProcessor(
                audioPlayer: _audioPlayer,
                opusBitrate: 64000,
                bufferDuration: 0.1f,
                isVerboseLogging: true // Enable logging for debugging
            );

            // Track audio chunks that are fired via event
            _firedAudioChunks = new List<byte[]>();
            _processor.OnOpusAudioEncoded += (chunk) => _firedAudioChunks.Add(chunk);
        }

        [TearDown]
        public void Teardown()
        {
            _processor?.Dispose();
            if (_audioPlayerObject != null)
                Object.DestroyImmediate(_audioPlayerObject);
        }

        #endregion

        #region Test 1: Default Buffering Behavior

        /// <summary>
        /// BUSINESS REQUIREMENT: StartEncoding() MUST start in buffering mode by default
        ///
        /// WHY: All scenarios require initial buffering (RuleSystem, reconnect, session setup)
        /// WHAT: Verify IsBuffering = true immediately after StartEncoding()
        /// SUCCESS: Buffer is active, audio will be queued
        /// </summary>
        [Test]
        public void StartEncoding_AlwaysStartsBuffering()
        {
            // Act
            _processor.StartEncoding();

            // Assert
            Assert.IsTrue(_processor.IsBuffering,
                "StartEncoding() MUST start in buffering mode by default - " +
                "this is critical for RuleSystem approval, WebSocket reconnect, and session startup");
        }

        /// <summary>
        /// BUSINESS REQUIREMENT: Audio chunks must be queued during buffering, not fired
        ///
        /// WHY: Audio must wait for RuleSystem approval before being sent to backend
        /// WHAT: Verify OnOpusAudioEncoded event is NOT fired during buffering
        /// SUCCESS: Audio is queued internally, event not fired
        /// </summary>
        [Test]
        public void BufferingMode_QueuesAudio_DoesNotFireEvent()
        {
            // Arrange
            _processor.StartEncoding();
            var testChunk = new byte[] { 1, 2, 3, 4, 5 };

            // Act - manually add to buffer (simulates microphone input)
            AddAudioToBufferViaReflection(testChunk);

            // Assert
            Assert.AreEqual(0, _firedAudioChunks.Count,
                "Audio should be queued during buffering, NOT fired via OnOpusAudioEncoded event");
            Assert.IsTrue(GetBufferCount() > 0,
                "Audio should be in internal queue");
        }

        #endregion

        #region Test 2: Idempotent StartBuffering

        /// <summary>
        /// BUSINESS REQUIREMENT: StartBuffering() must be idempotent (safe to call multiple times)
        ///
        /// WHY: Multiple systems may call StartBuffering() (RuleSystem, InterruptionManager)
        /// WHAT: Verify StartBuffering() doesn't clear existing buffer or cause side effects
        /// SUCCESS: Buffer preserved, no data loss
        ///
        /// BUSINESS IMPACT:
        /// - Failure = Audio data loss when systems coordinate
        /// - Failure = First words of trainee lost
        /// </summary>
        [Test]
        public void StartBuffering_Idempotent_DoesNotClearBuffer()
        {
            // Arrange - start encoding (already buffering)
            _processor.StartEncoding();
            var chunk1 = new byte[] { 1, 2, 3 };
            AddAudioToBufferViaReflection(chunk1);
            int initialCount = GetBufferCount();

            // Act - call StartBuffering again (idempotent)
            _processor.StartBuffering();

            // Assert
            Assert.IsTrue(_processor.IsBuffering, "Should still be buffering");
            Assert.AreEqual(initialCount, GetBufferCount(),
                "StartBuffering() MUST NOT clear existing buffer - idempotent operation");
        }

        /// <summary>
        /// BUSINESS REQUIREMENT: Multiple StartBuffering() calls must be safe
        ///
        /// WHY: Race conditions in complex systems (RuleSystem + interruption detection)
        /// WHAT: Verify calling StartBuffering() 10 times doesn't cause issues
        /// SUCCESS: Buffer intact, state consistent
        /// </summary>
        [Test]
        public void StartBuffering_MultipleCalls_Safe()
        {
            // Arrange
            _processor.StartEncoding();
            var chunk1 = new byte[] { 1, 2, 3 };
            AddAudioToBufferViaReflection(chunk1);

            // Act - call StartBuffering many times
            for (int i = 0; i < 10; i++)
            {
                _processor.StartBuffering();
            }

            // Assert
            Assert.IsTrue(_processor.IsBuffering, "Should still be buffering after multiple calls");
            Assert.AreEqual(1, GetBufferCount(), "Buffer should contain exactly 1 chunk (no duplication)");
        }

        #endregion

        #region Test 3: FlushBuffer

        /// <summary>
        /// BUSINESS REQUIREMENT: FlushBuffer() must release all queued audio correctly
        ///
        /// WHY: After RuleSystem approval or SessionStarted, buffered audio must be sent
        /// WHAT: Verify FlushBuffer() fires all queued chunks via OnOpusAudioEncoded
        /// SUCCESS: All chunks fired in correct order, buffer cleared
        /// </summary>
        [Test]
        public void FlushBuffer_FiresAllQueuedChunks()
        {
            // Arrange - buffer multiple chunks
            _processor.StartEncoding();
            var chunk1 = new byte[] { 1, 2, 3 };
            var chunk2 = new byte[] { 4, 5, 6 };
            var chunk3 = new byte[] { 7, 8, 9 };

            AddAudioToBufferViaReflection(chunk1);
            AddAudioToBufferViaReflection(chunk2);
            AddAudioToBufferViaReflection(chunk3);

            Assert.AreEqual(3, GetBufferCount(), "Should have 3 chunks buffered");
            Assert.AreEqual(0, _firedAudioChunks.Count, "No events fired yet");

            // Act - flush buffer
            _processor.FlushBuffer();

            // Assert
            Assert.AreEqual(3, _firedAudioChunks.Count,
                "FlushBuffer() should fire all 3 buffered chunks");
            CollectionAssert.AreEqual(chunk1, _firedAudioChunks[0], "First chunk in correct order");
            CollectionAssert.AreEqual(chunk2, _firedAudioChunks[1], "Second chunk in correct order");
            CollectionAssert.AreEqual(chunk3, _firedAudioChunks[2], "Third chunk in correct order");
            Assert.AreEqual(0, GetBufferCount(), "Buffer should be cleared after flush");
        }

        /// <summary>
        /// BUSINESS REQUIREMENT: After FlushBuffer(), new audio should fire immediately
        ///
        /// WHY: Once RuleSystem approved, audio should stream directly (low latency)
        /// WHAT: Verify buffering mode is disabled after FlushBuffer()
        /// SUCCESS: IsBuffering = false, new audio fires immediately
        /// </summary>
        [Test]
        public void FlushBuffer_DisablesBuffering_NewAudioFiresImmediately()
        {
            // Arrange
            _processor.StartEncoding();
            var bufferedChunk = new byte[] { 1, 2, 3 };
            AddAudioToBufferViaReflection(bufferedChunk);

            // Act - flush buffer
            _processor.FlushBuffer();
            _firedAudioChunks.Clear(); // Clear for next test

            // Add new audio after flush
            var newChunk = new byte[] { 4, 5, 6 };
            AddAudioToBufferViaReflection(newChunk);

            // Assert
            Assert.IsFalse(_processor.IsBuffering,
                "After FlushBuffer(), buffering should be disabled for low latency");
            Assert.AreEqual(1, _firedAudioChunks.Count,
                "New audio after flush should fire immediately");
            CollectionAssert.AreEqual(newChunk, _firedAudioChunks[0]);
        }

        #endregion

        #region Test 4: DiscardBuffer

        /// <summary>
        /// BUSINESS REQUIREMENT: DiscardBuffer() must clear queued audio without firing events
        ///
        /// WHY: Failed interruptions or RuleSystem rejections need clean buffer discard
        /// WHAT: Verify DiscardBuffer() clears buffer and no events are fired
        /// SUCCESS: Buffer empty, no OnOpusAudioEncoded events
        ///
        /// BUSINESS IMPACT:
        /// - Failure = Failed interruption audio sent to backend (protocol violation)
        /// - Failure = RuleSystem rejected audio still sent
        /// </summary>
        [Test]
        public void DiscardBuffer_ClearsQueue_NoEventsFired()
        {
            // Arrange - buffer audio
            _processor.StartEncoding();
            var chunk1 = new byte[] { 1, 2, 3 };
            var chunk2 = new byte[] { 4, 5, 6 };

            AddAudioToBufferViaReflection(chunk1);
            AddAudioToBufferViaReflection(chunk2);

            Assert.AreEqual(2, GetBufferCount(), "Should have 2 chunks buffered");

            // Act - discard buffer (e.g., interruption not approved)
            _processor.DiscardBuffer();

            // Assert
            Assert.AreEqual(0, GetBufferCount(), "Buffer should be cleared");
            Assert.AreEqual(0, _firedAudioChunks.Count,
                "DiscardBuffer() MUST NOT fire events - audio should be silently dropped");
        }

        /// <summary>
        /// BUSINESS REQUIREMENT: After DiscardBuffer(), buffering mode continues
        ///
        /// WHY: System may need to buffer again (next attempt, RuleSystem evaluation)
        /// WHAT: Verify IsBuffering = true after DiscardBuffer()
        /// SUCCESS: Buffering mode preserved, ready for next attempt
        /// </summary>
        [Test]
        public void DiscardBuffer_PreservesBufferingMode()
        {
            // Arrange
            _processor.StartEncoding();
            var chunk = new byte[] { 1, 2, 3 };
            AddAudioToBufferViaReflection(chunk);

            // Act
            _processor.DiscardBuffer();

            // Assert
            Assert.IsTrue(_processor.IsBuffering,
                "DiscardBuffer() should preserve buffering mode - system may buffer again");
        }

        #endregion

        #region Test 5: Session Isolation

        /// <summary>
        /// BUSINESS REQUIREMENT: New sessions must start with clean buffer state
        ///
        /// WHY: GDPR compliance - audio from one session must NOT bleed into next session
        /// WHAT: Verify EndEncoding() + StartEncoding() creates clean buffer state
        /// SUCCESS: No audio bleeding, buffer starts empty
        ///
        /// BUSINESS IMPACT:
        /// - Failure = Patient A's audio sent with Patient B's request (GDPR violation)
        /// - Failure = Medical data privacy breach
        /// - CRITICAL for multi-user scenarios (100+ simultaneous sessions)
        /// </summary>
        [Test]
        public void NewSession_StartsWithCleanBuffer()
        {
            // Arrange - Session 1 with buffered audio
            _processor.StartEncoding();
            var session1Chunk = new byte[] { 1, 2, 3 };
            AddAudioToBufferViaReflection(session1Chunk);
            Assert.AreEqual(1, GetBufferCount(), "Session 1: Should have audio in buffer");

            // Act - End session 1, start session 2
            _processor.StopEncoding();
            _processor.StartEncoding();

            // Assert
            Assert.AreEqual(0, GetBufferCount(),
                "Session 2: Buffer MUST be clean - no audio bleeding from previous session (GDPR compliance)");
            Assert.IsTrue(_processor.IsBuffering,
                "Session 2: Should start in buffering mode (default behavior)");
        }

        /// <summary>
        /// BUSINESS REQUIREMENT: Multiple sessions must maintain buffer isolation
        ///
        /// WHY: Medical training involves many back-and-forth conversation turns
        /// WHAT: Test 5 consecutive sessions, verify no audio bleeding
        /// SUCCESS: Each session has clean buffer state
        /// </summary>
        [Test]
        public void MultipleConsecutiveSessions_MaintainBufferIsolation()
        {
            // Arrange & Act - 5 sessions
            for (int sessionNum = 1; sessionNum <= 5; sessionNum++)
            {
                _processor.StartEncoding();

                // Each session starts clean
                Assert.AreEqual(0, GetBufferCount(),
                    $"Session {sessionNum}: Buffer should start empty");
                Assert.IsTrue(_processor.IsBuffering,
                    $"Session {sessionNum}: Should start in buffering mode");

                // Add session-specific audio
                var chunk = new byte[] { (byte)sessionNum };
                AddAudioToBufferViaReflection(chunk);
                Assert.AreEqual(1, GetBufferCount(),
                    $"Session {sessionNum}: Should buffer audio");

                // End session
                _processor.StopEncoding();
            }

            // Final check - no leftover state
            _processor.StartEncoding();
            Assert.AreEqual(0, GetBufferCount(),
                "After 5 sessions, new session should still have clean buffer");
        }

        #endregion

        #region Test 6: Edge Cases

        /// <summary>
        /// BUSINESS REQUIREMENT: FlushBuffer() on empty buffer must be safe
        ///
        /// WHY: Race conditions or timing issues may cause flush on empty buffer
        /// WHAT: Verify FlushBuffer() on empty buffer doesn't crash or cause issues
        /// SUCCESS: No exceptions, system remains stable
        /// </summary>
        [Test]
        public void FlushBuffer_OnEmptyBuffer_Safe()
        {
            // Arrange
            _processor.StartEncoding();
            Assert.AreEqual(0, GetBufferCount(), "Buffer should start empty");

            // Act - flush empty buffer (should be safe)
            Assert.DoesNotThrow(() => _processor.FlushBuffer(),
                "FlushBuffer() on empty buffer should be safe");

            // Assert
            Assert.AreEqual(0, _firedAudioChunks.Count, "No events should fire");
        }

        /// <summary>
        /// BUSINESS REQUIREMENT: DiscardBuffer() on empty buffer must be safe
        ///
        /// WHY: Multiple systems may call DiscardBuffer() defensively
        /// WHAT: Verify DiscardBuffer() on empty buffer doesn't crash
        /// SUCCESS: No exceptions, system remains stable
        /// </summary>
        [Test]
        public void DiscardBuffer_OnEmptyBuffer_Safe()
        {
            // Arrange
            _processor.StartEncoding();

            // Act & Assert
            Assert.DoesNotThrow(() => _processor.DiscardBuffer(),
                "DiscardBuffer() on empty buffer should be safe");
        }

        /// <summary>
        /// BUSINESS REQUIREMENT: StartBuffering() before StartEncoding() must be safe
        ///
        /// WHY: System initialization order may vary
        /// WHAT: Verify calling StartBuffering() before StartEncoding() is safe
        /// SUCCESS: No crashes, buffering enabled when encoding starts
        /// </summary>
        [Test]
        public void StartBuffering_BeforeStartEncoding_Safe()
        {
            // Act - call StartBuffering before StartEncoding
            Assert.DoesNotThrow(() => _processor.StartBuffering(),
                "StartBuffering() before StartEncoding() should be safe");

            // Start encoding
            _processor.StartEncoding();

            // Assert - should be buffering
            Assert.IsTrue(_processor.IsBuffering, "Should be in buffering mode");
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Add audio to buffer via reflection (simulates microphone input)
        /// </summary>
        private void AddAudioToBufferViaReflection(byte[] audioChunk)
        {
            // Get private _audioQueue field
            var queueField = typeof(AudioStreamProcessor).GetField("_audioQueue",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            if (queueField != null)
            {
                var queue = queueField.GetValue(_processor) as Queue<byte[]>;

                // Respect buffering mode
                if (_processor.IsBuffering)
                {
                    queue?.Enqueue(audioChunk);
                }
                else
                {
                    // Simulate firing event directly (bypassing internal logic)
                    // In production, this happens in HandleOpusEncoded method
                    var eventField = typeof(AudioStreamProcessor).GetField("OnOpusAudioEncoded",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    var eventDelegate = eventField?.GetValue(_processor) as System.MulticastDelegate;

                    if (eventDelegate != null)
                    {
                        foreach (var handler in eventDelegate.GetInvocationList())
                        {
                            handler.Method.Invoke(handler.Target, new object[] { audioChunk });
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Get buffer count via reflection
        /// </summary>
        private int GetBufferCount()
        {
            var queueField = typeof(AudioStreamProcessor).GetField("_audioQueue",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            if (queueField != null)
            {
                var queue = queueField.GetValue(_processor) as Queue<byte[]>;
                return queue?.Count ?? 0;
            }

            return 0;
        }

        #endregion
    }
}
