using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using OpusSharp.Core;
using Tsc.AIBridge.Audio.Codecs;
using Tsc.AIBridge.Audio.Playback;
using Tsc.AIBridge.Audio.Processing;
using UnityEngine;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: NPC audio chunks that arrive AFTER the safety-net timeout
    /// must not be silently dropped. Either they are still decoded and played (correct
    /// behaviour for "stream is finishing up"), or the loss is loudly reported so it
    /// can be observed in production.
    ///
    /// WHY: Real production session 2026-05-13 17:42 — backend confirmed sending 30748
    /// bytes of OGG/Opus for a 4046ms response, but the client played back ~1,99s
    /// (95688 samples). The client log shows:
    /// <code>
    /// 17:42:29.379 Auto-detected playback complete — safety-net timeout (1,87s with no data, no server signal)
    /// 17:42:30.881 Pipeline: Idle → Streaming   (new stream)
    /// 17:42:30.882 Server signalled end-of-audio-stream  (immediate, no audio in between)
    /// </code>
    /// We've eliminated the parser and decoder as suspects (separate fixtures). The next
    /// most likely cause is this: the safety-net timeout triggers <c>StopPlayback</c> →
    /// <c>OnPlaybackComplete</c> → <c>NpcClientBase.ResetAudioStateForNextTurn</c> →
    /// <c>audioProcessor.EndAudioStream()</c> → <c>_isStreamingAudio = false</c>.
    /// After that, the guard at <see cref="AudioStreamProcessor.ProcessReceivedAudio"/>
    /// regel 471 (<c>if (!_isStreamingAudio) return;</c>) silently discards every late chunk.
    /// No log (only under verbose), no counter, no error event.
    ///
    /// WHAT: Drive the AudioStreamProcessor through the exact lifecycle the safety-net
    /// causes — open stream, feed half the audio, call EndAudioStream, feed the rest —
    /// and measure how many PCM samples the decoder produced. If late chunks decode,
    /// the total matches the full stream. If they are dropped, we get the production
    /// symptom: a fraction of the expected audio.
    ///
    /// HOW: Use real OpusEncoder (OpusSharp.Core) to build a 200-frame OGG/Opus stream,
    /// then split the raw bytes at the midpoint. Feed the first half, simulate safety-net
    /// teardown via <c>EndAudioStream</c>, then feed the second half. Count decoded
    /// samples via the StreamingAudioPlayer events.
    ///
    /// SUCCESS CRITERIA:
    /// - Baseline: feeding the full stream without interruption decodes ≥ (200×960)−312 samples
    /// - Race scenario: late chunks should NOT be silently lost. Two acceptable outcomes:
    ///   a) Late chunks decode → total matches baseline
    ///   b) Late chunks rejected loudly (log/counter/event visible to test)
    /// - The current code does neither — late chunks vanish silently. The race test
    ///   asserts the production-observed loss (~50%), proving the bug.
    ///
    /// BUSINESS IMPACT:
    /// - Without this guard fix, every safety-net timeout in production silently truncates
    ///   the rest of the NPC response — exactly the complaint users have for Voxtral/Cartesia
    ///   TTS where backend chunk-rate occasionally dips and the client safety-net triggers early
    /// </summary>
    [TestFixture]
    public class AudioStreamProcessorLateChunkRaceTests
    {
        private const int SampleRate = 48000;
        private const int Channels = 1;
        private const int FrameSize = 960; // 20ms at 48kHz
        private const int DefaultPreSkip = 312;
        private const int TotalFrames = 200;

        private const byte HeaderTypeBos = 0x02;
        private const byte HeaderTypeEos = 0x04;
        private const byte HeaderTypeNone = 0x00;
        private static readonly byte[] OggMagic = Encoding.ASCII.GetBytes("OggS");
        private static readonly byte[] OpusHeadMagic = Encoding.ASCII.GetBytes("OpusHead");
        private static readonly byte[] OpusTagsMagic = Encoding.ASCII.GetBytes("OpusTags");

        private GameObject _playerGameObject;
        private StreamingAudioPlayer _player;
        private AudioStreamProcessor _processor;

        [SetUp]
        public void SetUp()
        {
            _playerGameObject = new GameObject("LateChunkRaceTestPlayer");
            _playerGameObject.AddComponent<AudioSource>();
            _player = _playerGameObject.AddComponent<StreamingAudioPlayer>();
            _processor = new AudioStreamProcessor(_player, opusBitrate: 24000, bufferDuration: 0.5f, isVerboseLogging: false);
        }

        [TearDown]
        public void TearDown()
        {
            _processor?.Dispose();
            if (_playerGameObject != null)
                UnityEngine.Object.DestroyImmediate(_playerGameObject);
        }

        /// <summary>
        /// Baseline: the full stream fed without interruption should decode all expected samples.
        /// This pins the expected sample count we'll compare the race scenario against.
        /// </summary>
        [Test]
        public void Baseline_FullStream_NoInterruption_DecodesAllSamples()
        {
            var oggBytes = BuildRealOpusOggStream(TotalFrames, framesPerPage: 1);

            var samplesDecoded = RunFullStream(oggBytes);

            var expected = TotalFrames * FrameSize - DefaultPreSkip;
            Assert.AreEqual(expected, samplesDecoded,
                $"Baseline must decode every frame. Got {samplesDecoded}, expected {expected}.");
        }

        /// <summary>
        /// THE FIX: late chunks arriving within the resume window after a safety-net-driven
        /// EndAudioStream MUST be decoded and contribute to the audio output. Previously they
        /// were silently dropped because <c>_isStreamingAudio == false</c> guarded
        /// <c>ProcessReceivedAudio</c> and the chunk was discarded without log or counter.
        ///
        /// Expected behaviour: total decoded samples after late chunks ≈ baseline (full stream).
        /// </summary>
        [Test]
        public void RaceScenario_LateChunksAfterSafetyNet_AreNotDropped()
        {
            var oggBytes = BuildRealOpusOggStream(TotalFrames, framesPerPage: 1);

            // Split bytes on a page boundary near the midpoint so the first half parses cleanly.
            var splitOffset = FindPageBoundaryNearMidpoint(oggBytes);
            var firstHalf = oggBytes.Take(splitOffset).ToArray();
            var secondHalf = oggBytes.Skip(splitOffset).ToArray();

            _processor.StartAudioStream(isOpus: true, sampleRate: SampleRate);
            _processor.ProcessReceivedAudio(firstHalf);

            var samplesAfterFirstHalf = TotalSamplesInPlayerBuffer();
            Assert.Greater(samplesAfterFirstHalf, 0, "First-half processing must produce samples");

            // Simulate the safety-net teardown — exactly what NpcClientBase does when the
            // 1,87s timeout fires while the server is still streaming.
            _processor.EndAudioStream();

            // Snapshot dropped-bytes counter BEFORE late chunks. The resume logic must NOT
            // increment this counter — late chunks should be accepted, not dropped.
            var droppedBeforeLate = _processor.DroppedBytesAfterStreamEnd;

            // Late chunks arrive (server was still streaming). With the fix, the processor
            // resumes the stream (without resetting decoder state) so the mid-stream OGG
            // pages decode cleanly and contribute to the player buffer.
            _processor.ProcessReceivedAudio(secondHalf);

            var samplesAfterLateChunks = TotalSamplesInPlayerBuffer();
            var droppedAfterLate = _processor.DroppedBytesAfterStreamEnd;

            Assert.Greater(samplesAfterLateChunks, samplesAfterFirstHalf,
                "Late chunks must add samples to the player buffer. If equal, AudioStreamProcessor " +
                "is still dropping bytes after EndAudioStream — the race-condition fix is broken.");

            Assert.AreEqual(droppedBeforeLate, droppedAfterLate,
                "Late chunks were dropped silently! DroppedBytesAfterStreamEnd increased, meaning " +
                "the resume path did not engage and bytes were lost.");

            // Strong condition: total should reach baseline (all 200 frames decoded).
            var baselineExpected = TotalFrames * FrameSize - DefaultPreSkip;
            Assert.AreEqual(baselineExpected, samplesAfterLateChunks,
                $"Late-chunk resume must recover the full audio: expected {baselineExpected} samples, " +
                $"got {samplesAfterLateChunks}. Anything less means part of the production audio is " +
                $"still being lost on the late path.");
        }

        /// <summary>
        /// Chunks that arrive WAY too late (outside the resume window) should still be
        /// dropped — but loudly. The counter must reflect the drop so production can be
        /// monitored, instead of silent loss like before.
        /// </summary>
        [Test]
        public void LateChunks_OutsideResumeWindow_AreCountedAsDropped()
        {
            var oggBytes = BuildRealOpusOggStream(TotalFrames, framesPerPage: 1);

            _processor.StartAudioStream(isOpus: true, sampleRate: SampleRate);
            _processor.ProcessReceivedAudio(oggBytes);
            _processor.EndAudioStream();

            // Simulate "much later" by forcing the resume window check to fail. We do that
            // by exposing a public test seam: EndAudioStream sets a timestamp; we read the
            // counter before/after a delayed-feed attempt.
            var droppedBefore = _processor.DroppedBytesAfterStreamEnd;

            // Push the timestamp far into the past via the test seam so the resume window
            // is exceeded. (Production sets the timestamp inside EndAudioStream.)
            _processor.ForceLastEndTimeForTest(DateTime.UtcNow.AddSeconds(-30));

            var lateChunk = new byte[123];
            _processor.ProcessReceivedAudio(lateChunk);

            var droppedAfter = _processor.DroppedBytesAfterStreamEnd;

            Assert.AreEqual(droppedBefore + lateChunk.Length, droppedAfter,
                "Chunks outside the resume window must be counted as dropped so production " +
                "can monitor silent audio loss. Counter did not increment as expected.");
        }

        // ---------- Helpers: decode counting via player buffer ----------

        /// <summary>
        /// Reads <see cref="StreamingAudioPlayer.HasBufferedAudio"/> + buffer level to derive
        /// total samples the decoder pushed to the player. Sums what's still buffered with what
        /// would already have drained — but in EditMode tests, no audio thread runs, so all
        /// pushed samples remain in the queue.
        /// </summary>
        private int TotalSamplesInPlayerBuffer()
        {
            // BufferLevel is in seconds at the player's sample rate.
            // Convert back to sample count for an exact integer.
            // _audioBuffer.Count is the most direct measure but isn't public; BufferLevel uses
            // sampleRate=48000 by default so BufferLevel * 48000 ≈ Count.
            return Mathf.RoundToInt(_player.BufferLevel * SampleRate);
        }

        private int RunFullStream(byte[] oggBytes)
        {
            _processor.StartAudioStream(isOpus: true, sampleRate: SampleRate);
            _processor.ProcessReceivedAudio(oggBytes);
            // Mirror NpcClientBase: flush remaining samples by calling EndAudioStream
            // (this is the clean-shutdown path with the EOS page already received).
            _processor.EndAudioStream();
            return TotalSamplesInPlayerBuffer();
        }

        // ---------- OGG byte-builder with real Opus packets ----------

        private static byte[] BuildRealOpusOggStream(int frameCount, int framesPerPage)
        {
            var opusPackets = EncodeOpusFrames(frameCount);

            using var stream = new MemoryStream();
            uint pageSeq = 0;
            const uint serial = 0xDECADEu;
            long granule = 0;

            WriteOggPage(stream, HeaderTypeBos, 0, serial, pageSeq++,
                segmentLengths: new byte[] { (byte)BuildOpusHeadPayload().Length },
                payload: BuildOpusHeadPayload());
            WriteOggPage(stream, HeaderTypeNone, 0, serial, pageSeq++,
                segmentLengths: new byte[] { (byte)BuildOpusTagsPayload().Length },
                payload: BuildOpusTagsPayload());

            for (var i = 0; i < opusPackets.Count; i += framesPerPage)
            {
                var packetsThisPage = Math.Min(framesPerPage, opusPackets.Count - i);
                var segmentTable = new List<byte>();
                var combinedPayload = new List<byte>();

                for (var j = 0; j < packetsThisPage; j++)
                {
                    var packet = opusPackets[i + j];
                    var remaining = packet.Length;
                    while (remaining >= 255)
                    {
                        segmentTable.Add(255);
                        remaining -= 255;
                    }
                    segmentTable.Add((byte)remaining);
                    combinedPayload.AddRange(packet);
                }

                granule += FrameSize * packetsThisPage;
                WriteOggPage(stream, HeaderTypeNone, granule, serial, pageSeq++,
                    segmentTable.ToArray(), combinedPayload.ToArray());
            }

            WriteOggPage(stream, HeaderTypeEos, granule, serial, pageSeq,
                segmentLengths: Array.Empty<byte>(), payload: Array.Empty<byte>());

            return stream.ToArray();
        }

        private static List<byte[]> EncodeOpusFrames(int frameCount)
        {
            var encoder = new OpusEncoder(SampleRate, Channels, OpusPredefinedValues.OPUS_APPLICATION_VOIP);
            var packets = new List<byte[]>(frameCount);

            const float frequency = 440f;
            const float amplitude = 0.3f;
            var pcmFrame = new float[FrameSize * Channels];
            var encodeBuffer = new byte[4000];

            for (var f = 0; f < frameCount; f++)
            {
                for (var s = 0; s < FrameSize; s++)
                {
                    var t = (f * FrameSize + s) / (float)SampleRate;
                    pcmFrame[s] = amplitude * (float)Math.Sin(2 * Math.PI * frequency * t);
                }

                var encodedSize = encoder.Encode(pcmFrame, FrameSize, encodeBuffer, encodeBuffer.Length);
                var packet = new byte[encodedSize];
                Array.Copy(encodeBuffer, packet, encodedSize);
                packets.Add(packet);
            }

            return packets;
        }

        /// <summary>
        /// Scans forward from the midpoint of <paramref name="bytes"/> for an "OggS" magic and
        /// returns that offset. Splitting on a page boundary keeps the first half a complete,
        /// parseable prefix and the second half a valid mid-stream continuation.
        /// </summary>
        private static int FindPageBoundaryNearMidpoint(byte[] bytes)
        {
            var midpoint = bytes.Length / 2;
            for (var i = midpoint; i + 3 < bytes.Length; i++)
            {
                if (bytes[i] == 'O' && bytes[i + 1] == 'g' && bytes[i + 2] == 'g' && bytes[i + 3] == 'S')
                {
                    return i;
                }
            }
            return midpoint; // fall back if nothing found (shouldn't happen with our shape)
        }

        // ---------- OGG page builder helpers ----------

        private static byte[] BuildOpusHeadPayload()
        {
            var p = new byte[19];
            Array.Copy(OpusHeadMagic, 0, p, 0, 8);
            p[8] = 1; p[9] = (byte)Channels;
            p[10] = (byte)(DefaultPreSkip & 0xFF); p[11] = (byte)((DefaultPreSkip >> 8) & 0xFF);
            p[12] = (byte)(SampleRate & 0xFF); p[13] = (byte)((SampleRate >> 8) & 0xFF);
            p[14] = (byte)((SampleRate >> 16) & 0xFF); p[15] = (byte)((SampleRate >> 24) & 0xFF);
            p[16] = 0; p[17] = 0; p[18] = 0;
            return p;
        }

        private static byte[] BuildOpusTagsPayload()
        {
            const string vendor = "RaceTest";
            var vendorBytes = Encoding.UTF8.GetBytes(vendor);
            var p = new byte[8 + 4 + vendorBytes.Length + 4];
            Array.Copy(OpusTagsMagic, 0, p, 0, 8);
            p[8] = (byte)vendorBytes.Length;
            Array.Copy(vendorBytes, 0, p, 12, vendorBytes.Length);
            return p;
        }

        private static void WriteOggPage(MemoryStream output, byte headerType, long granule, uint serial, uint sequence,
            byte[] segmentLengths, byte[] payload)
        {
            var pageSize = 27 + segmentLengths.Length + payload.Length;
            var page = new byte[pageSize];
            var offset = 0;

            Array.Copy(OggMagic, 0, page, offset, 4); offset += 4;
            page[offset++] = 0; page[offset++] = headerType;
            BitConverter.GetBytes(granule).CopyTo(page, offset); offset += 8;
            BitConverter.GetBytes(serial).CopyTo(page, offset); offset += 4;
            BitConverter.GetBytes(sequence).CopyTo(page, offset); offset += 4;
            page[offset++] = 0; page[offset++] = 0; page[offset++] = 0; page[offset++] = 0;
            page[offset++] = (byte)segmentLengths.Length;
            Array.Copy(segmentLengths, 0, page, offset, segmentLengths.Length); offset += segmentLengths.Length;
            Array.Copy(payload, 0, page, offset, payload.Length);

            var crc = ComputeOggCrc(page);
            BitConverter.GetBytes(crc).CopyTo(page, 22);

            output.Write(page, 0, page.Length);
        }

        private static readonly uint[] OggCrcTable = BuildOggCrcTable();

        private static uint[] BuildOggCrcTable()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                var r = i << 24;
                for (var j = 0; j < 8; j++)
                {
                    if ((r & 0x80000000U) != 0) r = (r << 1) ^ 0x04C11DB7U;
                    else r <<= 1;
                }
                t[i] = r;
            }
            return t;
        }

        private static uint ComputeOggCrc(byte[] page)
        {
            uint crc = 0;
            foreach (var b in page) crc = (crc << 8) ^ OggCrcTable[((crc >> 24) & 0xFF) ^ b];
            return crc;
        }
    }
}
