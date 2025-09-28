using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Tsc.AIBridge.Network;
using Tsc.AIBridge.Audio.Playback;
using Tsc.AIBridge.Messages;

namespace Tsc.AIBridge.Tests.Runtime
{
    /// <summary>
    /// BUSINESS REQUIREMENT: Centralized network quality monitoring for optimal buffering
    ///
    /// WHY: Each training session needs optimal audio buffering based on actual network conditions.
    /// Multiple NPCs should NOT test the network independently - this creates overhead and inconsistency.
    ///
    /// WHAT: Tests that NetworkQualityMonitor correctly measures network quality ONCE for all NPCs,
    /// calculates optimal buffer sizes, and adapts bidirectionally to changing conditions.
    ///
    /// SUCCESS CRITERIA:
    /// - Single network measurement point for entire scene
    /// - Automatic buffer calculation without manual configuration
    /// - Bidirectional adaptation (both up and down)
    /// - Self-calibration during first 10 seconds
    /// - Consistent buffer settings across all NPCs
    ///
    /// BUSINESS IMPACT:
    /// - Failure = Audio glitches, stuttering, or unnecessary latency
    /// - Poor network adaptation = Frustrated trainers and trainees
    /// - Overhead from duplicate measurements = Performance issues on Quest headsets
    /// </summary>
    public class NetworkQualityMonitorTests
    {
        private NetworkQualityMonitor _monitor;
        private GameObject _gameObject;
        private GameObject _bufferManagerObject;
        private AdaptiveBufferManager _bufferManager;

        [SetUp]
        public void SetUp()
        {
            // Create AdaptiveBufferManager first (required by NetworkQualityMonitor)
            _bufferManagerObject = new GameObject("TestBufferManager");
            _bufferManager = _bufferManagerObject.AddComponent<AdaptiveBufferManager>();

            _gameObject = new GameObject("TestNetworkMonitor");
            _monitor = _gameObject.AddComponent<NetworkQualityMonitor>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_gameObject != null)
            {
                Object.DestroyImmediate(_gameObject);
            }
            if (_bufferManagerObject != null)
            {
                Object.DestroyImmediate(_bufferManagerObject);
            }

            // Clear singleton references to prevent conflicts between tests
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
        }

        [Test]
        public void Should_CreateSingletonInstance()
        {
            // Singleton should return the same instance
            var instance1 = NetworkQualityMonitor.Instance;
            var instance2 = NetworkQualityMonitor.Instance;

            Assert.AreEqual(instance1, instance2);
            Assert.IsNotNull(instance1);
        }

        [Test]
        public void Should_AutoCreateInstanceIfMissing()
        {
            // Destroy our test instance
            Object.Destroy(_gameObject);
            _gameObject = null;
            _monitor = null;

            // Should auto-create
            var instance = NetworkQualityMonitor.Instance;

            Assert.IsNotNull(instance);
            Assert.IsNotNull(instance.gameObject);
        }

        [Test]
        public void Should_StartWithUnknownQuality()
        {
            Assert.AreEqual("Unknown", _monitor.CurrentQuality);
            Assert.AreEqual(0, _monitor.MeasurementCount);
        }

        [Test]
        public void Should_ProcessBufferHintAndUpdateQuality()
        {
            var bufferHint = new BufferHintMessage
            {
                NetworkQuality = "Good",
                RecommendedBufferSize = "Medium",
                TtsLatencyMs = 750.0,
                LatencyLevel = "Medium"
            };

            _monitor.ProcessBufferHint(bufferHint);

            Assert.AreEqual("Good", _monitor.CurrentQuality);
            Assert.AreEqual(750f, _monitor.AverageLatencyMs);
            Assert.AreEqual(1, _monitor.MeasurementCount);
        }

        [Test]
        public void Should_CalculateRunningAverage()
        {
            // Send multiple measurements
            _monitor.ProcessBufferHint(new BufferHintMessage { TtsLatencyMs = 500 });
            _monitor.ProcessBufferHint(new BufferHintMessage { TtsLatencyMs = 700 });
            _monitor.ProcessBufferHint(new BufferHintMessage { TtsLatencyMs = 600 });

            // Average should be (500 + 700 + 600) / 3 = 600
            Assert.AreEqual(600f, _monitor.AverageLatencyMs, 0.01f);
            Assert.AreEqual(3, _monitor.MeasurementCount);
        }

        [Test]
        public void Should_TrackMinMaxLatency()
        {
            _monitor.ProcessBufferHint(new BufferHintMessage { TtsLatencyMs = 500 });
            _monitor.ProcessBufferHint(new BufferHintMessage { TtsLatencyMs = 200 });  // Min
            _monitor.ProcessBufferHint(new BufferHintMessage { TtsLatencyMs = 1000 }); // Max

            Assert.AreEqual(200f, _monitor.MinLatencyMs);
            Assert.AreEqual(1000f, _monitor.MaxLatencyMs);
        }

        [Test]
        public void Should_DetermineNetworkQualityCorrectly()
        {
            // Test Excellent
            _monitor.ProcessBufferHint(new BufferHintMessage { TtsLatencyMs = 300 });
            Assert.AreEqual("Excellent", _monitor.CurrentQuality);

            // Test Good
            _monitor.ProcessBufferHint(new BufferHintMessage { TtsLatencyMs = 750 });
            Assert.AreEqual("Good", _monitor.CurrentQuality);

            // Test Fair
            _monitor.ProcessBufferHint(new BufferHintMessage { TtsLatencyMs = 1500 });
            Assert.AreEqual("Fair", _monitor.CurrentQuality);

            // Test Poor
            _monitor.ProcessBufferHint(new BufferHintMessage { TtsLatencyMs = 3000 });
            Assert.AreEqual("Poor", _monitor.CurrentQuality);
        }

        [Test]
        public void Should_CalculateBufferBasedOnLatency()
        {
            // Low latency = smaller buffer
            _monitor.ProcessBufferHint(new BufferHintMessage { TtsLatencyMs = 200 });
            var lowLatencyBuffer = _monitor.RecommendedBufferDuration;

            // High latency = larger buffer
            _monitor.ProcessBufferHint(new BufferHintMessage { TtsLatencyMs = 2000 });
            var highLatencyBuffer = _monitor.RecommendedBufferDuration;

            Assert.Greater(highLatencyBuffer, lowLatencyBuffer);

            // Verify bounds (50ms min, 1000ms max)
            Assert.GreaterOrEqual(lowLatencyBuffer, 0.05f);
            Assert.LessOrEqual(highLatencyBuffer, 1.0f);
        }


        [Test]
        public void Should_BeMoreConservativeDuringCalibration()
        {
            // During calibration (first 10 seconds)
            Assert.IsTrue(_monitor.IsCalibrating);

            _monitor.ProcessBufferHint(new BufferHintMessage { TtsLatencyMs = 500 });
            var calibrationBuffer = _monitor.RecommendedBufferDuration;

            // Should be slightly higher during calibration (safety margin)
            Assert.Greater(calibrationBuffer, 0.05f); // More than absolute minimum
        }

        [Test]
        public void Should_ProcessInitialRttMeasurement()
        {
            // Initial RTT sets baseline
            _monitor.ProcessInitialRtt(150f);

            Assert.AreEqual(150f, _monitor.AverageLatencyMs);
            Assert.AreEqual(1, _monitor.MeasurementCount);
            Assert.AreEqual(150f, _monitor.MinLatencyMs);
            Assert.AreEqual(150f, _monitor.MaxLatencyMs);
        }

        [Test]
        public void Should_FireLatencyMeasuredEvent()
        {
            float receivedLatency = 0;
            _monitor.OnLatencyMeasured += (latency) => receivedLatency = latency;

            _monitor.ProcessBufferHint(new BufferHintMessage { TtsLatencyMs = 750 });

            Assert.AreEqual(750f, receivedLatency);
        }


        [Test]
        public void Should_ProvideAccurateNetworkStats()
        {
            _monitor.ProcessBufferHint(new BufferHintMessage { TtsLatencyMs = 300 });
            _monitor.ProcessBufferHint(new BufferHintMessage { TtsLatencyMs = 500 });
            _monitor.ProcessBufferHint(new BufferHintMessage { TtsLatencyMs = 400 });

            var stats = _monitor.GetNetworkStats();

            Assert.IsTrue(stats.Contains("Quality: Excellent"));
            Assert.IsTrue(stats.Contains("400ms")); // Average
            Assert.IsTrue(stats.Contains("min: 300ms"));
            Assert.IsTrue(stats.Contains("max: 500ms"));
            Assert.IsTrue(stats.Contains("Samples: 3"));
        }

        [Test]
        public void Should_UseP95LatencyForBufferCalculation()
        {
            // Add multiple measurements with one spike
            _monitor.ProcessBufferHint(new BufferHintMessage { TtsLatencyMs = 200 });
            _monitor.ProcessBufferHint(new BufferHintMessage { TtsLatencyMs = 210 });
            _monitor.ProcessBufferHint(new BufferHintMessage { TtsLatencyMs = 190 });
            _monitor.ProcessBufferHint(new BufferHintMessage { TtsLatencyMs = 1000 }); // Spike

            // Buffer should be based on 95th percentile (not full max)
            var buffer = _monitor.RecommendedBufferDuration;

            // Should handle the spike but not overreact
            Assert.Greater(buffer, 0.05f); // More than minimum
            Assert.Less(buffer, 0.2f);     // But not excessive
        }

        [UnityTest]
        public IEnumerator Should_EndCalibrationAfter10Seconds()
        {
            Assert.IsTrue(_monitor.IsCalibrating);

            // Wait for calibration period
            yield return new WaitForSeconds(10.1f);

            Assert.IsFalse(_monitor.IsCalibrating);
        }

        [Test]
        public void Should_NotCrashWithNullBufferHint()
        {
            // Should handle null gracefully
            Assert.DoesNotThrow(() => _monitor.ProcessBufferHint(null));
        }

        [Test]
        public void Should_HandleRapidMeasurements()
        {
            // Simulate rapid measurements
            for (int i = 0; i < 100; i++)
            {
                var latency = Random.Range(100f, 1000f);
                _monitor.ProcessBufferHint(new BufferHintMessage { TtsLatencyMs = latency });
            }

            Assert.AreEqual(100, _monitor.MeasurementCount);
            Assert.Greater(_monitor.AverageLatencyMs, 0);
            Assert.Greater(_monitor.RecommendedBufferDuration, 0);
        }
    }
}