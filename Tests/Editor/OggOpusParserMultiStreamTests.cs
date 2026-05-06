using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;
using Tsc.AIBridge.Audio.Codecs;
using UnityEngine.TestTools;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: Unity's OggOpusParser must handle multiple concatenated OGG logical
    /// streams on a single input (the post-2026-05-06 voxtral / cartesia flow where every TtsSentence
    /// produces its own self-contained OGG stream — BOS-flagged OpusHead, audio data pages, EOS-flagged
    /// final page — and these streams arrive back-to-back over the same WebSocket binary channel).
    ///
    /// WHY: Production incident 2026-05-06. Voxtral content-creator testing showed dozens of
    /// "Invalid OpusHead signature: h..." errors mid-turn plus accompanying audio_gap Slack alerts.
    /// The byte 0x68 (= 'h') is the typical Opus TOC byte for 24 kHz mono SILK config, i.e. an audio
    /// packet's first byte. The error meant the parser was treating sentence #2's fresh OpusHead
    /// packet as if it were an audio packet — i.e. the parser had not detected the new logical
    /// stream boundary. The legacy parser ignored the BOS flag entirely (constants commented out)
    /// and treated sentence #2's page sequence-0 as a "rewind" of sentence #1's page 0.
    ///
    /// WHAT: This fixture concatenates two valid OGG/Opus streams in a single MemoryStream and
    /// drives the parser through them, asserting that every audio packet from BOTH streams is
    /// returned cleanly, with no error messages and no packet bytes containing the OpusHead /
    /// OpusTags magic signatures (which would mean the parser leaked header bytes into the
    /// audio-packet output).
    ///
    /// HOW: Builds streams byte-for-byte from minimal OGG primitives — capture pattern, header
    /// type flags (BOS / EOS), granule, serial, sequence, and a CRC-32 over the page bytes.
    /// Every audio packet payload is one specific marker byte (0xAA for stream 1, 0xBB for stream 2)
    /// so the assertion can verify which stream each packet came from.
    ///
    /// SUCCESS CRITERIA:
    /// - Audio packets from BOTH streams are returned by ReadNextOpusPacket
    /// - No "Invalid OpusHead signature" log messages
    /// - No packet payload contains the bytes "OpusHead" or "OpusTags"
    /// - Stream-2 packets are returned only after stream-1 packets (order preserved)
    ///
    /// BUSINESS IMPACT:
    /// - Without the multi-stream fix, voxtral / cartesia mid-turn audio drops out the moment
    ///   sentence #2 starts and Unity console floods with parser errors. The Slack
    ///   #log-orchestrator-medium channel got hit with audio_gap alerts because the player
    ///   genuinely heard silence (audio packets dropped before reaching the AudioSource).
    /// </summary>
    [TestFixture]
    public class OggOpusParserMultiStreamTests
    {
        private const byte HeaderTypeBos = 0x02;
        private const byte HeaderTypeEos = 0x04;
        private const byte HeaderTypeNone = 0x00;

        private static readonly byte[] OggMagic = Encoding.ASCII.GetBytes("OggS");
        private static readonly byte[] OpusHeadMagic = Encoding.ASCII.GetBytes("OpusHead");
        private static readonly byte[] OpusTagsMagic = Encoding.ASCII.GetBytes("OpusTags");

        /// <summary>
        /// REGRESSION GUARD voor de production incident 2026-05-06.
        ///
        /// Twee zelfstandige OGG/Opus streams achter elkaar (zoals voxtral nu naar Unity stuurt
        /// in een multi-sentence turn). Stream 1 met audio-marker 0xAA, stream 2 met audio-marker
        /// 0xBB. De parser moet ALLE audio packets uit beide streams returnen, ZONDER ergens
        /// "OpusHead" / "OpusTags" magic bytes door te lekken naar de audio-packet output.
        /// </summary>
        [Test]
        public void ReadNextOpusPacket_TwoConcatenatedStreams_ReturnsAllAudioPackets_NoHeaderLeakage()
        {
            // Arrange — concatenate two complete OGG/Opus streams.
            using var stream = new MemoryStream();
            WriteCompleteOggOpusStream(stream, serial: 0x11111111u, audioMarker: 0xAA, audioFrameCount: 3);
            WriteCompleteOggOpusStream(stream, serial: 0x22222222u, audioMarker: 0xBB, audioFrameCount: 2);
            stream.Position = 0;

            var parser = new OggOpusParser();
            var initialised = parser.Initialize(stream, isVerboseLogging: false);
            Assert.IsTrue(initialised, "parser must initialise on a valid concatenated OGG stream");

            // Act — drain every packet the parser is willing to yield. Iteration cap protects
            // against an infinite loop in case of a parser bug.
            var packetBuffer = new byte[8192];
            var collected = new List<byte[]>();
            var iteration = 0;
            while (iteration++ < 100)
            {
                var size = parser.ReadNextOpusPacket(packetBuffer);
                if (size <= 0) break;
                var copy = new byte[size];
                Array.Copy(packetBuffer, copy, size);
                collected.Add(copy);
            }
            Assert.Less(iteration, 100, "parser entered runaway loop — multi-stream handling regressed");

            // Assert — every audio frame from both streams is present, and only audio markers
            // appear in the packet payloads (no header magic leaked through).
            var stream1Packets = collected.FindAll(p => p.Length > 0 && p[0] == 0xAA);
            var stream2Packets = collected.FindAll(p => p.Length > 0 && p[0] == 0xBB);

            Assert.AreEqual(3, stream1Packets.Count, "stream 1's three audio frames must come through");
            Assert.AreEqual(2, stream2Packets.Count, "stream 2's two audio frames must come through");

            // Strict header-leakage check: no audio packet may contain header magic.
            foreach (var packet in collected)
            {
                var asAscii = Encoding.ASCII.GetString(packet);
                Assert.IsFalse(asAscii.Contains("OpusHead"),
                    "an audio packet leaked OpusHead bytes — parser failed to absorb headers");
                Assert.IsFalse(asAscii.Contains("OpusTags"),
                    "an audio packet leaked OpusTags bytes — parser failed to absorb tag pages");
            }

            // Order: every stream-1 packet must come before every stream-2 packet.
            var firstStream2Index = collected.FindIndex(p => p.Length > 0 && p[0] == 0xBB);
            var lastStream1Index = collected.FindLastIndex(p => p.Length > 0 && p[0] == 0xAA);
            Assert.Less(lastStream1Index, firstStream2Index,
                "all stream-1 packets must be returned before any stream-2 packet");
        }

        /// <summary>
        /// Sanity: a single OGG stream still parses correctly (regression-guard against the BOS
        /// detection accidentally breaking the simple, single-stream case that ElevenLabs uses).
        /// </summary>
        [Test]
        public void ReadNextOpusPacket_SingleStream_StillWorksAfterMultiStreamFix()
        {
            using var stream = new MemoryStream();
            WriteCompleteOggOpusStream(stream, serial: 0xCAFEBABEu, audioMarker: 0xCC, audioFrameCount: 4);
            stream.Position = 0;

            var parser = new OggOpusParser();
            Assert.IsTrue(parser.Initialize(stream));

            var packetBuffer = new byte[8192];
            var audioCount = 0;
            while (true)
            {
                var size = parser.ReadNextOpusPacket(packetBuffer);
                if (size <= 0) break;
                if (packetBuffer[0] == 0xCC) audioCount++;
            }

            Assert.AreEqual(4, audioCount, "single-stream parsing must still deliver every audio frame");
        }

        // ---------- Minimal OGG/Opus stream builder ----------

        private static void WriteCompleteOggOpusStream(MemoryStream output, uint serial, byte audioMarker, int audioFrameCount)
        {
            uint pageSeq = 0;
            long granule = 0;
            const int samplesPerFrame = 480; // 20 ms at 24 kHz

            // Page 0: OpusHead (BOS)
            var opusHeadPayload = BuildOpusHeadPayload();
            WriteOggPage(output, HeaderTypeBos, granulePosition: 0, serial, pageSeq++, opusHeadPayload);

            // Page 1: OpusTags
            var opusTagsPayload = BuildOpusTagsPayload();
            WriteOggPage(output, HeaderTypeNone, granulePosition: 0, serial, pageSeq++, opusTagsPayload);

            // Audio data pages — each carries a single packet starting with the audio-marker byte
            // followed by enough padding so the OpusDecoder layer above doesn't reject for being
            // too short. The PARSER doesn't decode Opus; it just shovels packet bytes through, so
            // any non-empty payload works for this fixture.
            for (var i = 0; i < audioFrameCount; i++)
            {
                granule += samplesPerFrame;
                var audioPayload = new byte[64];
                audioPayload[0] = audioMarker;
                for (var j = 1; j < audioPayload.Length; j++) audioPayload[j] = (byte)((i * 17 + j) & 0xFF);
                WriteOggPage(output, HeaderTypeNone, granule, serial, pageSeq++, audioPayload);
            }

            // Final page: EOS sentinel with empty payload (matches OggOpusWriter.WriteEosPage on the server).
            WriteOggPage(output, HeaderTypeEos, granule, serial, pageSeq, payload: Array.Empty<byte>());
        }

        private static byte[] BuildOpusHeadPayload()
        {
            // OpusHead spec layout: "OpusHead" + version(1) + channels(1) + preSkip(2 LE)
            //   + inputSampleRate(4 LE) + outputGain(2 LE) + channelMappingFamily(1) = 19 bytes
            var p = new byte[19];
            Array.Copy(OpusHeadMagic, 0, p, 0, 8);
            p[8] = 1;     // version
            p[9] = 1;     // channels
            p[10] = 0x00; // preSkip lo
            p[11] = 0x0F; // preSkip hi (3840 = 0x0F00)
            p[12] = 0xC0; // input sample rate lo (24000 = 0x5DC0)
            p[13] = 0x5D;
            p[14] = 0x00;
            p[15] = 0x00;
            p[16] = 0x00; // output gain lo
            p[17] = 0x00; // output gain hi
            p[18] = 0x00; // channel mapping family
            return p;
        }

        private static byte[] BuildOpusTagsPayload()
        {
            // "OpusTags" + 4-byte vendor length + vendor string + 4-byte comment count (0)
            const string vendor = "TestVendor";
            var vendorBytes = Encoding.UTF8.GetBytes(vendor);
            var p = new byte[8 + 4 + vendorBytes.Length + 4];
            Array.Copy(OpusTagsMagic, 0, p, 0, 8);
            p[8] = (byte)vendorBytes.Length;
            p[9] = 0; p[10] = 0; p[11] = 0;
            Array.Copy(vendorBytes, 0, p, 12, vendorBytes.Length);
            // last 4 bytes are zero comment count
            return p;
        }

        private static void WriteOggPage(MemoryStream output, byte headerType, long granulePosition, uint serial, uint sequence, byte[] payload)
        {
            // Build segment table: for any payload < 255 bytes one entry equals payload length.
            // Empty payload → zero segments (matches the EOS-only sentinel pages from
            // OggOpusWriter.WriteEosPage on the server).
            byte segmentCount;
            byte[] segmentTable;
            if (payload.Length == 0)
            {
                segmentCount = 0;
                segmentTable = Array.Empty<byte>();
            }
            else
            {
                segmentCount = 1;
                segmentTable = new[] { (byte)payload.Length };
            }

            var pageSize = 27 + segmentTable.Length + payload.Length;
            var page = new byte[pageSize];
            var offset = 0;

            Array.Copy(OggMagic, 0, page, offset, 4); offset += 4;
            page[offset++] = 0; // OGG version
            page[offset++] = headerType;
            BitConverter.GetBytes(granulePosition).CopyTo(page, offset); offset += 8;
            BitConverter.GetBytes(serial).CopyTo(page, offset); offset += 4;
            BitConverter.GetBytes(sequence).CopyTo(page, offset); offset += 4;
            // CRC field (4 bytes) — set to zero, recompute below
            page[offset++] = 0; page[offset++] = 0; page[offset++] = 0; page[offset++] = 0;
            page[offset++] = segmentCount;
            Array.Copy(segmentTable, 0, page, offset, segmentTable.Length); offset += segmentTable.Length;
            Array.Copy(payload, 0, page, offset, payload.Length);

            var crc = ComputeOggCrc(page);
            BitConverter.GetBytes(crc).CopyTo(page, 22);

            output.Write(page, 0, page.Length);
        }

        // OGG uses a CRC-32 with polynomial 0x04C11DB7, no reflection, no XOR-out, init=0.
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
