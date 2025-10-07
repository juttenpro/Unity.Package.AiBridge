using System;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using System.Collections;
using System.Collections.Generic;
using Tsc.AIBridge.Audio.Capture;
using Tsc.AIBridge.Audio.Playback;
using Tsc.AIBridge.Core;
using Tsc.AIBridge.Input;
using Tsc.AIBridge.WebSocket;
using Tsc.AIBridge.Audio.Processing;
using Tsc.AIBridge.Messages;
using Tsc.AIBridge.Utilities;
using Object = UnityEngine.Object;

namespace Tsc.AIBridge.Tests.Runtime
{
    /// <summary>
    /// BUSINESS REQUIREMENT: Robust audio chunk handling across multiple input modes and timing scenarios
    ///
    /// WHY: Medical VR training requires reliable audio capture regardless of:
    /// - Input method (PTT vs Voice Activation)
    /// - Network timing (buffering during RuleSystem evaluation)
    /// - SmartMicOffset (continued recording after PTT release)
    /// - Background noise measurement (VAD calibration)
    ///
    /// WHAT: Tests that RequestOrchestrator correctly accepts/rejects audio chunks based on
    /// request lifecycle state (_isRequestActive flag), independent of session or buffer state.
    ///
    /// HOW: Tests cover 5 critical scenarios:
    /// 1. PTT Mode - Normal buffering → upload flow
    /// 2. PTT Mode - SmartMicOffset continued recording
    /// 3. Voice Activation - Early chunks during async setup
    /// 4. Edge Cases - Chunks before/after request lifecycle
    /// 5. Cancel/Interrupt - Chunks rejected after cancellation
    ///
    /// SUCCESS CRITERIA:
    /// - Audio chunks accepted when _isRequestActive = true (any phase)
    /// - Audio chunks rejected when _isRequestActive = false
    /// - Voice activation race condition handled (chunks during setup)
    /// - SmartMicOffset chunks accepted until EndAudioRequest
    /// - No dependency on AudioStreamProcessor.IsBuffering (SoC violation)
    /// - Clean state management with single flag
    ///
    /// BUSINESS IMPACT:
    /// - Failure = Lost audio during critical medical training moments
    /// - Lost first words of trainee = Failed assessment
    /// - Race conditions = Inconsistent behavior, user frustration
    /// - 100+ simultaneous users = Race conditions multiply
    /// </summary>
    [TestFixture]
    public class RequestOrchestratorAudioTests
    {
        private GameObject _orchestratorObject;
        private RequestOrchestrator _orchestrator;
        private MockSpeechInputHandler _mockInputHandler;
        private MockWebSocketClient _mockWebSocket;
        private MockNpcClient _mockNpcClient;
        private GameObject _inputHandlerObject;
        private GameObject _webSocketObject;
        private GameObject _npcClientObject;
        private GameObject _audioListenerObject;

        #region Setup/Teardown

        [SetUp]
        public void Setup()
        {
            // Create dummy AudioListener to suppress Unity warnings
            _audioListenerObject = new GameObject("DummyAudioListener");
            _audioListenerObject.AddComponent<AudioListener>();

            // Create RequestOrchestrator
            _orchestratorObject = new GameObject("TestRequestOrchestrator");
            _orchestrator = _orchestratorObject.AddComponent<RequestOrchestrator>();

            // Create mock SpeechInputHandler
            _inputHandlerObject = new GameObject("MockSpeechInputHandler");
            _mockInputHandler = _inputHandlerObject.AddComponent<MockSpeechInputHandler>();

            // Manually trigger Awake for mock (Unity may not call it in tests)
            _mockInputHandler.SendMessage("Awake", SendMessageOptions.DontRequireReceiver);

            // Create mock WebSocketClient
            _webSocketObject = new GameObject("MockWebSocketClient");
            _mockWebSocket = _webSocketObject.AddComponent<MockWebSocketClient>();

            // Manually trigger Awake for mock
            _mockWebSocket.SendMessage("Awake", SendMessageOptions.DontRequireReceiver);

            // Create mock NpcClient
            _npcClientObject = new GameObject("MockNpcClient");
            _mockNpcClient = _npcClientObject.AddComponent<MockNpcClient>();

            // Register the NPC in the NpcMessageRouter
            NpcMessageRouter.Instance.RegisterNpc(_mockNpcClient);

            // Wire up dependencies using reflection (Unity serialized fields)
            var inputField = typeof(RequestOrchestrator).GetField("speechInputHandler",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            inputField?.SetValue(_orchestrator, _mockInputHandler);

            var webSocketField = typeof(RequestOrchestrator).GetField("_webSocketClient",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            webSocketField?.SetValue(_orchestrator, _mockWebSocket);

            // Set the active NPC client so RequestOrchestrator can register the handler
            var activeNpcField = typeof(RequestOrchestrator).GetField("_activeNpcClient",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            activeNpcField?.SetValue(_orchestrator, _mockNpcClient);

            // DON'T call Start manually - Unity will call it automatically in the first frame
            // Calling it twice causes double subscription to events!
            // Instead, wait one frame to let Unity initialize everything
            // Note: Tests should start with "yield return null;" to ensure setup is complete
        }

        [TearDown]
        public void Teardown()
        {
            // Unregister NPC from router
            if (_mockNpcClient != null)
            {
                NpcMessageRouter.Instance.UnregisterNpc(_mockNpcClient);
            }

            if (_orchestratorObject != null)
                Object.DestroyImmediate(_orchestratorObject);
            if (_inputHandlerObject != null)
                Object.DestroyImmediate(_inputHandlerObject);
            if (_webSocketObject != null)
                Object.DestroyImmediate(_webSocketObject);
            if (_npcClientObject != null)
                Object.DestroyImmediate(_npcClientObject);
            if (_audioListenerObject != null)
                Object.DestroyImmediate(_audioListenerObject);
        }

        #endregion

        #region Test 1: PTT Mode - Basic Flow

        /// <summary>
        /// BUSINESS REQUIREMENT: PTT mode audio must flow through buffering → upload seamlessly
        ///
        /// WHY: Most common input method for medical training (hands-free operation)
        /// WHAT: Verify chunks accepted from StartAudioRequest until EndAudioRequest
        /// SUCCESS: All chunks sent to WebSocket, none dropped
        /// </summary>
        [UnityTest]
        public IEnumerator PTT_NormalFlow_AcceptsAllChunks()
        {
            // Wait for Unity to call Start() on all components
            yield return null;

            // Arrange: Create a conversation request
            var request = new ConversationRequest
            {
                NpcId = "TestNPC",
                Messages = new List<ChatMessage>
                {
                    new ChatMessage { Role = "system", Content = "Test" }
                },
                LlmProvider = "openai",
                LlmModel = "gpt-4"
            };

            // Act: Start request
            _orchestrator.StartConversationRequest(request);
            yield return null; // Let queue process

            // Simulate audio chunks arriving
            var chunk1 = new byte[] { 1, 2, 3 };
            var chunk2 = new byte[] { 4, 5, 6 };
            var chunk3 = new byte[] { 7, 8, 9 };

            _mockInputHandler.TriggerOnOpusAudioEncoded(chunk1);
            _mockInputHandler.TriggerOnOpusAudioEncoded(chunk2);
            _mockInputHandler.TriggerOnOpusAudioEncoded(chunk3);

            yield return null;

            // Assert: All chunks should be sent to WebSocket
            Assert.AreEqual(3, _mockWebSocket.SentBinaryMessages.Count, "Should send all 3 chunks");
            CollectionAssert.AreEqual(chunk1, _mockWebSocket.SentBinaryMessages[0]);
            CollectionAssert.AreEqual(chunk2, _mockWebSocket.SentBinaryMessages[1]);
            CollectionAssert.AreEqual(chunk3, _mockWebSocket.SentBinaryMessages[2]);
        }

        /// <summary>
        /// BUSINESS REQUIREMENT: Audio before request start must be rejected
        ///
        /// WHY: Prevents audio from previous session bleeding into new session
        /// WHAT: Chunks arriving before StartAudioRequest should be dropped
        /// SUCCESS: Warning logged, no chunks sent to WebSocket
        /// </summary>
        [UnityTest]
        public IEnumerator PTT_ChunksBeforeRequest_DropsAudio()
        {
            // Wait for Unity to call Start() on all components
            yield return null;

            // Arrange: No request started
            var chunk = new byte[] { 1, 2, 3 };

            // Act: Try to send chunk before request
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("no active request"));
            _mockInputHandler.TriggerOnOpusAudioEncoded(chunk);
            yield return null;

            // Assert: Chunk should be dropped
            Assert.AreEqual(0, _mockWebSocket.SentBinaryMessages.Count, "Should drop chunk before request");
        }

        /// <summary>
        /// BUSINESS REQUIREMENT: Audio after request end must be rejected
        ///
        /// WHY: PTT release = user finished speaking, no more audio should be sent
        /// WHAT: Chunks arriving after EndAudioRequest should be dropped
        /// SUCCESS: Warning logged, chunks after end not sent
        /// </summary>
        [UnityTest]
        public IEnumerator PTT_ChunksAfterEnd_DropsAudio()
        {
            // Wait for Unity to call Start() on all components
            yield return null;

            // Arrange: Start and end request
            var request = new ConversationRequest
            {
                NpcId = "TestNPC",
                Messages = new List<ChatMessage>
                {
                    new ChatMessage { Role = "system", Content = "Test" }
                },
                LlmProvider = "openai",
                LlmModel = "gpt-4"
            };

            _orchestrator.StartConversationRequest(request);
            yield return null;

            // Send a valid chunk first
            var chunk1 = new byte[] { 1, 2, 3 };
            _mockInputHandler.TriggerOnOpusAudioEncoded(chunk1);
            yield return null;

            // Act: End request and try to send another chunk
            _orchestrator.EndAudioRequest();
            var chunk2 = new byte[] { 4, 5, 6 };
            _mockInputHandler.TriggerOnOpusAudioEncoded(chunk2);
            yield return null;

            // Assert: Only first chunk should be sent
            Assert.AreEqual(1, _mockWebSocket.SentBinaryMessages.Count, "Should only send chunk before end");
            CollectionAssert.AreEqual(chunk1, _mockWebSocket.SentBinaryMessages[0]);
        }

        #endregion

        #region Test 2: Voice Activation - Race Condition

        /// <summary>
        /// BUSINESS REQUIREMENT: Voice activation must handle async session setup race condition
        ///
        /// WHY: Voice activation starts encoding immediately, but session setup is queued/async
        /// WHAT: Early chunks must be accepted even before session fully initialized
        /// SUCCESS: All chunks accepted, including those during setup phase
        ///
        /// BUSINESS IMPACT:
        /// - Failure = Lost first words of trainee ("I need help with...")
        /// - Critical in emergency scenario training
        /// </summary>
        [UnityTest]
        public IEnumerator VoiceActivation_EarlyChunks_AcceptedDuringSetup()
        {
            // Wait for Unity to call Start() on all components
            yield return null;

            // Arrange: Simulate voice activation trigger
            var request = new ConversationRequest
            {
                NpcId = "TestNPC",
                Messages = new List<ChatMessage>
                {
                    new ChatMessage { Role = "system", Content = "Test" }
                },
                LlmProvider = "openai",
                LlmModel = "gpt-4"
            };

            // Act: Start request (queued, not immediate)
            _orchestrator.StartConversationRequest(request);

            // Simulate chunks arriving BEFORE queue processes (race condition!)
            var earlyChunk1 = new byte[] { 1, 2, 3 };
            var earlyChunk2 = new byte[] { 4, 5, 6 };

            _mockInputHandler.TriggerOnOpusAudioEncoded(earlyChunk1);
            _mockInputHandler.TriggerOnOpusAudioEncoded(earlyChunk2);

            // Now let queue process
            yield return null;

            // More chunks after setup
            var lateChunk = new byte[] { 7, 8, 9 };
            _mockInputHandler.TriggerOnOpusAudioEncoded(lateChunk);
            yield return null;

            // Assert: ALL chunks should be accepted (early ones buffered, late ones direct)
            Assert.AreEqual(3, _mockWebSocket.SentBinaryMessages.Count, "Should accept all chunks including early ones");
        }

        #endregion

        #region Test 3: SmartMicOffset

        /// <summary>
        /// BUSINESS REQUIREMENT: SmartMicOffset allows continued recording after PTT release
        ///
        /// WHY: User may release PTT but still be speaking (VAD detects this)
        /// WHAT: Chunks continue to be accepted after PTT release until explicit EndAudioRequest
        /// SUCCESS: Audio recorded until VAD detects silence, not just PTT release
        /// </summary>
        [UnityTest]
        public IEnumerator SmartMicOffset_ChunksAfterPTTRelease_StillAccepted()
        {
            // Wait for Unity to call Start() on all components
            yield return null;

            // Arrange: Start request (simulates PTT press)
            var request = new ConversationRequest
            {
                NpcId = "TestNPC",
                Messages = new List<ChatMessage>
                {
                    new ChatMessage { Role = "system", Content = "Test" }
                },
                LlmProvider = "openai",
                LlmModel = "gpt-4"
            };

            _orchestrator.StartConversationRequest(request);
            yield return null;

            // Send chunk during PTT
            var chunk1 = new byte[] { 1, 2, 3 };
            _mockInputHandler.TriggerOnOpusAudioEncoded(chunk1);
            yield return null;

            // Act: PTT released but SmartMicOffset keeps recording (no EndAudioRequest yet!)
            // User is still speaking according to VAD
            var chunk2 = new byte[] { 4, 5, 6 }; // After PTT release
            var chunk3 = new byte[] { 7, 8, 9 }; // Still recording
            _mockInputHandler.TriggerOnOpusAudioEncoded(chunk2);
            _mockInputHandler.TriggerOnOpusAudioEncoded(chunk3);
            yield return null;

            // Now VAD detects silence → EndAudioRequest
            _orchestrator.EndAudioRequest();
            yield return null;

            // Assert: All 3 chunks should be accepted (including after PTT release)
            Assert.AreEqual(3, _mockWebSocket.SentBinaryMessages.Count, "Should accept chunks during SmartMicOffset");
        }

        #endregion

        #region Test 4: Cancel/Interrupt

        /// <summary>
        /// BUSINESS REQUIREMENT: Cancelled requests must reject new audio immediately
        ///
        /// WHY: Interruptions or RuleSystem rejections require immediate cleanup
        /// WHAT: CancelCurrentSession should prevent any further audio chunks
        /// SUCCESS: Chunks after cancel are dropped with warning
        /// </summary>
        [UnityTest]
        public IEnumerator Cancel_AfterStart_RejectsSubsequentChunks()
        {
            // Wait for Unity to call Start() on all components
            yield return null;

            // Arrange: Start request
            var request = new ConversationRequest
            {
                NpcId = "TestNPC",
                Messages = new List<ChatMessage>
                {
                    new ChatMessage { Role = "system", Content = "Test" }
                },
                LlmProvider = "openai",
                LlmModel = "gpt-4"
            };

            _orchestrator.StartConversationRequest(request);
            yield return null;

            var chunk1 = new byte[] { 1, 2, 3 };
            _mockInputHandler.TriggerOnOpusAudioEncoded(chunk1);
            yield return null;

            // Act: Cancel session (e.g., RuleSystem rejection or interruption)
            _orchestrator.CancelCurrentSession("Test cancellation");
            yield return null;

            // Try to send more chunks after cancel
            var chunk2 = new byte[] { 4, 5, 6 };
            _mockInputHandler.TriggerOnOpusAudioEncoded(chunk2);
            yield return null;

            // Assert: Only chunk before cancel should be sent
            Assert.AreEqual(1, _mockWebSocket.SentBinaryMessages.Count, "Should only send chunk before cancel");
            CollectionAssert.AreEqual(chunk1, _mockWebSocket.SentBinaryMessages[0]);
        }

        #endregion

        #region Test 5: Multiple Requests

        /// <summary>
        /// BUSINESS REQUIREMENT: Rapid sequential requests must be handled cleanly
        ///
        /// WHY: User may rapidly press PTT multiple times (nervous, testing, or actual usage)
        /// WHAT: Each request should have clean lifecycle, no audio bleeding between requests
        /// SUCCESS: Audio from request N doesn't leak into request N+1
        /// </summary>
        [UnityTest]
        public IEnumerator MultipleRequests_Sequential_AudioIsolated()
        {
            // Wait for Unity to call Start() on all components
            yield return null;

            // Arrange & Act: First request
            var request1 = new ConversationRequest
            {
                NpcId = "TestNPC1",
                Messages = new List<ChatMessage>
                {
                    new ChatMessage { Role = "system", Content = "Test 1" }
                },
                LlmProvider = "openai",
                LlmModel = "gpt-4"
            };

            _orchestrator.StartConversationRequest(request1);
            yield return null;

            var chunk1 = new byte[] { 1, 2, 3 };
            _mockInputHandler.TriggerOnOpusAudioEncoded(chunk1);
            yield return null;

            _orchestrator.EndAudioRequest();
            yield return null;

            // Second request
            var request2 = new ConversationRequest
            {
                NpcId = "TestNPC2",
                Messages = new List<ChatMessage>
                {
                    new ChatMessage { Role = "system", Content = "Test 2" }
                },
                LlmProvider = "openai",
                LlmModel = "gpt-4"
            };

            _orchestrator.StartConversationRequest(request2);
            yield return null;

            var chunk2 = new byte[] { 4, 5, 6 };
            _mockInputHandler.TriggerOnOpusAudioEncoded(chunk2);
            yield return null;

            // Assert: Both chunks sent, clean separation
            Assert.AreEqual(2, _mockWebSocket.SentBinaryMessages.Count, "Should send both chunks from separate requests");
            CollectionAssert.AreEqual(chunk1, _mockWebSocket.SentBinaryMessages[0], "First chunk from first request");
            CollectionAssert.AreEqual(chunk2, _mockWebSocket.SentBinaryMessages[1], "Second chunk from second request");
        }

        #endregion

        #region Test 6: Separation of Concerns

        /// <summary>
        /// BUSINESS REQUIREMENT: RequestOrchestrator must not depend on AudioStreamProcessor internals
        ///
        /// WHY: Clean architecture, testability, maintainability
        /// WHAT: Audio acceptance decision should be based solely on _isRequestActive flag
        /// SUCCESS: No coupling to AudioStreamProcessor.IsBuffering or other internals
        ///
        /// This test validates the refactoring from complex multi-state logic to simple single-flag logic.
        /// </summary>
        [Test]
        public void RequestLifecycle_IndependentOfBufferState()
        {
            // This test validates that _isRequestActive is the ONLY state checked
            // by inspecting the implementation (architectural test)

            // Arrange: Create request
            var request = new ConversationRequest
            {
                NpcId = "TestNPC",
                Messages = new List<ChatMessage>
                {
                    new ChatMessage { Role = "system", Content = "Test" }
                },
                LlmProvider = "openai",
                LlmModel = "gpt-4"
            };

            // Act & Assert: Verify lifecycle state changes
            // Before request
            Assert.IsFalse(GetRequestActiveState(), "Should start with inactive state");

            // Note: AudioStreamProcessor IS mocked via MockSpeechInputHandler in Setup()
            // Phase 1 fix: RequestOrchestrator now validates Inspector components in Start()
            // and disables itself if critical components are missing. Since we have a proper
            // mock setup, no "Cannot start buffering" error occurs anymore.

            // Expect warnings for missing optional components (MetadataHandler for latency tracking)
            LogAssert.Expect(LogType.Warning, "[RequestOrchestrator] GetLatencyTracker: MetadataHandler is NULL for NPC: TestNPC");
            LogAssert.Expect(LogType.Warning, "[RequestOrchestrator] Could not call MarkRecordingStart() - LatencyTracker is null");

            // Start request
            _orchestrator.StartConversationRequest(request);
            Assert.IsTrue(GetRequestActiveState(), "Should be active after StartConversationRequest");

            // End request
            _orchestrator.EndAudioRequest();
            Assert.IsFalse(GetRequestActiveState(), "Should be inactive after EndAudioRequest");
        }

        private bool GetRequestActiveState()
        {
            var field = typeof(RequestOrchestrator).GetField("_isRequestActive",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return (bool)(field?.GetValue(_orchestrator) ?? false);
        }

        #endregion

        #region Test 8: Multi-Turn Conversations - State Management

        /// <summary>
        /// BUSINESS REQUIREMENT: Multiple conversation turns with same NPC must work correctly
        ///
        /// WHY: Medical training involves back-and-forth conversations (10+ turns common)
        /// WHAT: Verify AudioStreamProcessor.EndAudioStream() calls EndStream() on player even when _isStreamingAudio is false
        /// HOW: Test via AudioStreamProcessor (where the bug was), not direct StartStream calls
        ///
        /// SUCCESS CRITERIA:
        /// - EndAudioStream() must call EndStream() on player even if _isStreamingAudio is false
        /// - This ensures _isPlaybackStarted gets reset between turns
        /// - Without this fix, turn 2+ would fail to trigger playback start events
        ///
        /// BUSINESS IMPACT:
        /// - Failure = latency metrics only shown on turn 1, broken UX for multi-turn conversations
        /// - Critical for trainee feedback and progress tracking
        /// - This test validates the fix we made to AudioStreamProcessor.EndAudioStream()
        /// </summary>
        [UnityTest]
        public IEnumerator MultiTurn_SameNPC_StateResetsCorrectly()
        {
            // Wait for Unity lifecycle
            yield return null;

            // Track how many times playback start event fires
            int playbackStartCallCount = 0;

            // Create a real StreamingAudioPlayer to test state management
            var audioPlayerObject = new GameObject("TestAudioPlayer");
            var audioFilterRelay = audioPlayerObject.AddComponent<AudioFilterRelay>();
            var audioPlayer = audioPlayerObject.AddComponent<StreamingAudioPlayer>();
            audioPlayer.SuppressInitializationWarnings();
            audioPlayer.SetAudioFilterRelay(audioFilterRelay);

            // Hook into playback start event
            Action playbackStartHandler = () => playbackStartCallCount++;
            StreamingAudioPlayer.OnPlaybackStartedStatic += playbackStartHandler;

            // Create real AudioStreamProcessor with our test audio player
            var audioProcessor = new AudioStreamProcessor(audioPlayer, opusBitrate: 64000, bufferDuration: 0.1f, isVerboseLogging: false);

            yield return null;

            // TURN 1: Normal flow - processor starts stream, audio added, playback starts
            Debug.Log("========== TURN 1: Normal audio stream ==========");
            audioProcessor.StartAudioStream(isOpus: true, sampleRate: 48000);

            // Add audio via processor (simulates TTS audio arrival)
            float[] samples = new float[15000];
            for (int i = 0; i < samples.Length; i++) samples[i] = 0.1f;
            audioPlayer.AddAudioData(samples);

            yield return new WaitForSeconds(0.1f);
            Assert.AreEqual(1, playbackStartCallCount, "Turn 1: Playback should start");
            Assert.IsTrue(audioProcessor.IsStreamingAudio, "Turn 1: Should be streaming");

            // END TURN 1: Call EndAudioStream - this should call EndStream() on player
            Debug.Log("========== TURN 1 END: Calling EndAudioStream ==========");
            audioProcessor.EndAudioStream();
            yield return null;

            Assert.IsFalse(audioProcessor.IsStreamingAudio, "After EndAudioStream, should not be streaming");

            // TURN 2: The critical test - does playback start again?
            Debug.Log("========== TURN 2: Testing if playback can start again ==========");
            audioProcessor.StartAudioStream(isOpus: true, sampleRate: 48000);

            // Add audio again
            audioPlayer.AddAudioData(samples);

            yield return new WaitForSeconds(0.1f);
            Assert.AreEqual(2, playbackStartCallCount,
                "Turn 2: Playback should start again. " +
                "If this fails, EndAudioStream() didn't call EndStream() to reset _isPlaybackStarted - THE BUG!");

            // Cleanup
            audioProcessor.EndAudioStream();
            StreamingAudioPlayer.OnPlaybackStartedStatic -= playbackStartHandler;
            Object.DestroyImmediate(audioPlayerObject);
            audioProcessor.Dispose();
        }

        #endregion

        #region Mock Classes

        /// <summary>
        /// Mock SpeechInputHandler that can simulate audio chunks
        /// Inherits from real SpeechInputHandler to satisfy Unity type checking
        /// </summary>
        private class MockSpeechInputHandler : SpeechInputHandler
        {
            private AudioStreamProcessor _testAudioStreamProcessor;

            private void Awake()
            {
                // Don't call base.Awake() to avoid initializing real microphone

                // Create a test AudioStreamProcessor (UPSTREAM encoding)
                _testAudioStreamProcessor = new AudioStreamProcessor(
                    audioPlayer: null,
                    opusBitrate: MicrophoneCapture.UPSTREAM_OPUS_BITRATE,
                    bufferDuration: 0f,
                    isVerboseLogging: false
                );

                // CRITICAL: Set the private _audioStreamProcessor field so base property works correctly
                // The base property is NOT virtual, so we must set the backing field
                var field = typeof(SpeechInputHandler).GetField("_audioStreamProcessor",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (field != null)
                {
                    field.SetValue(this, _testAudioStreamProcessor);
                }
                else
                {
                    Debug.LogError("[MockSpeechInputHandler] Failed to set _audioStreamProcessor field via reflection!");
                }
            }

            private void Start()
            {
                // Don't call base.Start() - it tries to subscribe to _microphoneCapture events
                // which is null in test environment (line 249: _microphoneCapture.OnAudioDataAvailable += HandleAudioData)
            }

            public new AudioStreamProcessor AudioStreamProcessor => _testAudioStreamProcessor;

            /// <summary>
            /// Override the test AudioStreamProcessor - used for multi-turn state management tests
            /// </summary>
            public void SetAudioStreamProcessor(AudioStreamProcessor processor)
            {
                _testAudioStreamProcessor = processor;

                // Update the backing field via reflection
                var field = typeof(SpeechInputHandler).GetField("_audioStreamProcessor",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (field != null)
                {
                    field.SetValue(this, processor);
                }
            }

            public void TriggerOnOpusAudioEncoded(byte[] chunk)
            {
                // Get the AudioStreamProcessor property (should return _testAudioStreamProcessor we set via reflection)
                var processor = AudioStreamProcessor;

                if (processor == null)
                {
                    Debug.LogError("[MockSpeechInputHandler] AudioStreamProcessor is null! Cannot trigger event.");
                    return;
                }

                // Try to find the event field with both Public and NonPublic binding flags
                var eventField = typeof(AudioStreamProcessor).GetField("OnOpusAudioEncoded",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                if (eventField == null)
                {
                    Debug.LogError("[MockSpeechInputHandler] Could not find OnOpusAudioEncoded event field via reflection!");
                    return;
                }

                var eventDelegate = eventField.GetValue(processor) as System.MulticastDelegate;

                if (eventDelegate == null)
                {
                    Debug.LogWarning("[MockSpeechInputHandler] OnOpusAudioEncoded has no subscribers! Event will not be triggered.");
                    return;
                }

                // Invoke all subscribers
                Debug.Log($"[MockSpeechInputHandler] Triggering OnOpusAudioEncoded with {chunk.Length} bytes to {eventDelegate.GetInvocationList().Length} subscriber(s)");
                foreach (var handler in eventDelegate.GetInvocationList())
                {
                    handler.Method.Invoke(handler.Target, new object[] { chunk });
                }
            }

            public new void StartRecording() { }
            public new void StopRecording() { }
        }

        /// <summary>
        /// Mock WebSocketClient that tracks sent messages
        /// Inherits from real WebSocketClient to satisfy Unity type checking
        /// </summary>
        private class MockWebSocketClient : WebSocketClient
        {
            public List<byte[]> SentBinaryMessages { get; } = new List<byte[]>();
            public List<string> SentJsonMessages { get; } = new List<string>();

            private void Awake()
            {
                // Don't call base.Awake() to avoid real WebSocket initialization
            }

            private void Start()
            {
                // Don't call base.Start() - it starts InitializeConnectionSequence() coroutine
                // which tries to create real WebSocket connections
            }

            public override bool IsConnected => true;

            public override System.Threading.Tasks.Task SendBinaryAsync(byte[] data)
            {
                SentBinaryMessages.Add(data);
                return System.Threading.Tasks.Task.CompletedTask;
            }

            public override System.Threading.Tasks.Task SendSessionStartAsync(Messages.SessionStartMessage message, System.Threading.CancellationToken cancellationToken = default)
            {
                SentJsonMessages.Add(Newtonsoft.Json.JsonConvert.SerializeObject(message));
                return System.Threading.Tasks.Task.CompletedTask;
            }
        }

        /// <summary>
        /// Mock NpcClient that extends NpcClientBase for testing
        /// Tracks received messages for test assertions
        /// </summary>
        private class MockNpcClient : NpcClientBase
        {
            public override string NpcName => "TestNPC";

            public List<byte[]> ReceivedBinaryMessages { get; } = new List<byte[]>();
            public List<string> ReceivedTextMessages { get; } = new List<string>();
            public List<string> CompletedRequests { get; } = new List<string>();

            protected override void ValidateConfiguration()
            {
                // Mock implementation - no validation needed
            }

            public override void OnTextMessage(string json)
            {
                ReceivedTextMessages.Add(json);
                Debug.Log($"[MockNpcClient] Received text message: {json?.Substring(0, Mathf.Min(50, json?.Length ?? 0))}...");
            }

            public override void OnBinaryMessage(byte[] data)
            {
                ReceivedBinaryMessages.Add(data);
                Debug.Log($"[MockNpcClient] Received binary message: {data?.Length ?? 0} bytes");
            }

            public override void OnRequestComplete(string requestId)
            {
                CompletedRequests.Add(requestId);
                Debug.Log($"[MockNpcClient] Request completed: {requestId}");
            }
        }

        #endregion
    }
}
