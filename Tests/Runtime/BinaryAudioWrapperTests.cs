using NUnit.Framework;
using System;
using System.Text;
using Tsc.AIBridge.Utilities;

namespace Tsc.AIBridge.Tests.Runtime
{
    /// <summary>
    /// BUSINESS REQUIREMENT: Binary audio must be routable to specific NPCs in multi-NPC scenarios
    ///
    /// WHY: Multiple NPCs can have parallel conversations. Binary audio has no JSON RequestId,
    /// so we embed RequestId in the binary frame itself for robust routing.
    ///
    /// WHAT: Tests UnwrapAudioChunk() to ensure:
    /// - Wrapped audio is correctly unwrapped (RequestId + audio data extracted)
    /// - Magic marker (0xAD) is recognized
    /// - Invalid formats throw appropriate exceptions (strict mode)
    /// - Round-trip integrity (wrap → unwrap preserves data)
    ///
    /// HOW: Unit tests cover:
    /// 1. Valid wrapped audio - extracts RequestId and audio data
    /// 2. Unwrapped audio (missing magic marker) - throws exception (strict mode)
    /// 3. Null/empty data - throws exception
    /// 4. Corrupted format (invalid length) - throws exception
    /// 5. Round-trip - wrap on backend, unwrap in Unity
    ///
    /// SUCCESS CRITERIA:
    /// - RequestId correctly extracted from wrapped binary frames
    /// - Audio data preserved bit-for-bit after unwrap
    /// - Strict mode: All invalid formats throw exceptions (no fallbacks)
    /// - Multi-NPC routing works: audio goes to correct NPC only
    ///
    /// BUSINESS IMPACT:
    /// - Failure = Audio crosstalk between NPCs (Rebecca hears John's audio)
    /// - Lost audio in parallel conversations
    /// - Inconsistent behavior in multi-user training scenarios
    /// </summary>
    [TestFixture]
    public class BinaryAudioWrapperTests
    {
        #region Valid Wrapped Audio Tests

        [Test]
        public void UnwrapAudioChunk_WithValidWrappedAudio_ExtractsRequestIdAndAudio()
        {
            // Arrange: Create wrapped audio manually
            // Format: [0xAD][Length][RequestId bytes][Audio data]
            var requestId = "test-request-123";
            var audioData = new byte[] { 0x4F, 0x67, 0x67, 0x53, 0x00, 0x02 }; // OGG header

            var requestIdBytes = Encoding.UTF8.GetBytes(requestId);
            var wrapped = new byte[2 + requestIdBytes.Length + audioData.Length];
            wrapped[0] = 0xAD; // Magic marker
            wrapped[1] = (byte)requestIdBytes.Length;
            Array.Copy(requestIdBytes, 0, wrapped, 2, requestIdBytes.Length);
            Array.Copy(audioData, 0, wrapped, 2 + requestIdBytes.Length, audioData.Length);

            // Act
            var (extractedRequestId, extractedAudio) = BinaryAudioWrapper.UnwrapAudioChunk(wrapped);

            // Assert
            Assert.AreEqual(requestId, extractedRequestId, "RequestId should be extracted correctly");
            Assert.AreEqual(audioData.Length, extractedAudio.Length, "Audio length should match");
            CollectionAssert.AreEqual(audioData, extractedAudio, "Audio data should be preserved bit-for-bit");
        }

        [Test]
        public void UnwrapAudioChunk_WithGuidRequestId_HandlesLongRequestId()
        {
            // Arrange: Test with 36-character GUID RequestId (typical in production)
            var requestId = Guid.NewGuid().ToString(); // 36 characters
            var audioData = new byte[] { 0xFF, 0xAA, 0x55, 0x00 };

            var requestIdBytes = Encoding.UTF8.GetBytes(requestId);
            var wrapped = new byte[2 + requestIdBytes.Length + audioData.Length];
            wrapped[0] = 0xAD;
            wrapped[1] = (byte)requestIdBytes.Length;
            Array.Copy(requestIdBytes, 0, wrapped, 2, requestIdBytes.Length);
            Array.Copy(audioData, 0, wrapped, 2 + requestIdBytes.Length, audioData.Length);

            // Act
            var (extractedRequestId, extractedAudio) = BinaryAudioWrapper.UnwrapAudioChunk(wrapped);

            // Assert
            Assert.AreEqual(requestId, extractedRequestId, "GUID RequestId should be handled");
            Assert.AreEqual(36, extractedRequestId.Length, "GUID should be 36 characters");
            CollectionAssert.AreEqual(audioData, extractedAudio, "Audio data should be intact");
        }

        [Test]
        public void UnwrapAudioChunk_WithLargeAudioChunk_PreservesData()
        {
            // Arrange: Test with realistic 16KB audio chunk
            var requestId = "large-chunk-test";
            var audioData = new byte[16384]; // 16KB
            var random = new System.Random(42);
            random.NextBytes(audioData);

            var requestIdBytes = Encoding.UTF8.GetBytes(requestId);
            var wrapped = new byte[2 + requestIdBytes.Length + audioData.Length];
            wrapped[0] = 0xAD;
            wrapped[1] = (byte)requestIdBytes.Length;
            Array.Copy(requestIdBytes, 0, wrapped, 2, requestIdBytes.Length);
            Array.Copy(audioData, 0, wrapped, 2 + requestIdBytes.Length, audioData.Length);

            // Act
            var (extractedRequestId, extractedAudio) = BinaryAudioWrapper.UnwrapAudioChunk(wrapped);

            // Assert
            Assert.AreEqual(requestId, extractedRequestId);
            Assert.AreEqual(audioData.Length, extractedAudio.Length);
            CollectionAssert.AreEqual(audioData, extractedAudio, "Large audio chunk should be preserved exactly");
        }

        #endregion

        #region Strict Mode: Invalid Format Tests

        [Test]
        public void UnwrapAudioChunk_WithUnwrappedAudio_ThrowsException()
        {
            // Arrange: Raw OGG audio without wrapper (STRICT MODE: this is an error)
            var unwrappedAudio = new byte[] { 0x4F, 0x67, 0x67, 0x53, 0x00, 0x02 }; // "OggS" header

            // Act & Assert: Should throw InvalidOperationException in strict mode
            var exception = Assert.Throws<InvalidOperationException>(() =>
                BinaryAudioWrapper.UnwrapAudioChunk(unwrappedAudio));

            StringAssert.Contains("not wrapped with RequestId", exception.Message,
                "Exception should explain that audio must be wrapped");
        }

        [Test]
        public void UnwrapAudioChunk_WithNullData_ThrowsException()
        {
            // Act & Assert: Null data should throw ArgumentException
            Assert.Throws<ArgumentException>(() =>
                BinaryAudioWrapper.UnwrapAudioChunk(null));
        }

        [Test]
        public void UnwrapAudioChunk_WithEmptyData_ThrowsException()
        {
            // Act & Assert: Empty array should throw ArgumentException
            Assert.Throws<ArgumentException>(() =>
                BinaryAudioWrapper.UnwrapAudioChunk(new byte[0]));
        }

        [Test]
        public void UnwrapAudioChunk_WithInvalidLength_ThrowsException()
        {
            // Arrange: Corrupted format - length field says 20 bytes but only 5 bytes available
            var invalidData = new byte[] { 0xAD, 20, 0x01, 0x02, 0x03 }; // Says 20 bytes RequestId, only 3 bytes

            // Act & Assert: Should throw InvalidOperationException
            var exception = Assert.Throws<InvalidOperationException>(() =>
                BinaryAudioWrapper.UnwrapAudioChunk(invalidData));

            StringAssert.Contains("Invalid wrapped audio format", exception.Message,
                "Exception should indicate invalid format");
        }

        [Test]
        public void UnwrapAudioChunk_WithWrongMagicMarker_ThrowsException()
        {
            // Arrange: Wrong magic marker (0xFF instead of 0xAD)
            var wrongMarker = new byte[] { 0xFF, 0x05, 0x74, 0x65, 0x73, 0x74, 0x00, 0x4F, 0x67, 0x67 };

            // Act & Assert: Should throw InvalidOperationException (not wrapped)
            Assert.Throws<InvalidOperationException>(() =>
                BinaryAudioWrapper.UnwrapAudioChunk(wrongMarker));
        }

        #endregion

        #region Format Detection Tests

        [Test]
        public void IsWrapped_WithValidWrappedAudio_ReturnsTrue()
        {
            // Arrange
            var requestId = "test-123";
            var audioData = new byte[] { 0x01, 0x02 };
            var requestIdBytes = Encoding.UTF8.GetBytes(requestId);
            var wrapped = new byte[2 + requestIdBytes.Length + audioData.Length];
            wrapped[0] = 0xAD;
            wrapped[1] = (byte)requestIdBytes.Length;
            Array.Copy(requestIdBytes, 0, wrapped, 2, requestIdBytes.Length);
            Array.Copy(audioData, 0, wrapped, 2 + requestIdBytes.Length, audioData.Length);

            // Act
            bool isWrapped = BinaryAudioWrapper.IsWrapped(wrapped);

            // Assert
            Assert.IsTrue(isWrapped, "Should detect wrapped format");
        }

        [Test]
        public void IsWrapped_WithUnwrappedAudio_ReturnsFalse()
        {
            // Arrange: OGG audio without wrapper
            var unwrapped = new byte[] { 0x4F, 0x67, 0x67, 0x53, 0x00, 0x02 };

            // Act
            bool isWrapped = BinaryAudioWrapper.IsWrapped(unwrapped);

            // Assert
            Assert.IsFalse(isWrapped, "Should NOT detect unwrapped audio as wrapped");
        }

        [Test]
        public void IsWrapped_WithNullData_ReturnsFalse()
        {
            // Act
            bool isWrapped = BinaryAudioWrapper.IsWrapped(null);

            // Assert
            Assert.IsFalse(isWrapped, "Null data should not be considered wrapped");
        }

        [Test]
        public void IsWrapped_WithTooShortData_ReturnsFalse()
        {
            // Arrange: Only 2 bytes (too short to be valid wrapped format)
            var tooShort = new byte[] { 0xAD, 0x05 }; // Says 5 bytes RequestId but no more data

            // Act
            bool isWrapped = BinaryAudioWrapper.IsWrapped(tooShort);

            // Assert
            Assert.IsFalse(isWrapped, "Too-short data should not be considered wrapped");
        }

        #endregion

        #region Round-Trip Integration Tests

        [Test]
        public void RoundTrip_WrapOnBackendUnwrapInUnity_PreservesBitPerfectAudio()
        {
            // Simulate: Backend wraps → Network → Unity unwraps

            // Arrange: Original audio (what ElevenLabs TTS generates)
            var originalAudio = new byte[] { 0x4F, 0x67, 0x67, 0x53, 0x00, 0x02, 0xFF, 0xAA, 0x55 };
            var requestId = "round-trip-test";

            // Act: Simulate backend wrapping (same algorithm as backend BinaryAudioWrapper)
            var requestIdBytes = Encoding.UTF8.GetBytes(requestId);
            var wrapped = new byte[2 + requestIdBytes.Length + originalAudio.Length];
            wrapped[0] = 0xAD;
            wrapped[1] = (byte)requestIdBytes.Length;
            Array.Copy(requestIdBytes, 0, wrapped, 2, requestIdBytes.Length);
            Array.Copy(originalAudio, 0, wrapped, 2 + requestIdBytes.Length, originalAudio.Length);

            // Unity receives wrapped bytes via WebSocket and unwraps
            var (extractedRequestId, unwrappedAudio) = BinaryAudioWrapper.UnwrapAudioChunk(wrapped);

            // Assert: Bit-perfect preservation
            Assert.AreEqual(requestId, extractedRequestId, "RequestId should survive round-trip");
            Assert.AreEqual(originalAudio.Length, unwrappedAudio.Length, "Audio length should match exactly");

            for (int i = 0; i < originalAudio.Length; i++)
            {
                Assert.AreEqual(originalAudio[i], unwrappedAudio[i],
                    $"Byte {i} should be preserved bit-for-bit (expected 0x{originalAudio[i]:X2}, got 0x{unwrappedAudio[i]:X2})");
            }
        }

        [Test]
        public void WrappedAudioOverhead_WithGuidRequestId_IsMinimal()
        {
            // BUSINESS REQUIREMENT: Overhead should be < 0.3% for typical 16KB audio chunks

            // Arrange
            var requestId = Guid.NewGuid().ToString(); // 36 characters
            var audioData = new byte[16384]; // 16KB typical chunk

            var requestIdBytes = Encoding.UTF8.GetBytes(requestId);
            var wrapped = new byte[2 + requestIdBytes.Length + audioData.Length];
            wrapped[0] = 0xAD;
            wrapped[1] = (byte)requestIdBytes.Length;
            Array.Copy(requestIdBytes, 0, wrapped, 2, requestIdBytes.Length);
            Array.Copy(audioData, 0, wrapped, 2 + requestIdBytes.Length, audioData.Length);

            // Act
            int overhead = wrapped.Length - audioData.Length;
            double overheadPercentage = (overhead / (double)audioData.Length) * 100;

            // Assert
            Assert.AreEqual(2 + requestIdBytes.Length, overhead, "Overhead should be 2 bytes header + RequestId length");
            Assert.Less(overhead, 40, "Overhead should be less than 40 bytes");
            Assert.Less(overheadPercentage, 0.3, "Overhead should be less than 0.3% of audio data");

            UnityEngine.Debug.Log($"[BinaryAudioWrapper] Overhead: {overhead} bytes ({overheadPercentage:F3}%) for {audioData.Length} bytes audio");
        }

        #endregion
    }
}
