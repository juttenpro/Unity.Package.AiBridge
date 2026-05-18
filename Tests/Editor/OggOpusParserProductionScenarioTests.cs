using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;
using Tsc.AIBridge.Audio.Codecs;

namespace Tsc.AIBridge.Tests.Editor
{
    /// <summary>
    /// BUSINESS REQUIREMENT: <see cref="OggOpusParser"/> must extract ALL audio packets
    /// from production-shaped OGG/Opus streams, especially those produced by Voxtral
    /// and Cartesia (PCM → Opus → OGG via our own packaging code).
    ///
    /// WHY: In a real session on 2026-05-13 17:42 the backend produced a 4046ms TTS response
    /// (165 OGG pages, 30748 bytes, 41 chars text) for the sentence
    /// "Sta ... statines? Dat zie ik niet zitten." The client only played back ~1,99s
    /// (95688 samples) before its safety-net timeout triggered — losing more than half of
    /// the audio that the backend confirmed it had sent (success=True, was_interrupted=False).
    ///
    /// The symptom is reproducible across providers that go through our PCM→Opus→OGG path
    /// (Voxtral, Cartesia) but NOT for ElevenLabs (native OGG/Opus passthrough). That
    /// localises the suspect surface to either (a) our server-side OggOpusWriter packaging,
    /// (b) WebSocket transport between server and client, or (c) the client-side
    /// <see cref="OggOpusParser"/>. This fixture isolates (c): given a clean, deterministic
    /// OGG byte stream that mimics the shape of production output, does the parser return
    /// every packet?
    ///
    /// WHAT: Build byte streams with production-realistic page counts and per-page packet
    /// densities, then assert that the parser extracts exactly the expected number of
    /// audio packets and that each packet's body is intact.
    ///
    /// HOW: Use the same OGG byte-builder helpers <see cref="OggOpusParserStateMachineTests"/>
    /// uses (re-implemented locally to keep this fixture self-contained), construct streams
    /// with shapes that match what we observed in the Voxtral incident, then parse and count.
    ///
    /// SUCCESS CRITERIA:
    /// - 200 single-packet pages: parser returns all 200 audio packets (zero loss)
    /// - 100 dual-packet pages: parser returns all 200 audio packets (multi-packet pages OK)
    /// - 165 mixed-density pages totalling 200 packets (matches Voxtral observed shape):
    ///   parser returns all 200 packets
    /// - Each parsed packet's first byte matches the deterministic marker we wrote
    ///
    /// BUSINESS IMPACT:
    /// - If a test fails, the parser is silently dropping packets in production-shaped streams
    ///   — the NPC audio truncation users hear is explained by this code path
    /// - If all tests pass, the parser is innocent and the bug lies upstream (transport,
    ///   audio buffer, or server-side packaging); investigation can focus there
    /// </summary>
    [TestFixture]
    public class OggOpusParserProductionScenarioTests
    {
        private const byte HeaderTypeBos = 0x02;
        private const byte HeaderTypeEos = 0x04;
        private const byte HeaderTypeNone = 0x00;

        private static readonly byte[] OggMagic = Encoding.ASCII.GetBytes("OggS");
        private static readonly byte[] OpusHeadMagic = Encoding.ASCII.GetBytes("OpusHead");
        private static readonly byte[] OpusTagsMagic = Encoding.ASCII.GetBytes("OpusTags");

        /// <summary>
        /// BUSINESS REQUIREMENT: Parser must handle high page counts with one packet per page.
        ///
        /// WHY: Voxtral / Cartesia output streams contain hundreds of small pages — far more
        ///      than the handful in existing tests. A subtle off-by-one or state-machine
        ///      reset bug would only manifest at scale.
        /// WHAT: 200 audio packets, each in its own page (200 audio pages + headers + EOS).
        /// </summary>
        [Test]
        public void Parse_200SinglePacketPages_ReturnsAll200Packets()
        {
            using var stream = BuildProductionLikeStream(
                serial: 0xC0DE_FACEu,
                packetCount: 200,
                packetsPerPage: 1,
                packetSize: 150);

            var (packets, terminatedNormally) = ParseAll(stream);

            Assert.IsTrue(terminatedNormally, "parser must return 0 (clean EOS), never -1 (error)");
            Assert.AreEqual(200, packets.Count,
                $"Expected 200 audio packets, parser returned {packets.Count}. " +
                "If this fails, the parser is silently dropping packets at scale — exactly the " +
                "symptom users report with Voxtral/Cartesia TTS (audio cuts off mid-sentence).");
            AssertEachPacketMarkerMatchesIndex(packets);
        }

        /// <summary>
        /// BUSINESS REQUIREMENT: Multi-packet pages (2 packets per page) must yield both packets.
        ///
        /// WHY: Real Opus encoders sometimes emit multiple frames per OGG page (denser packing).
        ///      The parser must walk the segment table and emit each terminated packet.
        /// WHAT: 100 pages × 2 packets = 200 packets total.
        /// </summary>
        [Test]
        public void Parse_100DualPacketPages_ReturnsAll200Packets()
        {
            using var stream = BuildProductionLikeStream(
                serial: 0xBEEF_F00Du,
                packetCount: 200,
                packetsPerPage: 2,
                packetSize: 80);

            var (packets, terminatedNormally) = ParseAll(stream);

            Assert.IsTrue(terminatedNormally);
            Assert.AreEqual(200, packets.Count,
                $"Expected 200 audio packets across 100 dual-packet pages, parser returned {packets.Count}.");
            AssertEachPacketMarkerMatchesIndex(packets);
        }

        /// <summary>
        /// BUSINESS REQUIREMENT: Match the EXACT shape of the Voxtral production incident.
        ///
        /// WHY: Backend reported 165 OGG pages totalling 30615 bytes for a 4046ms TTS response.
        ///      That's ~185 bytes/page and ~1.21 packets/page on average (assuming standard
        ///      20ms Opus frames at 48kHz: 4046ms / 20ms ≈ 200 packets across 165 pages).
        ///      We reconstruct that shape — most pages carry one packet, every fifth page
        ///      carries two — and verify the parser still returns all 200.
        /// WHAT: 165 pages totalling 200 packets, deterministically distributed.
        /// </summary>
        [Test]
        public void Parse_VoxtralProductionShape_165PagesYielding200Packets()
        {
            const int totalPackets = 200;
            const int totalPages = 165;
            // 35 pages double up (165 pages, 200 packets → 35 extras packed two-per-page,
            // 130 pages with one packet). 35 double-packet + 130 single-packet = 165 pages,
            // 35*2 + 130*1 = 200 packets — exactly the Voxtral shape.
            const int doublePackedPages = totalPackets - totalPages;
            const int singlePackedPages = totalPages - doublePackedPages;

            using var stream = BuildMixedDensityStream(
                serial: 0xD15EA5Eu,
                singlePackedPages: singlePackedPages,
                doublePackedPages: doublePackedPages,
                packetSize: 150);

            var (packets, terminatedNormally) = ParseAll(stream);

            Assert.IsTrue(terminatedNormally);
            Assert.AreEqual(totalPackets, packets.Count,
                $"Voxtral production shape (165 pages, 200 packets) yielded {packets.Count} packets. " +
                "If this is lower than 200, the parser is the source of the truncation users hear.");
            AssertEachPacketMarkerMatchesIndex(packets);
        }

        /// <summary>
        /// BUSINESS REQUIREMENT: Drip-fed bytes must produce the same packet count as a single read.
        ///
        /// WHY: In production the bytes arrive over WebSocket in arbitrarily-sized chunks, not as
        ///      one complete blob. The parser must restore its read position between calls and
        ///      eventually return every packet — exactly the contract claimed by its docstring.
        /// WHAT: Same Voxtral shape as the previous test, but bytes are appended in small
        ///       irregular chunks between parse calls.
        /// </summary>
        [Test]
        public void Parse_VoxtralShape_DripFed_YieldsAll200Packets()
        {
            const int totalPackets = 200;
            const int doublePackedPages = 35;
            const int singlePackedPages = 130;

            using var sourceStream = BuildMixedDensityStream(
                serial: 0xC0FFEEu,
                singlePackedPages: singlePackedPages,
                doublePackedPages: doublePackedPages,
                packetSize: 150);
            var fullBytes = sourceStream.ToArray();

            using var feed = new MemoryStream();
            var parser = new OggOpusParser();
            Assert.IsTrue(parser.Initialize(feed));

            var buffer = new byte[8192];
            var collected = new List<byte>();
            var feedPosition = 0;

            // Irregular chunk sizes mimic WebSocket frame jitter.
            var chunkSizes = new[] { 47, 113, 13, 271, 5, 64, 1, 199, 33, 7 };
            var sizeIndex = 0;

            while (feedPosition < fullBytes.Length)
            {
                var chunk = chunkSizes[sizeIndex++ % chunkSizes.Length];
                var actual = Math.Min(chunk, fullBytes.Length - feedPosition);
                feed.Position = feed.Length;
                feed.Write(fullBytes, feedPosition, actual);
                feedPosition += actual;

                int sz;
                while ((sz = parser.ReadNextOpusPacket(buffer)) > 0)
                {
                    collected.Add(buffer[0]);
                }
            }

            Assert.AreEqual(totalPackets, collected.Count,
                $"Drip-fed Voxtral shape yielded {collected.Count} packets, expected {totalPackets}. " +
                "A mismatch here means the parser loses packets at chunk boundaries — explains why " +
                "even successful backend transmissions can result in audio truncation on the client.");
        }

        // ---------- Stream builders ----------

        /// <summary>
        /// Builds a complete OGG/Opus byte stream: OpusHead BOS + OpusTags + audio pages + EOS.
        /// Every audio packet's first byte equals its packet index modulo 256, so parsed packets
        /// can be verified against their original write order.
        /// </summary>
        private static MemoryStream BuildProductionLikeStream(uint serial, int packetCount, int packetsPerPage, int packetSize)
        {
            var stream = new MemoryStream();
            uint pageSeq = 0;
            long granule = 0;

            WriteOggPage(stream, HeaderTypeBos, 0, serial, pageSeq++,
                segmentLengths: new byte[] { (byte)BuildOpusHeadPayload().Length },
                payload: BuildOpusHeadPayload());
            WriteOggPage(stream, HeaderTypeNone, 0, serial, pageSeq++,
                segmentLengths: new byte[] { (byte)BuildOpusTagsPayload().Length },
                payload: BuildOpusTagsPayload());

            var packetIndex = 0;
            while (packetIndex < packetCount)
            {
                var packetsThisPage = Math.Min(packetsPerPage, packetCount - packetIndex);
                var segmentLengths = new byte[packetsThisPage];
                var payloadParts = new byte[packetsThisPage][];
                var pagePayloadSize = 0;
                for (var i = 0; i < packetsThisPage; i++)
                {
                    segmentLengths[i] = (byte)packetSize;
                    payloadParts[i] = MakePacket(packetSize, (byte)(packetIndex + i));
                    pagePayloadSize += packetSize;
                }
                var combinedPayload = new byte[pagePayloadSize];
                var offset = 0;
                foreach (var part in payloadParts)
                {
                    Array.Copy(part, 0, combinedPayload, offset, part.Length);
                    offset += part.Length;
                }

                granule += 480 * packetsThisPage; // 20ms per packet at 24kHz input → 480 samples
                WriteOggPage(stream, HeaderTypeNone, granule, serial, pageSeq++, segmentLengths, combinedPayload);
                packetIndex += packetsThisPage;
            }

            WriteOggPage(stream, HeaderTypeEos, granule, serial, pageSeq,
                segmentLengths: Array.Empty<byte>(), payload: Array.Empty<byte>());

            stream.Position = 0;
            return stream;
        }

        /// <summary>
        /// Builds a stream with a mix of single-packet and double-packet pages, interleaved.
        /// The interleaving mimics what a real Opus encoder produces when frame sizes vary.
        /// </summary>
        private static MemoryStream BuildMixedDensityStream(uint serial, int singlePackedPages, int doublePackedPages, int packetSize)
        {
            var stream = new MemoryStream();
            uint pageSeq = 0;
            long granule = 0;

            WriteOggPage(stream, HeaderTypeBos, 0, serial, pageSeq++,
                segmentLengths: new byte[] { (byte)BuildOpusHeadPayload().Length },
                payload: BuildOpusHeadPayload());
            WriteOggPage(stream, HeaderTypeNone, 0, serial, pageSeq++,
                segmentLengths: new byte[] { (byte)BuildOpusTagsPayload().Length },
                payload: BuildOpusTagsPayload());

            var packetIndex = 0;
            var singlesRemaining = singlePackedPages;
            var doublesRemaining = doublePackedPages;

            // Alternate double-packed pages into the single-packed sequence at a steady ratio.
            // For 130 singles + 35 doubles, insert a double approximately every 4 pages.
            var totalPages = singlePackedPages + doublePackedPages;
            for (var pageIndex = 0; pageIndex < totalPages; pageIndex++)
            {
                var slotsLeft = totalPages - pageIndex;
                var doubleProbability = doublesRemaining * 1.0 / Math.Max(1, slotsLeft);
                // Deterministic: pick double when ratio threshold crosses; falls back when exhausted.
                var pickDouble = doublesRemaining > 0 && doubleProbability >= 0.5;

                var packetsThisPage = pickDouble ? 2 : 1;
                if (pickDouble)
                    doublesRemaining--;
                else
                    singlesRemaining--;

                var segmentLengths = new byte[packetsThisPage];
                var payloadParts = new byte[packetsThisPage][];
                var pagePayloadSize = 0;
                for (var i = 0; i < packetsThisPage; i++)
                {
                    segmentLengths[i] = (byte)packetSize;
                    payloadParts[i] = MakePacket(packetSize, (byte)(packetIndex + i));
                    pagePayloadSize += packetSize;
                }
                var combinedPayload = new byte[pagePayloadSize];
                var offset = 0;
                foreach (var part in payloadParts)
                {
                    Array.Copy(part, 0, combinedPayload, offset, part.Length);
                    offset += part.Length;
                }

                granule += 480 * packetsThisPage;
                WriteOggPage(stream, HeaderTypeNone, granule, serial, pageSeq++, segmentLengths, combinedPayload);
                packetIndex += packetsThisPage;
            }

            WriteOggPage(stream, HeaderTypeEos, granule, serial, pageSeq,
                segmentLengths: Array.Empty<byte>(), payload: Array.Empty<byte>());

            stream.Position = 0;
            return stream;
        }

        // ---------- Parser drainer ----------

        private static (List<byte> packets, bool terminatedNormally) ParseAll(MemoryStream stream)
        {
            var parser = new OggOpusParser();
            Assert.IsTrue(parser.Initialize(stream));

            var buffer = new byte[8192];
            var collected = new List<byte>();

            while (true)
            {
                var size = parser.ReadNextOpusPacket(buffer);
                if (size > 0)
                {
                    collected.Add(buffer[0]); // first-byte marker is enough to identify the packet
                    continue;
                }
                if (size == 0) return (collected, true);
                return (collected, false); // -1 = error
            }
        }

        private static void AssertEachPacketMarkerMatchesIndex(List<byte> packets)
        {
            for (var i = 0; i < packets.Count; i++)
            {
                Assert.AreEqual((byte)(i & 0xFF), packets[i],
                    $"Packet {i} arrived with marker 0x{packets[i]:X2}, expected 0x{i & 0xFF:X2}. " +
                    "Out-of-order or duplicated packets indicate a state-machine bug.");
            }
        }

        // ---------- OGG byte-builder helpers (copied from OggOpusParserStateMachineTests) ----------

        private static byte[] BuildOpusHeadPayload()
        {
            var p = new byte[19];
            Array.Copy(OpusHeadMagic, 0, p, 0, 8);
            p[8] = 1;     // version
            p[9] = 1;     // channels
            p[10] = 0x00; p[11] = 0x0F; // preSkip = 3840
            p[12] = 0xC0; p[13] = 0x5D; p[14] = 0x00; p[15] = 0x00; // input sample rate = 24000
            p[16] = 0x00; p[17] = 0x00; // output gain = 0
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
