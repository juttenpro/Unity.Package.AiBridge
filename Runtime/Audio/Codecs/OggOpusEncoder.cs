using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Tsc.AIBridge.Audio.Codecs
{
    /// <summary>
    /// Wraps Opus encoded frames in OGG container format
    /// Required for Azure Speech-to-Text which expects OGG/Opus, not raw Opus frames
    /// This is a regular C# class, not a MonoBehaviour, for better performance.
    /// </summary>
    public class OggOpusEncoder
    {
        // Events
        public event Action<byte[]> OnOggPacketReady;

        // OGG state
        private ulong _granulePosition;
        private uint _pageSequenceNumber;
        private readonly byte[] _serialNumber = new byte[4];
        private bool _isFirstPage = true;
        private bool _streamStarted;
        private readonly List<byte[]> _bufferedPackets = new List<byte[]>();
        private const int MaxPacketsPerPage = 255; // OGG specification limit
        private int _totalPackets;

        // Configuration
        private int _sampleRate = 48000; // Opus internal rate
        private int _channels = 1;
        private bool _isVerboseLogging;

        // Constants
        private const string OpusHeadMagic = "OpusHead";
        private const string OpusTagsMagic = "OpusTags";

        /// <summary>
        /// Create a new OggOpusEncoder
        /// </summary>
        public OggOpusEncoder()
        {
            // Generate random serial number for this stream
            var random = new System.Random();
            random.NextBytes(_serialNumber);
        }

        public void Initialize(int sampleRate = 48000, int channels = 1, bool isVerboseLogging = false)
        {
            // Note: Opus always operates at 48kHz internally, regardless of input sample rate
            // Azure STT needs the actual Opus rate (48kHz) in the OGG headers
            _sampleRate = 48000; // Force Opus internal rate
            _channels = channels;
            _isVerboseLogging = isVerboseLogging;

            if (_isVerboseLogging)
            {
                Debug.Log($"[OggOpusEncoder] Initialized: Input {sampleRate}Hz -> Opus 48000Hz, {channels}ch");
            }
        }

        /// <summary>
        /// Start a new OGG/Opus stream
        /// </summary>
        public void StartStream()
        {
            if (_streamStarted)
            {
                Debug.LogWarning("[OggOpusEncoder] Stream already started");
                return;
            }

            _streamStarted = true;
            _isFirstPage = true;
            _pageSequenceNumber = 0;
            _granulePosition = 0;
            _bufferedPackets.Clear();

            // Send OGG headers
            var idHeader = CreateIdHeaderPage();
            OnOggPacketReady?.Invoke(idHeader);

            var commentHeader = CreateCommentHeaderPage();
            OnOggPacketReady?.Invoke(commentHeader);

            if (_isVerboseLogging)
            {
                Debug.Log($"[OggOpusEncoder] Stream started. Sent ID header ({idHeader.Length} bytes) and comment header ({commentHeader.Length} bytes)");
            }
        }

        /// <summary>
        /// Process raw Opus frame and wrap in OGG container
        /// </summary>
        public void ProcessOpusFrame(byte[] opusFrame)
        {
            if (!_streamStarted)
            {
                Debug.LogError("[OggOpusEncoder] Stream not started. Call StartStream() first.");
                return;
            }

            _bufferedPackets.Add(opusFrame);

            if (_isVerboseLogging)
            {
                Debug.Log($"[OggOpusEncoder] Buffered Opus frame: {opusFrame.Length} bytes, total buffered: {_bufferedPackets.Count}");
            }

            // Flush page when we have enough packets or too much data
            var totalSize = _bufferedPackets.Sum(p => p.Length);
            // For real-time streaming, flush more frequently - every 2 packets or 512 bytes
            if (_bufferedPackets.Count >= 2 || totalSize > 512)
            {
                FlushBufferedPackets(false);
            }
        }

        /// <summary>
        /// End the OGG/Opus stream
        /// </summary>
        public void EndStream()
        {
            if (!_streamStarted)
                return;

            if (_isVerboseLogging)
            {
                Debug.Log($"[OggOpusEncoder] EndStream called with {_bufferedPackets.Count} buffered packets");
            }

            // Flush any remaining packets with EOS flag
            FlushBufferedPackets(true);

            _streamStarted = false;

            if (_isVerboseLogging)
            {
                Debug.Log($"[OggOpusEncoder] Stream ended. Total pages: {_pageSequenceNumber}");
            }
        }

        /// <summary>
        /// Flush buffered Opus packets as OGG page
        /// </summary>
        private void FlushBufferedPackets(bool isLastPage)
        {
            if (_bufferedPackets.Count == 0 && !isLastPage)
                return;

            // For the last page, we need to send even if empty to signal EOS
            if (_bufferedPackets.Count == 0 && isLastPage)
            {
                // Create empty EOS page
                var emptyPage = CreateDataPage(new byte[0][], isLastPage);
                OnOggPacketReady?.Invoke(emptyPage);
                return;
            }

            var page = CreateDataPage(_bufferedPackets.ToArray(), isLastPage);
            OnOggPacketReady?.Invoke(page);

            if (_isVerboseLogging && (_pageSequenceNumber % 10 == 0 || isLastPage))
            {
                Debug.Log($"[OggOpusEncoder] Page {_pageSequenceNumber}: {_bufferedPackets.Count} packets, {page.Length} bytes total");
            }

            _bufferedPackets.Clear();
        }

        /// <summary>
        /// Create OGG page with Opus ID header
        /// </summary>
        private byte[] CreateIdHeaderPage()
        {
            var opusHead = new List<byte>();

            // OpusHead magic signature
            opusHead.AddRange(System.Text.Encoding.ASCII.GetBytes(OpusHeadMagic));

            // Version (1)
            opusHead.Add(1);

            // Channel count
            opusHead.Add((byte)_channels);

            // Pre-skip (312 samples standard for 48kHz)
            opusHead.Add(0x38);
            opusHead.Add(0x01);

            // Input sample rate (little endian)
            opusHead.Add((byte)(_sampleRate & 0xFF));
            opusHead.Add((byte)((_sampleRate >> 8) & 0xFF));
            opusHead.Add((byte)((_sampleRate >> 16) & 0xFF));
            opusHead.Add((byte)((_sampleRate >> 24) & 0xFF));

            // Output gain (0)
            opusHead.Add(0);
            opusHead.Add(0);

            // Channel mapping family (0 = mono/stereo)
            opusHead.Add(0);

            return CreateOggPage(new[] { opusHead.ToArray() }, 0x02); // BOS flag
        }

        /// <summary>
        /// Create OGG page with Opus comment header
        /// </summary>
        private byte[] CreateCommentHeaderPage()
        {
            var opusTags = new List<byte>();

            // OpusTags magic signature
            opusTags.AddRange(System.Text.Encoding.ASCII.GetBytes(OpusTagsMagic));

            // Vendor string
            var vendor = "Unity VR Training 1.0";
            var vendorBytes = System.Text.Encoding.UTF8.GetBytes(vendor);

            // Vendor string length (little endian)
            opusTags.Add((byte)(vendorBytes.Length & 0xFF));
            opusTags.Add((byte)((vendorBytes.Length >> 8) & 0xFF));
            opusTags.Add((byte)((vendorBytes.Length >> 16) & 0xFF));
            opusTags.Add((byte)((vendorBytes.Length >> 24) & 0xFF));

            // Vendor string
            opusTags.AddRange(vendorBytes);

            // User comment count (0)
            opusTags.Add(0);
            opusTags.Add(0);
            opusTags.Add(0);
            opusTags.Add(0);

            return CreateOggPage(new[] { opusTags.ToArray() }, 0x00);
        }

        /// <summary>
        /// Create OGG page with Opus data packets
        /// </summary>
        private byte[] CreateDataPage(byte[][] packets, bool isLastPage)
        {
            byte headerType = 0x00;
            if (_isFirstPage)
            {
                headerType |= 0x02; // BOS (beginning of stream)
                _isFirstPage = false;
            }
            if (isLastPage)
            {
                headerType |= 0x04; // EOS (end of stream)
            }

            // Update granule position (960 samples per 20ms frame at 48kHz)
            // But we need to account for the 312 sample pre-skip
            _totalPackets += packets.Length;
            _granulePosition = (ulong)(_totalPackets * 960) - 312;

            return CreateOggPage(packets, headerType);
        }

        /// <summary>
        /// Create an OGG page with given packets
        /// </summary>
        private byte[] CreateOggPage(byte[][] packets, byte headerType)
        {
            var page = new List<byte>();

            // OGG page header
            page.AddRange(System.Text.Encoding.ASCII.GetBytes("OggS")); // Capture pattern
            page.Add(0); // Version
            page.Add(headerType); // Header type

            // Granule position (8 bytes, little endian)
            var granuleBytes = BitConverter.GetBytes(_granulePosition);
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(granuleBytes);
            }
            page.AddRange(granuleBytes);

            // Serial number (4 bytes)
            page.AddRange(_serialNumber);

            // Page sequence number (4 bytes, little endian)
            page.Add((byte)(_pageSequenceNumber & 0xFF));
            page.Add((byte)((_pageSequenceNumber >> 8) & 0xFF));
            page.Add((byte)((_pageSequenceNumber >> 16) & 0xFF));
            page.Add((byte)((_pageSequenceNumber >> 24) & 0xFF));
            _pageSequenceNumber++;

            // Checksum placeholder (4 bytes) - will be calculated later
            var checksumIndex = page.Count;
            page.Add(0);
            page.Add(0);
            page.Add(0);
            page.Add(0);

            // Page segments (number of lacing values)
            var lacingValues = new List<byte>();
            foreach (var packet in packets)
            {
                var remaining = packet.Length;
                while (remaining >= 255)
                {
                    lacingValues.Add(255);
                    remaining -= 255;
                }
                lacingValues.Add((byte)remaining);
            }
            page.Add((byte)lacingValues.Count);

            // Lacing values
            page.AddRange(lacingValues);

            // Packet data
            foreach (var packet in packets)
            {
                page.AddRange(packet);
            }

            // Calculate and set CRC32 checksum
            var pageArray = page.ToArray();
            var checksum = CalculateOggChecksum(pageArray);
            pageArray[checksumIndex] = (byte)(checksum & 0xFF);
            pageArray[checksumIndex + 1] = (byte)((checksum >> 8) & 0xFF);
            pageArray[checksumIndex + 2] = (byte)((checksum >> 16) & 0xFF);
            pageArray[checksumIndex + 3] = (byte)((checksum >> 24) & 0xFF);

            return pageArray;
        }

        /// <summary>
        /// Calculate OGG page checksum
        /// </summary>
        private static uint CalculateOggChecksum(byte[] data)
        {
            uint crc = 0;

            for (var i = 0; i < data.Length; i++)
            {
                crc = (crc << 8) ^ OggCrcTable[((crc >> 24) & 0xff) ^ data[i]];
            }

            return crc;
        }

        // OGG CRC lookup table
        private static readonly uint[] OggCrcTable = GenerateOggCrcTable();

        private static uint[] GenerateOggCrcTable()
        {
            const uint polynomial = 0x04c11db7;
            var table = new uint[256];

            for (uint i = 0; i < 256; i++)
            {
                var crc = i << 24;
                for (var j = 0; j < 8; j++)
                {
                    if ((crc & 0x80000000) != 0)
                        crc = (crc << 1) ^ polynomial;
                    else
                        crc <<= 1;
                }
                table[i] = crc;
            }

            return table;
        }
    }
}