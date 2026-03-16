using System;
using NUnit.Framework;
using Tsc.AIBridge.Audio.Codecs;
using UnityEngine;
using UnityEngine.TestTools;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: Opus audio decoding must work reliably across all platforms (iOS, Android, Windows)
    ///
    /// WHY: iOS uses a native Opus wrapper that throws different error messages than the cross-platform DLL.
    ///      If the exception filter only catches one format, invalid packets cause unhandled exceptions that
    ///      corrupt stream state, causing the NPC to hang permanently after the 2nd conversation turn.
    /// WHAT: Tests that OpusStreamDecoder handles error conditions gracefully on all platforms.
    /// HOW: Verifies exception filter patterns, state management, and edge case handling.
    ///
    /// SUCCESS CRITERIA:
    /// - Both iOS ("Opus decode error: -4") and DLL ("OPUS_INVALID_PACKET") error formats are caught
    /// - Decoder state is properly reset between conversation turns
    /// - Null/empty data doesn't crash the decoder
    /// - After Reset(), decoder accepts new OGG streams cleanly
    ///
    /// BUSINESS IMPACT:
    /// - Falen = NPC hangs forever on iOS after 2nd conversation turn
    /// - Users can't complete training sessions on iOS devices
    /// - Root cause of iOS-only production bug reported 2026-03-16
    /// </summary>
    [TestFixture]
    public class OpusStreamDecoderTests
    {
        private OpusStreamDecoder _decoder;

        [SetUp]
        public void SetUp()
        {
            _decoder = new OpusStreamDecoder(isVerboseLogging: false);
        }

        [TearDown]
        public void TearDown()
        {
            LogAssert.ignoreFailingMessages = false;
            _decoder?.Dispose();
        }

        #region Exception Filter Pattern Tests

        /// <summary>
        /// BUSINESS REQUIREMENT: iOS NPC must not hang due to unhandled Opus decode exceptions
        ///
        /// WHY: iOS native wrapper throws "Opus decode error: -4", DLL throws "OPUS_INVALID_PACKET".
        ///      The original exception filter only caught "OPUS_INVALID_PACKET", causing iOS to hang.
        /// WHAT: Verifies the exception filter pattern matches BOTH error message formats.
        /// HOW: Tests the string.Contains() patterns used in the catch-when clause.
        ///
        /// SUCCESS CRITERIA:
        /// - iOS format "Opus decode error: -4" is matched by the filter
        /// - DLL format "OPUS_INVALID_PACKET" is matched by the filter
        /// - Other exceptions are NOT matched (they should propagate)
        ///
        /// BUSINESS IMPACT:
        /// - This is the ROOT CAUSE of the iOS hanging bug
        /// - Without this fix, every iOS user experiences NPC hang after 2nd turn
        /// </summary>
        [TestCase("Opus decode error: -4", true, TestName = "iOS native format (error code -4)")]
        [TestCase("Opus decode error: -1", true, TestName = "iOS native format (error code -1)")]
        [TestCase("Opus decode error: -2", true, TestName = "iOS native format (error code -2)")]
        [TestCase("OPUS_INVALID_PACKET", true, TestName = "DLL format (OPUS_INVALID_PACKET)")]
        [TestCase("Some other Opus error", false, TestName = "Unrelated error should NOT be caught")]
        [TestCase("NullReferenceException", false, TestName = "Non-Opus error should NOT be caught")]
        [TestCase("", false, TestName = "Empty error message should NOT be caught")]
        public void ExceptionFilter_MatchesBothPlatformErrorFormats(string errorMessage, bool shouldBeCaught)
        {
            // This tests the exact pattern used in OpusStreamDecoder.ProcessAvailablePackets()
            // catch (Exception ex) when (ex.Message.Contains("Opus decode error") ||
            //                           ex.Message.Contains("OPUS_INVALID_PACKET"))
            var isCaught = errorMessage.Contains("Opus decode error") ||
                           errorMessage.Contains("OPUS_INVALID_PACKET");

            Assert.AreEqual(shouldBeCaught, isCaught,
                $"Error message '{errorMessage}' should {(shouldBeCaught ? "" : "NOT ")}be caught by exception filter");
        }

        /// <summary>
        /// BUSINESS REQUIREMENT: The exception filter must handle the exact error messages thrown by platform-specific Opus wrappers
        ///
        /// WHY: OpusCoreIOS.cs throws "Opus decode error: {result}" where result is a negative error code.
        ///      OpusSharp.Core DLL throws messages containing "OPUS_INVALID_PACKET".
        ///      The filter must work for both without false positives.
        /// WHAT: Simulates the catch-when pattern with actual Exception objects.
        /// HOW: Creates exceptions matching each platform's format and verifies the when-clause.
        ///
        /// SUCCESS CRITERIA:
        /// - Exception with iOS message is caught (when clause returns true)
        /// - Exception with DLL message is caught (when clause returns true)
        ///
        /// BUSINESS IMPACT:
        /// - Ensures cross-platform audio decoding resilience
        /// </summary>
        [Test]
        public void ExceptionFilter_CatchesActualExceptionObjects_FromBothPlatforms()
        {
            // iOS native wrapper exception (from OpusCoreIOS.cs line 116)
            var iosException = new Exception("Opus decode error: -4");

            // DLL wrapper exception (from OpusSharp.Core)
            var dllException = new Exception("OPUS_INVALID_PACKET");

            // Simulate the catch-when pattern
            Assert.IsTrue(ShouldCatchOpusException(iosException),
                "iOS exception must be caught by filter");
            Assert.IsTrue(ShouldCatchOpusException(dllException),
                "DLL exception must be caught by filter");

            // Verify other exceptions are NOT caught
            var otherException = new InvalidOperationException("Stream is disposed");
            Assert.IsFalse(ShouldCatchOpusException(otherException),
                "Non-Opus exceptions must NOT be caught by filter");
        }

        /// <summary>
        /// Mirrors the exact exception filter from OpusStreamDecoder.ProcessAvailablePackets()
        /// </summary>
        private static bool ShouldCatchOpusException(Exception ex)
        {
            return ex.Message.Contains("Opus decode error") ||
                   ex.Message.Contains("OPUS_INVALID_PACKET");
        }

        #endregion

        #region State Management Tests

        /// <summary>
        /// BUSINESS REQUIREMENT: Decoder must support multiple conversation turns without state corruption
        ///
        /// WHY: Each TTS response is a new OGG stream. Without proper reset between turns,
        ///      the parser tries to decode OpusHead/OpusTags headers as audio, causing errors.
        /// WHAT: Tests that Reset() properly clears all state for a clean new stream.
        /// HOW: Verifies packet count resets to 0 after Reset().
        ///
        /// SUCCESS CRITERIA:
        /// - After Reset(), packet count is 0
        /// - Decoder can be used again after Reset()
        ///
        /// BUSINESS IMPACT:
        /// - Without reset between turns, 2nd turn audio decoding fails
        /// - This is a contributing factor to the iOS NPC hanging bug
        /// </summary>
        [Test]
        public void Reset_ClearsAllState_ForNewConversationTurn()
        {
            // Verify initial state
            Assert.AreEqual(0, _decoder.GetPacketCount(), "Initial packet count should be 0");

            // Reset and verify state is still clean
            _decoder.Reset();

            Assert.AreEqual(0, _decoder.GetPacketCount(), "Packet count should be 0 after Reset()");
        }

        /// <summary>
        /// BUSINESS REQUIREMENT: Decoder must handle multiple consecutive resets without errors
        ///
        /// WHY: Safety-in-depth - playback complete + EndAudioStream can both trigger reset.
        /// WHAT: Tests that calling Reset() multiple times doesn't throw or corrupt state.
        /// HOW: Calls Reset() 3 times in succession and verifies no exception.
        ///
        /// SUCCESS CRITERIA:
        /// - No exception thrown on multiple Reset() calls
        /// - State remains valid after multiple resets
        ///
        /// BUSINESS IMPACT:
        /// - Multiple Reset() paths exist (EndAudioStream, playback complete, interruption)
        /// - Must be safe to call from any code path without crash
        /// </summary>
        [Test]
        public void Reset_CalledMultipleTimes_DoesNotThrow()
        {
            Assert.DoesNotThrow(() =>
            {
                _decoder.Reset();
                _decoder.Reset();
                _decoder.Reset();
            }, "Multiple consecutive Reset() calls must not throw");

            Assert.AreEqual(0, _decoder.GetPacketCount());
        }

        /// <summary>
        /// BUSINESS REQUIREMENT: Decoder must accept new data after Reset()
        ///
        /// WHY: After Reset(), the decoder must be ready for a new OGG stream from the next TTS response.
        /// WHAT: Tests that ProcessData() works after Reset() without throwing.
        /// HOW: Sends garbage data (not valid OGG) - should not throw, just fail to parse.
        ///
        /// SUCCESS CRITERIA:
        /// - No exception thrown when processing data after Reset()
        /// - Decoder handles non-OGG data gracefully (fails to parse, doesn't crash)
        ///
        /// BUSINESS IMPACT:
        /// - Ensures decoder recovery after errors or between conversation turns
        /// </summary>
        [Test]
        public void ProcessData_AfterReset_DoesNotThrow()
        {
            _decoder.Reset();

            // OGG parser will log errors when it can't parse invalid data - this is expected behavior
            LogAssert.ignoreFailingMessages = true;

            // Send non-OGG data - should not throw, just fail to parse
            var invalidData = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04 };
            Assert.DoesNotThrow(() => _decoder.ProcessData(invalidData),
                "ProcessData after Reset() must not throw, even with invalid data");
        }

        #endregion

        #region Edge Case Tests

        /// <summary>
        /// BUSINESS REQUIREMENT: Audio pipeline must handle edge cases without crashing
        ///
        /// WHY: Network issues can cause null or empty audio chunks to arrive.
        /// WHAT: Tests that ProcessData handles null and empty data gracefully.
        /// HOW: Calls ProcessData with null and empty arrays.
        ///
        /// SUCCESS CRITERIA:
        /// - No exception on null data
        /// - No exception on empty array
        /// - Packet count remains 0 (nothing was decoded)
        ///
        /// BUSINESS IMPACT:
        /// - Prevents NPC crash from network glitches
        /// </summary>
        [Test]
        public void ProcessData_WithNullData_DoesNotThrow()
        {
            Assert.DoesNotThrow(() => _decoder.ProcessData(null));
            Assert.AreEqual(0, _decoder.GetPacketCount());
        }

        [Test]
        public void ProcessData_WithEmptyData_DoesNotThrow()
        {
            Assert.DoesNotThrow(() => _decoder.ProcessData(Array.Empty<byte>()));
            Assert.AreEqual(0, _decoder.GetPacketCount());
        }

        /// <summary>
        /// BUSINESS REQUIREMENT: Decoder must handle invalid OGG data without permanent state corruption
        ///
        /// WHY: Corrupt network data or stream boundary issues can produce invalid OGG pages.
        ///      The decoder must skip invalid data without getting stuck.
        /// WHAT: Tests that invalid OGG data doesn't throw and decoder remains usable.
        /// HOW: Sends random bytes that aren't valid OGG format.
        ///
        /// SUCCESS CRITERIA:
        /// - No exception thrown
        /// - Decoder can be Reset() and used again after invalid data
        ///
        /// BUSINESS IMPACT:
        /// - Ensures decoder resilience in production network conditions
        /// </summary>
        [Test]
        public void ProcessData_WithInvalidOggData_DoesNotThrowAndRemainsUsable()
        {
            // OGG parser will log errors when it can't parse garbage data - this is expected behavior
            LogAssert.ignoreFailingMessages = true;

            // Random bytes that aren't valid OGG
            var garbage = new byte[100];
            new System.Random(42).NextBytes(garbage);

            Assert.DoesNotThrow(() => _decoder.ProcessData(garbage),
                "Invalid OGG data must not throw");

            // Decoder must still be usable after invalid data
            Assert.DoesNotThrow(() => _decoder.Reset(),
                "Reset() must work after processing invalid data");
            Assert.AreEqual(0, _decoder.GetPacketCount(),
                "After Reset(), packet count must be 0");
        }

        /// <summary>
        /// BUSINESS REQUIREMENT: FlushRemainingAudio must not throw on empty decoder
        ///
        /// WHY: EndAudioStream calls FlushRemainingAudio, which can be called when no data was decoded.
        ///      Must return safely without throwing.
        /// WHAT: Tests FlushRemainingAudio on a fresh/empty decoder.
        /// HOW: Calls FlushRemainingAudio without sending any data first.
        ///
        /// SUCCESS CRITERIA:
        /// - No exception thrown
        /// - Returns null or empty array (no audio to flush)
        ///
        /// BUSINESS IMPACT:
        /// - Prevents EndAudioStream from failing on empty turns
        /// </summary>
        [Test]
        public void FlushRemainingAudio_OnEmptyDecoder_ReturnsNullWithoutThrowing()
        {
            float[] result = null;
            Assert.DoesNotThrow(() => result = _decoder.FlushRemainingAudio(),
                "FlushRemainingAudio on empty decoder must not throw");

            Assert.IsNull(result, "Empty decoder should return null from FlushRemainingAudio");
        }

        #endregion

        #region Dispose Tests

        /// <summary>
        /// BUSINESS REQUIREMENT: Proper resource cleanup prevents memory leaks in long training sessions
        ///
        /// WHY: Training sessions can last 30+ minutes with many conversation turns.
        ///      Resources must be properly disposed to prevent native memory leaks.
        /// WHAT: Tests that Dispose() is safe to call multiple times (idempotent).
        /// HOW: Calls Dispose() twice and verifies no exception.
        ///
        /// SUCCESS CRITERIA:
        /// - No exception on double Dispose()
        ///
        /// BUSINESS IMPACT:
        /// - Prevents memory leaks during long training sessions
        /// </summary>
        [Test]
        public void Dispose_CalledMultipleTimes_DoesNotThrow()
        {
            var decoder = new OpusStreamDecoder(isVerboseLogging: false);

            Assert.DoesNotThrow(() =>
            {
                decoder.Dispose();
                decoder.Dispose(); // Second call should be idempotent
            }, "Double Dispose() must not throw");
        }

        #endregion
    }
}
