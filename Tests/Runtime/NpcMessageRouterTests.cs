using NUnit.Framework;
using UnityEngine;
using Tsc.AIBridge.Core;
using Tsc.AIBridge.Network;
using Tsc.AIBridge.Messages;
using Tsc.AIBridge.WebSocket;
using Tsc.AIBridge.Audio.Playback;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Tsc.AIBridge.Tests.Runtime
{
    /// <summary>
    /// BUSINESS REQUIREMENT: Centralized message routing with special handling for system messages
    ///
    /// WHY: Network quality measurements (BufferHint) should be processed ONCE centrally,
    /// not duplicated across multiple NPCs. This prevents network testing overhead and ensures
    /// consistent buffering across all NPCs in a training session.
    ///
    /// WHAT: Tests that NpcMessageRouter correctly identifies and routes BufferHint messages
    /// to the NetworkQualityMonitor instead of individual NPCs, while still routing other
    /// messages correctly to their target NPCs.
    ///
    /// SUCCESS CRITERIA:
    /// - BufferHint messages are intercepted and sent to NetworkQualityMonitor
    /// - BufferHint messages are NOT sent to NPCs
    /// - Other messages continue to route normally to NPCs
    /// - System handles malformed messages gracefully
    ///
    /// BUSINESS IMPACT:
    /// - Failure = Duplicate network testing causing performance overhead
    /// - Each NPC testing network = 5x overhead with 5 NPCs
    /// - Inconsistent buffering = Different audio quality per NPC
    /// - Poor user experience in multi-NPC training scenarios
    /// </summary>
    public class NpcMessageRouterTests
    {
        private NpcMessageRouter _router;
        private GameObject _monitorObject;
        private NetworkQualityMonitor _networkMonitor;
        private GameObject _bufferManagerObject;
        private AdaptiveBufferManager _bufferManager;

        // Mock NPC client for testing
        private class MockNpcClient : NpcClientBase, IConversationHistory
        {
            public bool ReceivedMessage { get; set; }
            public string LastMessage { get; set; }
            private string _npcName = "TestNPC";
            private readonly List<ChatMessage> _history = new();
            public new TestMetadataHandler MetadataHandler { get; private set; }

            public override string NpcName => _npcName;

            // Initialize handler after Unity creates the component
            private new void Start()
            {
                if (MetadataHandler == null)
                {
                    InitializeHandler();
                }
            }

            public void InitializeHandler()
            {
                MetadataHandler = new TestMetadataHandler(this);

                // Use reflection to set the internal _metadataHandler field
                var fieldInfo = typeof(NpcClientBase).GetField("_metadataHandler",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (fieldInfo != null)
                {
                    fieldInfo.SetValue(this, MetadataHandler);
                }
            }

            public void SetNpcName(string name)
            {
                _npcName = name;
            }

            protected override void ValidateConfiguration()
            {
                // No-op for testing
            }

            public override List<ChatMessage> GetApiHistoryAsChatMessages()
            {
                return new List<ChatMessage>(_history);
            }

            public override void ClearHistory()
            {
                _history.Clear();
            }

            public override void AddPlayerMessage(string message)
            {
                _history.Add(new ChatMessage { Role = "user", Content = message });
            }
        }

        private class TestMetadataHandler : ConversationMetadataHandler
        {
            private readonly MockNpcClient _client;

            public TestMetadataHandler(MockNpcClient client) : base(client.NpcName, null, false)
            {
                _client = client;
            }

            public override void ProcessMessage(string json)
            {
                // Track that we received the message
                _client.ReceivedMessage = true;
                _client.LastMessage = json;
                // Don't call base - we just want to track the message
            }
        }

        [SetUp]
        public void SetUp()
        {
            // Create AdaptiveBufferManager first (required by NetworkQualityMonitor)
            _bufferManagerObject = new GameObject("TestBufferManager");
            _bufferManager = _bufferManagerObject.AddComponent<AdaptiveBufferManager>();

            // Get singleton router instance and reset state
            _router = NpcMessageRouter.Instance;
            _router.Reset(); // Clear any previous test data

            // Create network monitor
            _monitorObject = new GameObject("TestNetworkMonitor");
            _networkMonitor = _monitorObject.AddComponent<NetworkQualityMonitor>();
        }

        [TearDown]
        public void TearDown()
        {
            // Destroy all test GameObjects created for MockNpcClient
            var allGameObjects = Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None);
            foreach (var go in allGameObjects)
            {
                if (go != null && (go.name == "MockNpc" || go.name == "NPC1" || go.name == "NPC2"))
                    Object.DestroyImmediate(go);
            }

            // Reset router state (singleton doesn't need GameObject cleanup)
            _router.Reset();

            if (_monitorObject != null)
                Object.DestroyImmediate(_monitorObject);
            if (_bufferManagerObject != null)
                Object.DestroyImmediate(_bufferManagerObject);

            // Clear singleton references
            var bufferManagerField = typeof(AdaptiveBufferManager).GetField("_instance",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            if (bufferManagerField != null)
            {
                bufferManagerField.SetValue(null, null);
            }

            var networkMonitorField = typeof(NetworkQualityMonitor).GetField("_instance",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            if (networkMonitorField != null)
            {
                networkMonitorField.SetValue(null, null);
            }

            var routerField = typeof(NpcMessageRouter).GetField("_instance",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            if (routerField != null)
            {
                routerField.SetValue(null, null);
            }
        }

        [Test]
        public void Should_CreateSingletonInstance()
        {
            var instance = NpcMessageRouter.Instance;
            Assert.IsNotNull(instance);
            Assert.AreEqual(instance, NpcMessageRouter.Instance);
        }

        [Test]
        public void Should_RouteBufferHintToNetworkMonitor()
        {
            // Create BufferHint message
            var bufferHintJson = JsonConvert.SerializeObject(new
            {
                type = "bufferhint",
                requestId = "test-123",
                networkQuality = "Good",
                recommendedBufferSize = "Medium",
                ttsLatencyMs = 750.0,
                latencyLevel = "Medium"
            });

            var initialCount = _networkMonitor.MeasurementCount;

            // Route the message
            _router.RouteMessage(bufferHintJson);

            // Verify it reached NetworkQualityMonitor
            Assert.AreEqual(initialCount + 1, _networkMonitor.MeasurementCount);
            Assert.AreEqual("Good", _networkMonitor.CurrentQuality);
        }

        [Test]
        public void Should_NotRouteBufferHintToNPC()
        {
            // Register a mock NPC
            var mockNpcObject = new GameObject("MockNpc");
            var mockNpc = mockNpcObject.AddComponent<MockNpcClient>();
            mockNpc.InitializeHandler();
            _router.RegisterNpc(mockNpc);
            _router.SetActiveRequest("test-123", "TestNPC");

            // Create BufferHint message
            var bufferHintJson = JsonConvert.SerializeObject(new
            {
                type = "bufferhint",
                requestId = "test-123",
                networkQuality = "Good"
            });

            // Route the message
            _router.RouteMessage(bufferHintJson);

            // Verify NPC did NOT receive the message
            Assert.IsFalse(mockNpc.ReceivedMessage);
        }

        [Test]
        public void Should_RouteNormalMessagesToNPC()
        {
            // Register a mock NPC
            var mockNpcObject = new GameObject("MockNpc");
            var mockNpc = mockNpcObject.AddComponent<MockNpcClient>();
            mockNpc.InitializeHandler();
            _router.RegisterNpc(mockNpc);
            _router.SetActiveRequest("test-123", "TestNPC");

            // Create normal message (not BufferHint)
            var normalMessage = JsonConvert.SerializeObject(new
            {
                type = "transcription",
                requestId = "test-123",
                text = "Hello world"
            });

            // Route the message (this will go through _metadataHandler)
            _router.RouteMessage(normalMessage);

            // Verify NPC's metadata handler received the message
            Assert.IsTrue(mockNpc.ReceivedMessage);
            Assert.IsTrue(mockNpc.LastMessage.Contains("transcription"));
        }

        [Test]
        public void Should_HandleBufferHintCaseInsensitive()
        {
            var initialCount = _networkMonitor.MeasurementCount;

            // Test lowercase
            var lowercase = JsonConvert.SerializeObject(new
            {
                type = "bufferhint",
                ttsLatencyMs = 500.0
            });
            _router.RouteMessage(lowercase);

            // Test uppercase
            var uppercase = JsonConvert.SerializeObject(new
            {
                type = "BufferHint",
                ttsLatencyMs = 600.0
            });
            _router.RouteMessage(uppercase);

            // Both should be processed
            Assert.AreEqual(initialCount + 2, _networkMonitor.MeasurementCount);
        }

        [Test]
        public void Should_RegisterAndUnregisterNpc()
        {
            var mockNpcObject = new GameObject("MockNpc");
            var mockNpc = mockNpcObject.AddComponent<MockNpcClient>();
            mockNpc.InitializeHandler();

            // Register
            _router.RegisterNpc(mockNpc);
            _router.SetActiveRequest("test-123", "TestNPC");

            // Send message - should receive
            var message = JsonConvert.SerializeObject(new
            {
                type = "test",
                requestId = "test-123"
            });
            _router.RouteMessage(message);
            Assert.IsTrue(mockNpc.ReceivedMessage);

            // Unregister
            mockNpc.ReceivedMessage = false;
            _router.UnregisterNpc(mockNpc);

            // Send message - should NOT receive
            _router.RouteMessage(message);
            Assert.IsFalse(mockNpc.ReceivedMessage);
        }

        [Test]
        public void Should_ExtractRequestIdCorrectly()
        {
            var mockNpcObject = new GameObject("MockNpc");
            var mockNpc = mockNpcObject.AddComponent<MockNpcClient>();
            mockNpc.InitializeHandler();
            _router.RegisterNpc(mockNpc);
            _router.SetActiveRequest("req-456", "TestNPC");

            // Message with requestId
            var message = JsonConvert.SerializeObject(new
            {
                type = "test",
                requestId = "req-456",
                data = "test data"
            });

            _router.RouteMessage(message);

            Assert.IsTrue(mockNpc.ReceivedMessage);
        }

        [Test]
        public void Should_RouteToFirstActiveNpcIfNoRequestId()
        {
            var mockNpcObject = new GameObject("MockNpc");
            var mockNpc = mockNpcObject.AddComponent<MockNpcClient>();
            mockNpc.InitializeHandler();
            mockNpc.IsActive = true;
            _router.RegisterNpc(mockNpc);

            // Message without requestId
            var message = JsonConvert.SerializeObject(new
            {
                type = "test",
                data = "test data"
            });

            _router.RouteMessage(message);

            // Should fallback to first active NPC
            Assert.IsTrue(mockNpc.ReceivedMessage);
        }

        [Test]
        public void Should_HandleNullAndEmptyMessages()
        {
            // Should not throw
            Assert.DoesNotThrow(() => _router.RouteMessage(null));
            Assert.DoesNotThrow(() => _router.RouteMessage(""));
            Assert.DoesNotThrow(() => _router.RouteMessage("   "));
        }

        [Test]
        public void Should_HandleMalformedJson()
        {
            // Malformed JSON should not crash
            Assert.DoesNotThrow(() => _router.RouteMessage("{ invalid json"));
            Assert.DoesNotThrow(() => _router.RouteMessage("not json at all"));
        }

        [Test]
        public void Should_ClearRequestAssociation()
        {
            var mockNpcObject = new GameObject("MockNpc");
            var mockNpc = mockNpcObject.AddComponent<MockNpcClient>();
            mockNpc.InitializeHandler();
            _router.RegisterNpc(mockNpc);
            _router.SetActiveRequest("test-789", "TestNPC");

            // Message should route
            var message = JsonConvert.SerializeObject(new
            {
                type = "test",
                requestId = "test-789"
            });
            _router.RouteMessage(message);
            Assert.IsTrue(mockNpc.ReceivedMessage);

            // Clear the request
            mockNpc.ReceivedMessage = false;
            _router.ClearRequest("test-789");

            // Message should NOT route anymore
            _router.RouteMessage(message);
            Assert.IsFalse(mockNpc.ReceivedMessage);
        }

        [Test]
        public void Should_HandleMultipleNpcs()
        {
            var npc1Object = new GameObject("NPC1");
            var npc1 = npc1Object.AddComponent<MockNpcClient>();
            npc1.SetNpcName("NPC1");
            npc1.InitializeHandler();

            var npc2Object = new GameObject("NPC2");
            var npc2 = npc2Object.AddComponent<MockNpcClient>();
            npc2.SetNpcName("NPC2");
            npc2.InitializeHandler();

            _router.RegisterNpc(npc1);
            _router.RegisterNpc(npc2);

            _router.SetActiveRequest("req-1", "NPC1");
            _router.SetActiveRequest("req-2", "NPC2");

            // Send to NPC1
            var message1 = JsonConvert.SerializeObject(new
            {
                type = "test",
                requestId = "req-1",
                target = "npc1"
            });
            _router.RouteMessage(message1);

            Assert.IsTrue(npc1.ReceivedMessage);
            Assert.IsFalse(npc2.ReceivedMessage);

            // Reset
            npc1.ReceivedMessage = false;

            // Send to NPC2
            var message2 = JsonConvert.SerializeObject(new
            {
                type = "test",
                requestId = "req-2",
                target = "npc2"
            });
            _router.RouteMessage(message2);

            Assert.IsFalse(npc1.ReceivedMessage);
            Assert.IsTrue(npc2.ReceivedMessage);
        }

        [Test]
        public void Should_NotRouteToInactiveNpc()
        {
            var mockNpcObject = new GameObject("MockNpc");
            var mockNpc = mockNpcObject.AddComponent<MockNpcClient>();
            mockNpc.InitializeHandler();
            mockNpc.IsActive = false; // Inactive
            _router.RegisterNpc(mockNpc);

            // Message without specific requestId (fallback routing)
            var message = JsonConvert.SerializeObject(new
            {
                type = "test",
                data = "test"
            });

            _router.RouteMessage(message);

            // Should NOT route to inactive NPC
            Assert.IsFalse(mockNpc.ReceivedMessage);
        }

        [Test]
        public void Should_HandleBufferHintWithoutNetworkMonitor()
        {
            // Destroy the network monitor
            Object.Destroy(_monitorObject);
            _monitorObject = null;
            _networkMonitor = null;

            // Should not crash when routing BufferHint
            var bufferHintJson = JsonConvert.SerializeObject(new
            {
                type = "bufferhint",
                networkQuality = "Good"
            });

            Assert.DoesNotThrow(() => _router.RouteMessage(bufferHintJson));
        }
    }
}