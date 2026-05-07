using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Tsc.AIBridge.Audio.Codecs
{
    /// <summary>
    /// Streaming OGG/Opus container parser. Reads OGG pages from an input
    /// <see cref="Stream"/> and yields the contained Opus packets.
    ///
    /// <para><b>What this is</b></para>
    /// A small, deterministic state machine that walks the OGG bitstream the way
    /// the spec describes it (RFC 3533 + RFC 7845): one logical stream begins on
    /// a BOS-flagged page, ends on an EOS-flagged page, and any number of logical
    /// streams may be chained back-to-back on the same input. The first two
    /// packets of every logical stream are <c>OpusHead</c> and <c>OpusTags</c>;
    /// every subsequent packet is one Opus audio frame.
    ///
    /// <para><b>Why it was rewritten</b></para>
    /// The previous implementation grew organically over a year of TTS-provider
    /// changes and accumulated:
    /// <list type="bullet">
    /// <item><description>Three overlapping byte buffers
    /// (<c>_streamBuffer</c>, <c>_continuousStream</c>, <c>_incompletePageBuffer</c>)
    /// that drifted out of sync between code paths.</description></item>
    /// <item><description>A "rewind 27 bytes and re-enter" trick used to retrofit
    /// multi-stream support, layered on top of an in-place
    /// <c>_inputStream = new MemoryStream(combinedData)</c> stream-replacement
    /// that fired during chunk-boundary recovery.</description></item>
    /// <item><description>Five OGG header constants commented out (BOS, EOS,
    /// CONTINUED, …) plus an <c>_isEndOfStream</c> field with every assignment
    /// commented out — multi-stream and EOS were effectively missing as
    /// concepts.</description></item>
    /// <item><description>A global <c>HashSet&lt;uint&gt;</c> of page sequences
    /// shared across logical streams, so sentence #2's page-0 was matched against
    /// sentence #1's page-0 ("we've seen this — likely rewind, OK") and the
    /// fresh OpusHead packet ended up routed to the Opus decoder, producing the
    /// production-incident "Invalid OpusHead signature: h..." log spam.</description></item>
    /// </list>
    ///
    /// <para><b>What's different now</b></para>
    /// <list type="bullet">
    /// <item><description><b>Explicit state machine</b>:
    /// <c>ExpectingNewLogicalStream → ReadingHeaders → Streaming → ExpectingNewLogicalStream</c>.
    /// Every page transitions one state.</description></item>
    /// <item><description><b>Per-logical-stream context</b> wiped on every BOS:
    /// serial number, last-sequence, partial-packet buffer, header-info. Memory
    /// is bounded across long sessions.</description></item>
    /// <item><description><b>One byte source</b>: the caller-provided
    /// <see cref="Stream"/>. We never replace it, never split it, never seek
    /// backwards. When a page is incomplete we save the read offset, return
    /// "no packet yet", and resume the next call with more data appended.</description></item>
    /// <item><description><b>Continued-packet semantics handled per spec</b>:
    /// the segment table's trailing-255 rule (and continued-flag bit) drives
    /// packet boundaries, not state-by-side-effect.</description></item>
    /// </list>
    ///
    /// <para><b>Public contract preserved</b></para>
    /// <see cref="Initialize"/>, <see cref="ReadNextOpusPacket"/>,
    /// <see cref="Channels"/>, <see cref="SampleRate"/>, and <see cref="PreSkip"/>
    /// keep their old signatures so <c>OpusStreamDecoder</c> needs no changes.
    /// </summary>
    public class OggOpusParser
    {
        // OGG page header constants (RFC 3533 §6).
        private const byte FLAG_CONTINUED = 0x01;
        private const byte FLAG_BOS = 0x02;
        private const byte FLAG_EOS = 0x04;
        private const int OGG_HEADER_SIZE = 27;
        private const int MAX_SEGMENTS = 255;
        private const int MAX_SEGMENT_SIZE = 255;

        // Opus packet identification headers (RFC 7845 §5).
        private const string OPUS_HEAD_SIGNATURE = "OpusHead";
        private const string OPUS_TAGS_SIGNATURE = "OpusTags";

        /// <summary>The three high-level states this parser cycles through.</summary>
        private enum ParserState
        {
            /// <summary>Initial state, and the state after every EOS-flagged page.
            /// Any page we read must have BOS set, else it's discarded with a warning.</summary>
            ExpectingNewLogicalStream,

            /// <summary>BOS page seen, awaiting OpusHead and OpusTags packets.
            /// Per RFC 7845 §5 the first two packets of every Opus logical stream
            /// are these mandatory headers, in order.</summary>
            ReadingHeaders,

            /// <summary>Both OpusHead and OpusTags consumed for the current logical
            /// stream; every subsequent packet is an Opus audio frame.</summary>
            Streaming,
        }

        // ----- Stream input -----
        private Stream _input;
        private bool _isVerboseLogging;

        /// <summary>
        /// Read position the parser has consumed up to. Tracked internally so the
        /// parser is robust against the caller appending more bytes to the input
        /// stream between calls (which moves <c>_input.Position</c> to the end).
        /// At the start of every public call we restore <c>_input.Position</c> to
        /// this value; at the end we save the new position back.
        /// </summary>
        private long _readPosition;

        // ----- Parser state machine -----
        private ParserState _state = ParserState.ExpectingNewLogicalStream;

        // ----- Per-logical-stream context (cleared on every BOS-flagged page) -----
        private uint _streamSerial;
        private uint _lastSequence;
        private bool _lastSequenceValid;
        private bool _opusHeadParsed;
        private List<byte> _continuedPacket; // Cross-page packet accumulator (or null).
        private int _channels = 1;
        private int _sampleRate = 48000;
        private ushort _preSkip;
        private short _outputGain;

        // ----- Output queue -----
        private readonly Queue<byte[]> _audioPackets = new();

        // ----- Reusable buffers -----
        private readonly byte[] _headerBuffer = new byte[OGG_HEADER_SIZE];
        private readonly byte[] _segmentTable = new byte[MAX_SEGMENTS];

        /// <summary>Channels declared in the current OpusHead. 1 or 2.</summary>
        public int Channels => _channels;

        /// <summary>Decode rate. Opus always decodes to 48 kHz; this is informational.</summary>
        public int SampleRate => _sampleRate;

        /// <summary>PreSkip samples (encoder lookahead) declared in OpusHead.</summary>
        public ushort PreSkip => _preSkip;

        /// <summary>
        /// Bind the parser to an input stream. Returns false only when the stream
        /// itself is unusable (null or non-readable). Validity of the OGG content
        /// is established lazily during <see cref="ReadNextOpusPacket"/>.
        /// </summary>
        public bool Initialize(Stream inputStream, bool isVerboseLogging = false)
        {
            if (inputStream == null || !inputStream.CanRead)
            {
                Debug.LogError("[OggOpusParser] Invalid input stream");
                return false;
            }

            _input = inputStream;
            _isVerboseLogging = isVerboseLogging;
            _readPosition = inputStream.CanSeek ? inputStream.Position : 0;
            ResetForNewLogicalStream();
            _state = ParserState.ExpectingNewLogicalStream;

            if (_isVerboseLogging)
                Debug.Log("[OggOpusParser] Initialized");

            return true;
        }

        /// <summary>
        /// Reads the next Opus audio packet from the input stream. Drives the
        /// state machine until either an audio packet is available or the input
        /// runs out of data.
        /// </summary>
        /// <param name="packetBuffer">Caller-provided buffer that receives the
        /// packet bytes. Must be large enough for one Opus frame
        /// (8 KB is comfortable for any sensible voice config).</param>
        /// <returns>
        /// The packet length on success; <c>0</c> when the stream has no
        /// complete page available right now (caller should append more data and
        /// try again); <c>-1</c> on a fatal error.
        /// </returns>
        public int ReadNextOpusPacket(byte[] packetBuffer)
        {
            if (packetBuffer == null) return -1;
            if (_input == null) return -1;

            // Restore the position the parser was last reading from. The caller may
            // have appended more bytes to the stream since our last call, which on
            // a MemoryStream leaves Position at the end — reading from there would
            // immediately hit EOF.
            if (_input.CanSeek)
            {
                _input.Position = _readPosition;
            }

            try
            {
                while (true)
                {
                    // Fast path: deliver previously-buffered audio packets one at a time.
                    if (_audioPackets.Count > 0)
                    {
                        return EmitPacket(packetBuffer, _audioPackets.Dequeue());
                    }

                    // No queued audio — process more input.
                    if (!TryReadOnePage(out var page))
                    {
                        return 0;
                    }

                    HandlePage(page);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[OggOpusParser] Error reading packet: {ex.Message}");
                return -1;
            }
            finally
            {
                if (_input != null && _input.CanSeek)
                {
                    _readPosition = _input.Position;
                }
            }
        }

        // ---------- State machine ----------

        /// <summary>
        /// Routes a freshly-read page through the state machine. May enqueue zero
        /// or more audio packets to <see cref="_audioPackets"/>.
        /// </summary>
        private void HandlePage(OggPage page)
        {
            var isBos = (page.HeaderType & FLAG_BOS) != 0;
            var isEos = (page.HeaderType & FLAG_EOS) != 0;
            var isContinued = (page.HeaderType & FLAG_CONTINUED) != 0;

            if (isBos)
            {
                // A BOS page always starts a new logical stream, regardless of what
                // state we were in. This is the multi-stream case (sentence #2 of a
                // voxtral / cartesia turn) and also the cold-start case.
                ResetForNewLogicalStream();
                _streamSerial = page.Serial;
                _lastSequence = page.Sequence;
                _lastSequenceValid = true;
                _state = ParserState.ReadingHeaders;
            }
            else
            {
                // Non-BOS page must extend an established logical stream.
                if (_state == ParserState.ExpectingNewLogicalStream)
                {
                    if (_isVerboseLogging)
                        Debug.LogWarning(
                            $"[OggOpusParser] Discarding non-BOS page (seq={page.Sequence}) — no active logical stream");
                    return;
                }

                if (page.Serial != _streamSerial)
                {
                    if (_isVerboseLogging)
                        Debug.LogWarning(
                            $"[OggOpusParser] Discarding page from interleaved logical stream (serial={page.Serial:X}, expected {_streamSerial:X})");
                    return;
                }

                if (_lastSequenceValid)
                {
                    var expected = _lastSequence + 1;
                    if (page.Sequence < expected)
                    {
                        // Duplicate / rewind — silently ignore (network resends, etc.).
                        return;
                    }
                    if (page.Sequence > expected && _isVerboseLogging)
                    {
                        Debug.LogWarning(
                            $"[OggOpusParser] Page sequence gap: expected {expected}, got {page.Sequence}. Some audio may be lost.");
                    }
                }
                _lastSequence = page.Sequence;
                _lastSequenceValid = true;
            }

            // Reassemble packets from this page's segments. The first segment
            // belongs to the previously-continued packet iff this page has the
            // continued flag set.
            foreach (var packet in ExtractPackets(page, isContinued))
            {
                ProcessPacket(packet);
            }

            if (isEos)
            {
                _state = ParserState.ExpectingNewLogicalStream;
            }
        }

        /// <summary>Routes one fully-reassembled packet through the state machine.</summary>
        private void ProcessPacket(byte[] packet)
        {
            switch (_state)
            {
                case ParserState.ReadingHeaders:
                    if (StartsWith(packet, OPUS_HEAD_SIGNATURE))
                    {
                        if (TryParseOpusHead(packet))
                        {
                            _opusHeadParsed = true;
                        }
                        else
                        {
                            // OpusHead malformed — abandon this logical stream.
                            _state = ParserState.ExpectingNewLogicalStream;
                        }
                    }
                    else if (StartsWith(packet, OPUS_TAGS_SIGNATURE))
                    {
                        if (!_opusHeadParsed && _isVerboseLogging)
                        {
                            Debug.LogWarning(
                                "[OggOpusParser] OpusTags arrived before OpusHead — proceeding to streaming anyway");
                        }
                        // OpusTags is informational; we don't fail on it.
                        _state = ParserState.Streaming;
                    }
                    else
                    {
                        // RFC 7845 mandates OpusHead first, OpusTags second, audio after.
                        // Anything else here is malformed. Skip the packet but keep
                        // looking — the spec also allows unknown ID-style packets in
                        // theory, though we don't expect any from our TTS providers.
                        Debug.LogWarning(
                            $"[OggOpusParser] Unexpected packet during header phase (first byte 0x{packet[0]:X2}); skipping");
                    }
                    break;

                case ParserState.Streaming:
                    // Skip 0-byte packets. The OGG segment-table sometimes carries a
                    // single 0-length entry to express "packet boundary, no payload"
                    // (e.g. on payload-less EOS-flagged pages); these aren't valid
                    // Opus audio frames and would confuse OpusDecoder.
                    if (packet.Length > 0)
                    {
                        _audioPackets.Enqueue(packet);
                    }
                    break;

                case ParserState.ExpectingNewLogicalStream:
                    // Already filtered at page level — packets here are unreachable.
                    break;
            }
        }

        // ---------- Page reading ----------

        /// <summary>
        /// Try to read one complete OGG page from the input. The read is
        /// transactional: if the stream doesn't currently have a full page,
        /// we restore the position and return <c>false</c> so the caller can
        /// retry after appending more data.
        /// </summary>
        private bool TryReadOnePage(out OggPage page)
        {
            page = default;

            var startPosition = _input.Position;

            // Step 1: read 27-byte fixed header.
            if (!ReadAtLeast(_headerBuffer, 0, OGG_HEADER_SIZE))
            {
                _input.Position = startPosition;
                return false;
            }

            // Step 2: validate OGG capture pattern. If it doesn't match, scan
            // forward for the next "OggS" — production input from a flaky
            // network can occasionally desync; we recover rather than wedge.
            if (!HasOggMagic(_headerBuffer))
            {
                if (!ScanForwardToNextOgg(startPosition))
                {
                    _input.Position = startPosition;
                    return false;
                }
                return TryReadOnePage(out page);
            }

            var headerType = _headerBuffer[5];
            var granulePosition = BitConverter.ToInt64(_headerBuffer, 6);
            var serial = BitConverter.ToUInt32(_headerBuffer, 14);
            var sequence = BitConverter.ToUInt32(_headerBuffer, 18);
            var pageSegments = _headerBuffer[26];

            // Step 3: read segment table.
            if (pageSegments > 0)
            {
                if (!ReadAtLeast(_segmentTable, 0, pageSegments))
                {
                    _input.Position = startPosition;
                    return false;
                }
            }

            // Step 4: compute total payload size and read it.
            var payloadSize = 0;
            for (var i = 0; i < pageSegments; i++)
            {
                payloadSize += _segmentTable[i];
            }

            byte[] payload;
            if (payloadSize > 0)
            {
                payload = new byte[payloadSize];
                if (!ReadAtLeast(payload, 0, payloadSize))
                {
                    _input.Position = startPosition;
                    return false;
                }
            }
            else
            {
                payload = Array.Empty<byte>();
            }

            // Snapshot the segment lengths used by ExtractPackets. _segmentTable
            // is reused on the next call, so we must keep our own copy.
            var segmentLengths = new byte[pageSegments];
            Array.Copy(_segmentTable, segmentLengths, pageSegments);

            page = new OggPage
            {
                HeaderType = headerType,
                GranulePosition = granulePosition,
                Serial = serial,
                Sequence = sequence,
                SegmentLengths = segmentLengths,
                Payload = payload,
            };
            return true;
        }

        /// <summary>
        /// Walks the segment table per RFC 3533 §6: a packet's segments are read
        /// until one with length &lt; 255 closes it (so a chain
        /// <c>[255, 255, 80]</c> is one packet, <c>[100]</c> is one packet,
        /// <c>[255, 0]</c> is one packet of length 510). When a page ends with
        /// a 255-segment, the packet "continues" into the next page; the
        /// continuation flag on that next page repeats the message.
        /// </summary>
        private IEnumerable<byte[]> ExtractPackets(OggPage page, bool startsWithContinuation)
        {
            var packetStart = 0;
            var packetSize = 0;
            var inContinuedPacket = startsWithContinuation;

            for (var i = 0; i < page.SegmentLengths.Length; i++)
            {
                var seg = page.SegmentLengths[i];
                packetSize += seg;

                if (seg < MAX_SEGMENT_SIZE)
                {
                    // Packet boundary.
                    var bodySlice = new byte[packetSize];
                    Array.Copy(page.Payload, packetStart, bodySlice, 0, packetSize);

                    if (inContinuedPacket && _continuedPacket != null && _continuedPacket.Count > 0)
                    {
                        // Stitch carried-over bytes onto the front of this packet.
                        var combined = new byte[_continuedPacket.Count + bodySlice.Length];
                        _continuedPacket.CopyTo(combined, 0);
                        Array.Copy(bodySlice, 0, combined, _continuedPacket.Count, bodySlice.Length);
                        yield return combined;
                        _continuedPacket = null;
                    }
                    else
                    {
                        yield return bodySlice;
                    }

                    inContinuedPacket = false;
                    packetStart += packetSize;
                    packetSize = 0;
                }
                // 255-segment: keep accumulating.
            }

            // Tail: if the page ended with a 255-segment, those bytes form the
            // start of a packet that continues on the next page.
            if (packetSize > 0)
            {
                var carry = new byte[packetSize];
                Array.Copy(page.Payload, packetStart, carry, 0, packetSize);
                if (inContinuedPacket && _continuedPacket != null && _continuedPacket.Count > 0)
                {
                    _continuedPacket.AddRange(carry);
                }
                else
                {
                    _continuedPacket = new List<byte>(carry);
                }
            }
        }

        // ---------- OpusHead parsing ----------

        private bool TryParseOpusHead(byte[] packet)
        {
            // RFC 7845 §5.1: minimum size is 19 bytes for channel-mapping family 0.
            if (packet.Length < 19)
            {
                Debug.LogError($"[OggOpusParser] OpusHead too small: {packet.Length} bytes");
                return false;
            }

            var version = packet[8];
            _channels = packet[9];
            _preSkip = BitConverter.ToUInt16(packet, 10);
            // Bytes 12..15 = "input sample rate" (informational; Opus always decodes at 48 kHz).
            _outputGain = BitConverter.ToInt16(packet, 16);
            // Byte 18 = channel mapping family. We support family 0 (mono / stereo);
            // family 1+ needs additional table bytes that decoder configurations rarely use.

            _sampleRate = 48000;

            if (_isVerboseLogging)
            {
                Debug.Log(
                    $"[OggOpusParser] OpusHead: v{version}, {_channels}ch, preSkip={_preSkip}, outputGain={_outputGain / 256.0f:F2}dB");
            }
            return true;
        }

        // OpusTags is informational; we accept it without further parsing.
        // The vendor / comment fields are decoder-irrelevant for our use.

        // ---------- Stream IO helpers ----------

        /// <summary>
        /// Reads exactly <paramref name="count"/> bytes into <paramref name="buffer"/>
        /// starting at <paramref name="offset"/>. Returns false when the stream
        /// runs out before that many bytes are available; the caller is responsible
        /// for restoring the read position if it cares about transactionality.
        /// </summary>
        private bool ReadAtLeast(byte[] buffer, int offset, int count)
        {
            var totalRead = 0;
            while (totalRead < count)
            {
                var bytesRead = _input.Read(buffer, offset + totalRead, count - totalRead);
                if (bytesRead == 0)
                {
                    return false;
                }
                totalRead += bytesRead;
            }
            return true;
        }

        /// <summary>
        /// Move the read position forward to the next "OggS" capture pattern in
        /// the stream, or rewind back to <paramref name="originalPosition"/> and
        /// return false if no such marker exists yet.
        /// </summary>
        private bool ScanForwardToNextOgg(long originalPosition)
        {
            // Rewind to just after the failed magic-check.
            _input.Position = originalPosition + 1;

            const int scanBufferSize = 4096;
            var scanBuffer = new byte[scanBufferSize];

            while (_input.Position < _input.Length)
            {
                var startOfWindow = _input.Position;
                var bytesRead = _input.Read(scanBuffer, 0, (int)Math.Min(scanBufferSize, _input.Length - startOfWindow));
                if (bytesRead == 0) break;

                for (var i = 0; i + 3 < bytesRead; i++)
                {
                    if (scanBuffer[i] == 'O' && scanBuffer[i + 1] == 'g'
                        && scanBuffer[i + 2] == 'g' && scanBuffer[i + 3] == 'S')
                    {
                        var foundAt = startOfWindow + i;
                        var skipped = foundAt - originalPosition;
                        Debug.LogWarning(
                            $"[OggOpusParser] Recovered: skipped {skipped} bytes of non-OGG data at position {originalPosition}");
                        _input.Position = foundAt;
                        return true;
                    }
                }

                // Step back 3 bytes so a marker straddling the buffer boundary is still found.
                _input.Position = Math.Max(startOfWindow + bytesRead - 3, startOfWindow + 1);
            }

            return false;
        }

        // ---------- Utilities ----------

        private static bool HasOggMagic(byte[] buffer)
        {
            return buffer[0] == 'O' && buffer[1] == 'g' && buffer[2] == 'g' && buffer[3] == 'S';
        }

        private static bool StartsWith(byte[] packet, string ascii)
        {
            if (packet.Length < ascii.Length) return false;
            for (var i = 0; i < ascii.Length; i++)
            {
                if (packet[i] != (byte)ascii[i]) return false;
            }
            return true;
        }

        private static int EmitPacket(byte[] outputBuffer, byte[] packet)
        {
            if (packet.Length > outputBuffer.Length)
            {
                Debug.LogWarning(
                    $"[OggOpusParser] Packet too large for caller buffer ({packet.Length} > {outputBuffer.Length}); dropping");
                return -1;
            }
            Array.Copy(packet, 0, outputBuffer, 0, packet.Length);
            return packet.Length;
        }

        private void ResetForNewLogicalStream()
        {
            _streamSerial = 0;
            _lastSequence = 0;
            _lastSequenceValid = false;
            _opusHeadParsed = false;
            _continuedPacket = null;
            _audioPackets.Clear();
            // Don't reset _channels/_sampleRate/_preSkip — they're populated by
            // the next OpusHead packet anyway, and downstream code may peek them
            // before that packet arrives.
        }

        // ---------- Internal page representation ----------

        private struct OggPage
        {
            public byte HeaderType;
            public long GranulePosition;
            public uint Serial;
            public uint Sequence;
            public byte[] SegmentLengths;
            public byte[] Payload;
        }
    }
}
