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
    /// Edge-case coverage for the rewritten <see cref="OggOpusParser"/>.
    /// The single-stream and two-stream basics live in
    /// <see cref="OggOpusParserMultiStreamTests"/>; this fixture pins the
    /// less-obvious bits of the OGG/Opus spec the new state machine must honour:
    /// <list type="bullet">
    /// <item><description>A packet that spans two pages (segment-table 255-rule
    /// and continued-flag).</description></item>
    /// <item><description>A page that contains multiple complete packets
    /// (the segment table emits more than one packet boundary).</description></item>
    /// <item><description>Page-sequence continuity per logical stream: gaps
    /// produce a warning but the parser keeps going; rewinds are silently
    /// tolerated; sequences from a different serial are dropped.</description></item>
    /// <item><description>Streams that arrive incrementally (caller appends data
    /// over time): the parser must restore its read position when a page is
    /// incomplete and pick up exactly where it left off.</description></item>
    /// <item><description>EOS without a following BOS: parser cleanly returns 0
    /// and stays in <c>ExpectingNewLogicalStream</c> ready for whatever comes
    /// next (or never comes).</description></item>
    /// <item><description>Garbage bytes between logical streams: the parser
    /// recovers by scanning forward to the next "OggS" marker.</description></item>
    /// </list>
    /// </summary>
    [TestFixture]
    public class OggOpusParserStateMachineTests
    {
        private const byte HeaderTypeBos = 0x02;
        private const byte HeaderTypeEos = 0x04;
        private const byte HeaderTypeContinued = 0x01;
        private const byte HeaderTypeNone = 0x00;

        private static readonly byte[] OggMagic = Encoding.ASCII.GetBytes("OggS");
        private static readonly byte[] OpusHeadMagic = Encoding.ASCII.GetBytes("OpusHead");
        private static readonly byte[] OpusTagsMagic = Encoding.ASCII.GetBytes("OpusTags");

        // ---------- Continued / multi-segment packets ----------

        /// <summary>
        /// A 510-byte packet (= two full 255-segment chunks) inside one page must
        /// be reassembled into one packet of length 510. The segment table
        /// <c>[255, 255, 0]</c> is the spec-mandated way to express
        /// "exactly-multiple-of-255-bytes packet".
        /// </summary>
        [Test]
        public void Page_With_Exact510BytePacket_YieldsOneAssembledPacket()
        {
            using var stream = new MemoryStream();
            uint pageSeq = 0;
            const uint serial = 0xAAAA_AAAA;

            // Headers
            WriteOggPageWithSinglePacket(stream, HeaderTypeBos, 0, serial, pageSeq++, BuildOpusHeadPayload());
            WriteOggPageWithSinglePacket(stream, HeaderTypeNone, 0, serial, pageSeq++, BuildOpusTagsPayload());

            // Audio packet of 510 bytes — segment table must be [255, 255, 0]
            var bigPacket = new byte[510];
            for (var i = 0; i < bigPacket.Length; i++) bigPacket[i] = (byte)(0xCC ^ i);
            WriteOggPage(stream, HeaderTypeNone, 480, serial, pageSeq++,
                segmentLengths: new byte[] { 255, 255, 0 }, payload: bigPacket);

            stream.Position = 0;

            var parser = new OggOpusParser();
            Assert.IsTrue(parser.Initialize(stream));

            var buffer = new byte[8192];
            var size = parser.ReadNextOpusPacket(buffer);

            Assert.AreEqual(510, size, "510-byte packet expressed as [255,255,0] segments must reassemble as one 510-byte packet");
            for (var i = 0; i < 510; i++)
            {
                Assert.AreEqual((byte)(0xCC ^ i), buffer[i], $"reassembled byte {i} must match original");
            }
            Assert.AreEqual(0, parser.ReadNextOpusPacket(buffer), "no further packets expected");
        }

        /// <summary>
        /// A packet split across two pages: page 1 has segment-table <c>[255]</c>
        /// (continues, no terminator) and the body holds the first 255 bytes;
        /// page 2 has the continuation flag set and segment-table <c>[100]</c>
        /// completing the packet. Result: one 355-byte packet.
        /// </summary>
        [Test]
        public void Packet_SpanningTwoPages_IsStitchedAcrossContinuationFlag()
        {
            using var stream = new MemoryStream();
            uint pageSeq = 0;
            const uint serial = 0xBBBB_BBBB;

            // Headers
            WriteOggPageWithSinglePacket(stream, HeaderTypeBos, 0, serial, pageSeq++, BuildOpusHeadPayload());
            WriteOggPageWithSinglePacket(stream, HeaderTypeNone, 0, serial, pageSeq++, BuildOpusTagsPayload());

            // First half of the audio packet (255 bytes)
            var firstHalf = new byte[255];
            for (var i = 0; i < 255; i++) firstHalf[i] = (byte)(0x10 + i);
            WriteOggPage(stream, HeaderTypeNone, 0, serial, pageSeq++,
                segmentLengths: new byte[] { 255 }, payload: firstHalf);

            // Continuation: 100 bytes more
            var secondHalf = new byte[100];
            for (var i = 0; i < 100; i++) secondHalf[i] = (byte)(0xA0 + i);
            WriteOggPage(stream, HeaderTypeContinued, 480, serial, pageSeq++,
                segmentLengths: new byte[] { 100 }, payload: secondHalf);

            stream.Position = 0;

            var parser = new OggOpusParser();
            Assert.IsTrue(parser.Initialize(stream));

            var buffer = new byte[8192];
            var size = parser.ReadNextOpusPacket(buffer);

            Assert.AreEqual(355, size, "split packet must be reassembled to 255 + 100 bytes");
            for (var i = 0; i < 255; i++) Assert.AreEqual((byte)(0x10 + i), buffer[i], $"first-half byte {i}");
            for (var i = 0; i < 100; i++) Assert.AreEqual((byte)(0xA0 + i), buffer[255 + i], $"second-half byte {i}");
        }

        // ---------- Multiple packets per page ----------

        /// <summary>
        /// One page can carry multiple complete packets back-to-back.
        /// Segment-table <c>[50, 70, 30]</c> means three packets of those exact
        /// sizes in the payload, one after another.
        /// </summary>
        [Test]
        public void Page_With_ThreePackets_YieldsThreePacketsInOrder()
        {
            using var stream = new MemoryStream();
            uint pageSeq = 0;
            const uint serial = 0xCCCC_CCCC;

            WriteOggPageWithSinglePacket(stream, HeaderTypeBos, 0, serial, pageSeq++, BuildOpusHeadPayload());
            WriteOggPageWithSinglePacket(stream, HeaderTypeNone, 0, serial, pageSeq++, BuildOpusTagsPayload());

            var packetA = MakePacket(50, 0xA0);
            var packetB = MakePacket(70, 0xB0);
            var packetC = MakePacket(30, 0xC0);
            var combined = new byte[packetA.Length + packetB.Length + packetC.Length];
            Array.Copy(packetA, 0, combined, 0, packetA.Length);
            Array.Copy(packetB, 0, combined, packetA.Length, packetB.Length);
            Array.Copy(packetC, 0, combined, packetA.Length + packetB.Length, packetC.Length);

            WriteOggPage(stream, HeaderTypeNone, 480 * 3, serial, pageSeq++,
                segmentLengths: new byte[] { (byte)packetA.Length, (byte)packetB.Length, (byte)packetC.Length },
                payload: combined);

            stream.Position = 0;

            var parser = new OggOpusParser();
            Assert.IsTrue(parser.Initialize(stream));

            var buffer = new byte[8192];

            var sizeA = parser.ReadNextOpusPacket(buffer);
            Assert.AreEqual(50, sizeA);
            Assert.AreEqual(0xA0, buffer[0]);

            var sizeB = parser.ReadNextOpusPacket(buffer);
            Assert.AreEqual(70, sizeB);
            Assert.AreEqual(0xB0, buffer[0]);

            var sizeC = parser.ReadNextOpusPacket(buffer);
            Assert.AreEqual(30, sizeC);
            Assert.AreEqual(0xC0, buffer[0]);
        }

        // ---------- Sequence continuity ----------

        /// <summary>
        /// A page with a sequence number lower than the last seen page is treated
        /// as a duplicate / rewind and silently dropped. The parser doesn't crash
        /// or stall, and subsequent in-order pages still produce audio.
        /// </summary>
        [Test]
        public void Page_With_RewoundSequenceNumber_IsDroppedSilently()
        {
            using var stream = new MemoryStream();
            uint pageSeq = 0;
            const uint serial = 0xDDDD_DDDD;

            WriteOggPageWithSinglePacket(stream, HeaderTypeBos, 0, serial, pageSeq++, BuildOpusHeadPayload());
            WriteOggPageWithSinglePacket(stream, HeaderTypeNone, 0, serial, pageSeq++, BuildOpusTagsPayload());

            // Sequence 2 — real audio
            WriteOggPage(stream, HeaderTypeNone, 480, serial, pageSeq++,
                segmentLengths: new byte[] { 50 }, payload: MakePacket(50, 0xAA));

            // Sequence 1 — lower than last seen → must be discarded
            WriteOggPage(stream, HeaderTypeNone, 0, serial, sequence: 1,
                segmentLengths: new byte[] { 50 }, payload: MakePacket(50, 0xFF));

            // Sequence 3 — back in-order
            WriteOggPage(stream, HeaderTypeNone, 960, serial, pageSeq++,
                segmentLengths: new byte[] { 50 }, payload: MakePacket(50, 0xBB));

            stream.Position = 0;

            var parser = new OggOpusParser();
            Assert.IsTrue(parser.Initialize(stream));

            var buffer = new byte[8192];
            var collected = new List<byte>();
            int size;
            while ((size = parser.ReadNextOpusPacket(buffer)) > 0)
            {
                collected.Add(buffer[0]);
            }

            Assert.AreEqual(2, collected.Count, "rewound page must be dropped, two ordered packets remain");
            Assert.AreEqual(0xAA, collected[0]);
            Assert.AreEqual(0xBB, collected[1]);
        }

        /// <summary>
        /// A page with an unexpected serial number (interleaved logical stream)
        /// is dropped without affecting the active stream.
        /// </summary>
        [Test]
        public void Page_With_DifferentSerial_IsDroppedDuringStreaming()
        {
            using var stream = new MemoryStream();
            uint pageSeq = 0;
            const uint serial = 0xEEEE_EEEE;

            WriteOggPageWithSinglePacket(stream, HeaderTypeBos, 0, serial, pageSeq++, BuildOpusHeadPayload());
            WriteOggPageWithSinglePacket(stream, HeaderTypeNone, 0, serial, pageSeq++, BuildOpusTagsPayload());

            WriteOggPage(stream, HeaderTypeNone, 480, serial, pageSeq++,
                segmentLengths: new byte[] { 50 }, payload: MakePacket(50, 0xAA));

            // Random other serial (no BOS — we're not starting a NEW stream, just
            // a stray page that doesn't belong to the active one).
            WriteOggPage(stream, HeaderTypeNone, 480, serial: 0x12345678u, sequence: 0,
                segmentLengths: new byte[] { 50 }, payload: MakePacket(50, 0xFF));

            WriteOggPage(stream, HeaderTypeNone, 960, serial, pageSeq++,
                segmentLengths: new byte[] { 50 }, payload: MakePacket(50, 0xBB));

            stream.Position = 0;

            // Verbose logging produces a warning we don't want to assert on.
            LogAssert.ignoreFailingMessages = true;

            var parser = new OggOpusParser();
            Assert.IsTrue(parser.Initialize(stream));

            var buffer = new byte[8192];
            var collected = new List<byte>();
            int size;
            while ((size = parser.ReadNextOpusPacket(buffer)) > 0)
            {
                collected.Add(buffer[0]);
            }

            Assert.AreEqual(2, collected.Count);
            Assert.AreEqual(0xAA, collected[0]);
            Assert.AreEqual(0xBB, collected[1]);
        }

        // ---------- Incremental data arrival ----------

        /// <summary>
        /// Caller appends bytes incrementally (the realistic streaming case).
        /// At each prefix length the parser may have zero, one, or several
        /// packets available — but never crashes and never returns garbage.
        /// </summary>
        [Test]
        public void Parser_Handles_IncrementalDataArrival_WithoutLosingPackets()
        {
            // Build a complete stream first, then feed it byte-at-a-time.
            using var fullStream = new MemoryStream();
            uint pageSeq = 0;
            const uint serial = 0xDEADBEEFu;

            WriteOggPageWithSinglePacket(fullStream, HeaderTypeBos, 0, serial, pageSeq++, BuildOpusHeadPayload());
            WriteOggPageWithSinglePacket(fullStream, HeaderTypeNone, 0, serial, pageSeq++, BuildOpusTagsPayload());
            for (var i = 0; i < 5; i++)
            {
                WriteOggPage(fullStream, HeaderTypeNone, 480 * (i + 1), serial, pageSeq++,
                    segmentLengths: new byte[] { 50 }, payload: MakePacket(50, (byte)(0xA0 + i)));
            }
            WriteOggPage(fullStream, HeaderTypeEos, 480 * 5, serial, pageSeq,
                segmentLengths: Array.Empty<byte>(), payload: Array.Empty<byte>());

            var fullBytes = fullStream.ToArray();

            // Drip-feed the bytes through a MemoryStream the parser is bound to.
            using var feed = new MemoryStream();
            var parser = new OggOpusParser();
            Assert.IsTrue(parser.Initialize(feed));

            var buffer = new byte[8192];
            var collected = new List<byte>();
            var feedPosition = 0;

            // Append bytes in random-ish chunks.
            var chunkSizes = new[] { 13, 27, 5, 100, 7, 250, 1, 64 };
            var sizeIndex = 0;
            while (feedPosition < fullBytes.Length)
            {
                var chunk = chunkSizes[sizeIndex++ % chunkSizes.Length];
                var actual = Math.Min(chunk, fullBytes.Length - feedPosition);
                feed.Position = feed.Length;
                feed.Write(fullBytes, feedPosition, actual);
                feedPosition += actual;

                // Drain whatever's available now without losing read position.
                feed.Position = feed.Length - (feed.Length - feed.Position); // no-op; explicit
                int sz;
                while ((sz = parser.ReadNextOpusPacket(buffer)) > 0)
                {
                    collected.Add(buffer[0]);
                }
            }

            Assert.AreEqual(5, collected.Count, "five audio packets should survive the drip-feed");
            for (var i = 0; i < 5; i++)
            {
                Assert.AreEqual((byte)(0xA0 + i), collected[i], $"audio packet {i} marker preserved");
            }
        }

        // ---------- EOS handling ----------

        /// <summary>
        /// After consuming all audio packets and the EOS-flagged sentinel page,
        /// the parser quietly returns 0 (end-of-current-stream) without crashing
        /// or trying to ParseHeaders again.
        /// </summary>
        [Test]
        public void EosFollowedByEnd_ReturnsZeroCleanly()
        {
            using var stream = new MemoryStream();
            uint pageSeq = 0;
            const uint serial = 0xF00Du;

            WriteOggPageWithSinglePacket(stream, HeaderTypeBos, 0, serial, pageSeq++, BuildOpusHeadPayload());
            WriteOggPageWithSinglePacket(stream, HeaderTypeNone, 0, serial, pageSeq++, BuildOpusTagsPayload());
            WriteOggPage(stream, HeaderTypeNone, 480, serial, pageSeq++,
                segmentLengths: new byte[] { 50 }, payload: MakePacket(50, 0xAB));
            WriteOggPage(stream, HeaderTypeEos, 480, serial, pageSeq,
                segmentLengths: Array.Empty<byte>(), payload: Array.Empty<byte>());

            stream.Position = 0;

            var parser = new OggOpusParser();
            Assert.IsTrue(parser.Initialize(stream));

            var buffer = new byte[8192];
            Assert.AreEqual(50, parser.ReadNextOpusPacket(buffer));
            Assert.AreEqual(0xAB, buffer[0]);
            Assert.AreEqual(0, parser.ReadNextOpusPacket(buffer), "after EOS + no more data → 0");
            Assert.AreEqual(0, parser.ReadNextOpusPacket(buffer), "subsequent calls also return 0 (no error)");
        }

        // ---------- Garbage recovery ----------

        /// <summary>
        /// Random bytes between two valid OGG streams must be skipped: the parser
        /// scans forward to the next "OggS" capture pattern and resumes normally.
        /// </summary>
        [Test]
        public void GarbageBytesBetweenStreams_AreSkippedAndStreamingResumes()
        {
            using var stream = new MemoryStream();
            uint pageSeq1 = 0;
            const uint serial1 = 0x10000001u;

            WriteOggPageWithSinglePacket(stream, HeaderTypeBos, 0, serial1, pageSeq1++, BuildOpusHeadPayload());
            WriteOggPageWithSinglePacket(stream, HeaderTypeNone, 0, serial1, pageSeq1++, BuildOpusTagsPayload());
            WriteOggPage(stream, HeaderTypeNone, 480, serial1, pageSeq1++,
                segmentLengths: new byte[] { 50 }, payload: MakePacket(50, 0xAA));
            WriteOggPage(stream, HeaderTypeEos, 480, serial1, pageSeq1,
                segmentLengths: Array.Empty<byte>(), payload: Array.Empty<byte>());

            // 100 bytes of pure garbage between streams.
            var garbage = new byte[100];
            for (var i = 0; i < 100; i++) garbage[i] = (byte)(i ^ 0x42);
            stream.Write(garbage, 0, garbage.Length);

            uint pageSeq2 = 0;
            const uint serial2 = 0x20000002u;
            WriteOggPageWithSinglePacket(stream, HeaderTypeBos, 0, serial2, pageSeq2++, BuildOpusHeadPayload());
            WriteOggPageWithSinglePacket(stream, HeaderTypeNone, 0, serial2, pageSeq2++, BuildOpusTagsPayload());
            WriteOggPage(stream, HeaderTypeNone, 480, serial2, pageSeq2++,
                segmentLengths: new byte[] { 50 }, payload: MakePacket(50, 0xBB));

            stream.Position = 0;

            // Recovery emits a warning we don't assert on.
            LogAssert.ignoreFailingMessages = true;

            var parser = new OggOpusParser();
            Assert.IsTrue(parser.Initialize(stream));

            var buffer = new byte[8192];
            var collected = new List<byte>();
            int size;
            while ((size = parser.ReadNextOpusPacket(buffer)) > 0)
            {
                collected.Add(buffer[0]);
            }

            Assert.AreEqual(2, collected.Count, "garbage between streams gets skipped, both audio packets survive");
            Assert.AreEqual(0xAA, collected[0]);
            Assert.AreEqual(0xBB, collected[1]);
        }

        // ---------- Header-info accessibility ----------

        /// <summary>
        /// After OpusHead is consumed, <c>Channels</c>, <c>SampleRate</c>, and
        /// <c>PreSkip</c> reflect the new stream's header — even after a
        /// stream-boundary transition the values from the latest stream are
        /// visible (older callers that peek these fields between turns keep
        /// working).
        /// </summary>
        [Test]
        public void HeaderProperties_ReflectMostRecentStream()
        {
            using var stream = new MemoryStream();
            uint pageSeq = 0;
            const uint serial = 0x42424242u;

            WriteOggPageWithSinglePacket(stream, HeaderTypeBos, 0, serial, pageSeq++, BuildOpusHeadPayload());
            WriteOggPageWithSinglePacket(stream, HeaderTypeNone, 0, serial, pageSeq++, BuildOpusTagsPayload());
            WriteOggPage(stream, HeaderTypeNone, 480, serial, pageSeq++,
                segmentLengths: new byte[] { 50 }, payload: MakePacket(50, 0xAA));

            stream.Position = 0;

            var parser = new OggOpusParser();
            Assert.IsTrue(parser.Initialize(stream));

            var buffer = new byte[8192];
            Assert.Greater(parser.ReadNextOpusPacket(buffer), 0);

            Assert.AreEqual(1, parser.Channels, "OpusHead declared 1 channel");
            Assert.AreEqual(48000, parser.SampleRate, "Opus always decodes at 48 kHz");
            Assert.AreEqual(3840, parser.PreSkip, "OpusHead declared 3840 samples preSkip");
        }

        // ---------- OGG byte-builder helpers ----------

        private static byte[] BuildOpusHeadPayload()
        {
            var p = new byte[19];
            Array.Copy(OpusHeadMagic, 0, p, 0, 8);
            p[8] = 1;     // version
            p[9] = 1;     // channels
            p[10] = 0x00; // preSkip lo
            p[11] = 0x0F; // preSkip hi (0x0F00 = 3840)
            p[12] = 0xC0; // input sample rate lo (0x5DC0 = 24000)
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
            const string vendor = "TestVendor";
            var vendorBytes = Encoding.UTF8.GetBytes(vendor);
            var p = new byte[8 + 4 + vendorBytes.Length + 4];
            Array.Copy(OpusTagsMagic, 0, p, 0, 8);
            p[8] = (byte)vendorBytes.Length;
            p[9] = 0; p[10] = 0; p[11] = 0;
            Array.Copy(vendorBytes, 0, p, 12, vendorBytes.Length);
            return p;
        }

        private static byte[] MakePacket(int length, byte marker)
        {
            var p = new byte[length];
            p[0] = marker;
            for (var i = 1; i < length; i++) p[i] = (byte)((marker + i) & 0xFF);
            return p;
        }

        private static void WriteOggPageWithSinglePacket(MemoryStream output, byte headerType, long granule, uint serial, uint sequence, byte[] payload)
        {
            // Single-packet helper: segment table holds one entry equal to payload length
            // (only valid when length < 255; we use it for headers that are well below).
            byte[] segs;
            if (payload.Length == 0)
            {
                segs = Array.Empty<byte>();
            }
            else
            {
                segs = new byte[] { (byte)payload.Length };
            }
            WriteOggPage(output, headerType, granule, serial, sequence, segs, payload);
        }

        private static void WriteOggPage(MemoryStream output, byte headerType, long granule, uint serial, uint sequence,
            byte[] segmentLengths, byte[] payload)
        {
            var pageSize = 27 + segmentLengths.Length + payload.Length;
            var page = new byte[pageSize];
            var offset = 0;

            Array.Copy(OggMagic, 0, page, offset, 4); offset += 4;
            page[offset++] = 0; // OGG version
            page[offset++] = headerType;
            BitConverter.GetBytes(granule).CopyTo(page, offset); offset += 8;
            BitConverter.GetBytes(serial).CopyTo(page, offset); offset += 4;
            BitConverter.GetBytes(sequence).CopyTo(page, offset); offset += 4;
            page[offset++] = 0; page[offset++] = 0; page[offset++] = 0; page[offset++] = 0; // CRC slot
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
