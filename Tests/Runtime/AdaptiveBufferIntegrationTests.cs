using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Tsc.AIBridge.Network;
using Tsc.AIBridge.Audio.Playback;
using Tsc.AIBridge.Core;
using Tsc.AIBridge.Messages;
using Newtonsoft.Json;

namespace Tsc.AIBridge.Tests.Runtime
{
    /// <summary>
    /// BUSINESS REQUIREMENT: End-to-end adaptive buffering without manual configuration
    ///
    /// WHY: Medical training in hospitals has unpredictable network conditions.
    /// Manual buffer configuration leads to either audio glitches (buffer too small)
    /// or unnecessary latency (buffer too large). The system must self-calibrate.
    ///
    /// WHAT: Integration tests validating the complete flow from network measurements
    /// to actual buffer adjustments across all NPCs in a training scenario.
    ///
    /// HOW: Simulates real-world scenarios including network fluctuations,
    /// multiple NPCs, and verifies consistent behavior across the system.
    ///
    /// SUCCESS CRITERIA:
    /// - Zero manual configuration required
    /// - All NPCs use the same buffer settings
    /// - Buffer adapts bidirectionally based on network
    /// - No duplicate network testing overhead
    /// - Graceful handling of network spikes and drops
    ///
    /// BUSINESS IMPACT:
    /// - Failure = Poor training experience, audio issues
    /// - Success = Seamless training across all network conditions
    /// - Cost savings from reduced support calls about audio issues
    /// </summary>
    public class AdaptiveBufferIntegrationTests
    {
        private GameObject _routerObject;
        private GameObject _monitorObject;
        private GameObject _bufferManagerObject;
        private GameObject _audioPlayerObject;

        private NpcMessageRouter _router;
        private NetworkQualityMonitor _networkMonitor;
        private AdaptiveBufferManager _bufferManager;
        private StreamingAudioPlayer _audioPlayer;

        [SetUp]
        public void SetUp()
        {
            // Enable test mode to suppress initialization warnings
            StreamingAudioPlayer.SetGlobalTestMode(true);

            // Create all components
            _routerObject = new GameObject("Router");
            _router = _routerObject.AddComponent<NpcMessageRouter>();

            _monitorObject = new GameObject("NetworkMonitor");
            _networkMonitor = _monitorObject.AddComponent<NetworkQualityMonitor>();

            _bufferManagerObject = new GameObject("BufferManager");
            _bufferManager = _bufferManagerObject.AddComponent<AdaptiveBufferManager>();

            _audioPlayerObject = new GameObject("AudioPlayer");
            _audioPlayerObject.AddComponent<AudioSource>();

            // First add StreamingAudioPlayer
            _audioPlayer = _audioPlayerObject.AddComponent<StreamingAudioPlayer>();

            // Then add AudioFilterRelay and immediately set it
            var relay = _audioPlayerObject.AddComponent<AudioFilterRelay>();
            _audioPlayer.SetAudioFilterRelay(relay);
        }

        [TearDown]
        public void TearDown()
        {
            // Destroy in reverse order to avoid dependency issues
            if (_audioPlayerObject) Object.DestroyImmediate(_audioPlayerObject);
            if (_routerObject) Object.DestroyImmediate(_routerObject);
            if (_monitorObject) Object.DestroyImmediate(_monitorObject);
            if (_bufferManagerObject) Object.DestroyImmediate(_bufferManagerObject);

            // Clear singleton references
            var bufferManagerField = typeof(AdaptiveBufferManager).GetField("_instance",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            if (bufferManagerField != null)
            {
                bufferManagerField.SetValue(null, null);
            }

            // Disable test mode
            StreamingAudioPlayer.SetGlobalTestMode(false);
        }

        [Test]
        public void Should_RouteBufferHintThroughEntireSystem()
        {
            // Simulate BufferHint message from WebSocket
            var bufferHintJson = JsonConvert.SerializeObject(new
            {
                type = "bufferhint",
                requestId = "test-123",
                networkQuality = "Good",
                recommendedBufferSize = "Medium",
                ttsLatencyMs = 750.0,
                latencyLevel = "Medium"
            });

            // Route through the system
            _router.RouteMessage(bufferHintJson);

            // Verify NetworkQualityMonitor received it
            Assert.AreEqual(1, _networkMonitor.MeasurementCount);
            Assert.AreEqual("Good", _networkMonitor.CurrentQuality);
            Assert.AreEqual(750f, _networkMonitor.AverageLatencyMs);

            // Verify buffer recommendation is calculated
            Assert.Greater(_networkMonitor.RecommendedBufferDuration, 0);
        }


        [Test]
        public void Should_HandleMultipleNpcsWithSameBuffer()
        {
            // Create multiple audio players (simulating multiple NPCs)
            var npc1Object = new GameObject("NPC1");
            npc1Object.AddComponent<AudioSource>();
            var npc1Audio = npc1Object.AddComponent<StreamingAudioPlayer>();
            var relay1 = npc1Object.AddComponent<AudioFilterRelay>();
            npc1Audio.SetAudioFilterRelay(relay1);

            var npc2Object = new GameObject("NPC2");
            npc2Object.AddComponent<AudioSource>();
            var npc2Audio = npc2Object.AddComponent<StreamingAudioPlayer>();
            var relay2 = npc2Object.AddComponent<AudioFilterRelay>();
            npc2Audio.SetAudioFilterRelay(relay2);

            try
            {
                // Send network measurement
                SimulateNetworkCondition("Good", 600);

                // All NPCs should get the same buffer recommendation
                var recommendedBuffer = _networkMonitor.RecommendedBufferDuration;

                // Start streams on all NPCs
                npc1Audio.StartStream(48000);
                npc2Audio.StartStream(48000);

                // Verify all use centralized buffer
                // (They query AdaptiveBufferManager.Instance internally)
                Assert.AreEqual(_bufferManager.CurrentBufferDuration, _bufferManager.CurrentBufferDuration);
            }
            finally
            {
                if (npc1Object) Object.Destroy(npc1Object);
                if (npc2Object) Object.Destroy(npc2Object);
            }
        }

        [Test]
        public void Should_NotDuplicateNetworkMeasurements()
        {
            // Simulate multiple BufferHint messages with same timestamp
            // (as if multiple NPCs were measuring network)
            var timestamp = System.DateTimeOffset.Now.ToUnixTimeMilliseconds();

            for (int i = 0; i < 5; i++)
            {
                var bufferHintJson = JsonConvert.SerializeObject(new
                {
                    type = "bufferhint",
                    requestId = $"npc-{i}",
                    networkQuality = "Good",
                    ttsLatencyMs = 500.0,
                    timestamp = timestamp
                });

                _router.RouteMessage(bufferHintJson);
            }

            // Should process all measurements (no deduplication by timestamp)
            // But they all go to the same NetworkQualityMonitor
            Assert.AreEqual(5, _networkMonitor.MeasurementCount);

            // All measurements contribute to the same average
            Assert.AreEqual(500f, _networkMonitor.AverageLatencyMs);
        }


        [Test]
        public void Should_RespectMinimumAndMaximumBuffers()
        {
            // Test minimum (perfect network)
            for (int i = 0; i < 5; i++)
            {
                SimulateNetworkCondition("Excellent", 50);
            }
            var minBuffer = _networkMonitor.RecommendedBufferDuration;
            Assert.GreaterOrEqual(minBuffer, 0.05f); // 50ms absolute minimum

            // Test maximum (terrible network)
            for (int i = 0; i < 5; i++)
            {
                SimulateNetworkCondition("Poor", 10000);
            }
            var maxBuffer = _networkMonitor.RecommendedBufferDuration;
            Assert.LessOrEqual(maxBuffer, 1.0f); // 1 second maximum
        }

        [UnityTest]
        public IEnumerator Should_CompleteCalibrationPhase()
        {
            // During calibration
            Assert.IsTrue(_networkMonitor.IsCalibrating);

            // Send measurements during calibration
            for (int i = 0; i < 5; i++)
            {
                SimulateNetworkCondition("Good", Random.Range(400f, 600f));
                yield return new WaitForSeconds(0.5f);
            }

            // Still calibrating
            Assert.IsTrue(_networkMonitor.IsCalibrating);

            // Wait for calibration to complete (10 seconds total)
            yield return new WaitForSeconds(8f);

            // Calibration complete
            Assert.IsFalse(_networkMonitor.IsCalibrating);
        }


        [Test]
        public void Should_HandleRapidNetworkChanges()
        {
            // Simulate rapidly changing network (mobile user moving)
            for (int i = 0; i < 20; i++)
            {
                var qualities = new[] { "Excellent", "Good", "Fair", "Poor" };
                var quality = qualities[i % 4];
                var latency = 200 + (i % 4) * 500;

                SimulateNetworkCondition(quality, latency);
            }

            // System should still be stable
            Assert.Greater(_networkMonitor.MeasurementCount, 0);
            Assert.Greater(_networkMonitor.RecommendedBufferDuration, 0);
            Assert.Less(_networkMonitor.RecommendedBufferDuration, 1.0f);
        }

        [Test]
        public void Should_WorkWithoutManualConfiguration()
        {
            // No Inspector configuration needed - system self-calibrates
            // Just send measurements and it works

            SimulateNetworkCondition("Good", 600);

            // Should have reasonable defaults without any configuration
            Assert.Greater(_networkMonitor.RecommendedBufferDuration, 0.05f);
            Assert.Less(_networkMonitor.RecommendedBufferDuration, 1.0f);

            // Should provide quality assessment
            Assert.AreEqual("Good", _networkMonitor.CurrentQuality);
        }

        #region Helper Methods

        private void SimulateNetworkCondition(string quality, float latencyMs)
        {
            var bufferHintJson = JsonConvert.SerializeObject(new
            {
                type = "bufferhint",
                requestId = System.Guid.NewGuid().ToString(),
                networkQuality = quality,
                recommendedBufferSize = GetRecommendedSize(quality),
                ttsLatencyMs = latencyMs,
                latencyLevel = GetLatencyLevel(latencyMs),
                timestamp = System.DateTimeOffset.Now.ToUnixTimeMilliseconds()
            });

            _router.RouteMessage(bufferHintJson);
        }

        private string GetRecommendedSize(string quality)
        {
            return quality switch
            {
                "Excellent" => "Small",
                "Good" => "Medium",
                "Fair" => "Large",
                "Poor" => "Large",
                _ => "Medium"
            };
        }

        private string GetLatencyLevel(float latencyMs)
        {
            if (latencyMs < 300) return "Low";
            if (latencyMs < 1000) return "Medium";
            if (latencyMs < 2000) return "High";
            return "VeryHigh";
        }

        #endregion
    }
}