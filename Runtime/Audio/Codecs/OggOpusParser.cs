using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Tsc.AIBridge.Audio.Codecs
{
    /// <summary>
    /// Robust Ogg/Opus container parser for streaming audio from ElevenLabs
    /// Implements the Ogg bitstream format specification with proper streaming support
    /// </summary>
    public class OggOpusParser
    {
        // Ogg page header constants
        //private const string OGG_CAPTURE_PATTERN = "OggS";
        //private const byte OGG_VERSION = 0;
        //private const byte FLAG_CONTINUED = 0x01;
        //private const byte FLAG_FIRST = 0x02;
        //private const byte FLAG_LAST = 0x04;

        // Opus header signatures
        private const string OPUS_HEAD_SIGNATURE = "OpusHead";
        private const string OPUS_TAGS_SIGNATURE = "OpusTags";

        // Parser state
        private Stream _inputStream;
        private readonly byte[] _headerBuffer = new byte[282]; // Max Ogg page header size (27 + 255)
        private readonly List<byte[]> _pendingPackets = new();
        private bool _headersParsed;
        private int _channels = 1;
        private int _sampleRate = 48000;
        //private bool _isEndOfStream;
        private short _outputGain; // Q7.8 format gain from OpusHead
        private uint _lastPageSequence = uint.MaxValue; // Track last page sequence number
        private readonly HashSet<uint> _seenPageSequences = new(); // Track all seen sequences

        // Stream buffering for incomplete reads
        private readonly MemoryStream _streamBuffer = new();
        private bool _isVerboseLogging;

        // Continued packet handling across page boundaries
        private List<byte> _continuedPacket = new();

        // Opus stream info
        public int Channels => _channels;
        public int SampleRate => _sampleRate;
        //public bool IsEndOfStream => _isEndOfStream;
        //public float OutputGain => _outputGain / 256.0f; // Convert from Q7.8 to linear gain
        public ushort PreSkip { get; private set; } // Samples to skip at start

        /// <summary>
        /// Initialize the parser with an input stream
        /// </summary>
        public bool Initialize(Stream inputStream, bool isVerboseLogging = false)
        {
            _isVerboseLogging = isVerboseLogging;
            if (inputStream == null || !inputStream.CanRead)
            {
                Debug.LogError("[OggOpusParser] Invalid input stream");
                return false;
            }

            _inputStream = inputStream;
            _headersParsed = false;
            //_isEndOfStream = false;
            _pendingPackets.Clear();
            _continuedPacket.Clear(); // Clear any continued packet state
            _streamBuffer.SetLength(0);
            _streamBuffer.Position = 0;
            _lastPageSequence = uint.MaxValue; // Reset page sequence tracking
            _seenPageSequences.Clear(); // Clear seen sequences

            if(_isVerboseLogging)
                Debug.Log("[OggOpusParser] Initialized Ogg/Opus parser");

            return true;
        }

        /// <summary>
        /// Read the next Opus packet from the Ogg stream
        /// </summary>
        /// <param name="packetBuffer">Buffer to receive the packet data</param>
        /// <returns>Size of the packet, or -1 on error, 0 on end of stream</returns>
        public int ReadNextOpusPacket(byte[] packetBuffer)
        {
            try
            {
                // Parse headers if not done yet
                if (!_headersParsed)
                {
                    if (!ParseHeaders())
                    {
                        Debug.LogError("[OggOpusParser] Failed to parse Opus headers");
                        return -1;
                    }
                }

                // Return any pending packets first
                if (_pendingPackets.Count > 0)
                {
                    var packet = _pendingPackets[0];
                    _pendingPackets.RemoveAt(0);

                    if (packet.Length <= packetBuffer.Length)
                    {
                        Array.Copy(packet, 0, packetBuffer, 0, packet.Length);
                        return packet.Length;
                    }
                    else
                    {
                        Debug.LogWarning($"[OggOpusParser] Packet too large: {packet.Length} > {packetBuffer.Length}");
                        return -1;
                    }
                }

                // Read next Ogg page
                if (!ReadOggPage())
                {
                    //_isEndOfStream = true;
                    return 0;
                }

                // Return first packet from the page
                if (_pendingPackets.Count > 0)
                {
                    var packet = _pendingPackets[0];
                    _pendingPackets.RemoveAt(0);

                    if (packet.Length <= packetBuffer.Length)
                    {
                        Array.Copy(packet, 0, packetBuffer, 0, packet.Length);
                        return packet.Length;
                    }
                }

                return 0;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[OggOpusParser] Error reading packet: {ex.Message}");
                return -1;
            }
        }

        /// <summary>
        /// Parse Opus headers from the Ogg stream
        /// </summary>
        private bool ParseHeaders()
        {
            if(_isVerboseLogging)
                Debug.Log("[OggOpusParser] Parsing Opus headers...");

            // First page should contain OpusHead
            if (!ReadOggPage())
            {
                Debug.LogError("[OggOpusParser] Failed to read first Ogg page");
                return false;
            }

            if (_pendingPackets.Count == 0)
            {
                Debug.LogError("[OggOpusParser] No packets in first page");
                return false;
            }

            var headPacket = _pendingPackets[0];
            _pendingPackets.RemoveAt(0);

            if (!ParseOpusHead(headPacket))
            {
                Debug.LogError("[OggOpusParser] Failed to parse OpusHead");
                return false;
            }

            // Second page should contain OpusTags
            if (!ReadOggPage())
            {
                Debug.LogError("[OggOpusParser] Failed to read second Ogg page");
                return false;
            }

            if (_pendingPackets.Count > 0)
            {
                var tagsPacket = _pendingPackets[0];
                _pendingPackets.RemoveAt(0);
                ParseOpusTags(tagsPacket); // Tags are optional, don't fail if parsing fails
            }

            _headersParsed = true;

            if(_isVerboseLogging)
                Debug.Log($"[OggOpusParser] Headers parsed successfully: {_sampleRate}Hz, {_channels} channel(s)");

            return true;
        }

        /// <summary>
        /// Parse OpusHead packet
        /// </summary>
        private bool ParseOpusHead(byte[] packet)
        {
            if (packet.Length < 19) // Minimum OpusHead size
            {
                Debug.LogError($"[OggOpusParser] OpusHead packet too small: {packet.Length}");
                return false;
            }

            // Check signature
            var signature = Encoding.ASCII.GetString(packet, 0, 8);
            if (signature != OPUS_HEAD_SIGNATURE)
            {
                Debug.LogError($"[OggOpusParser] Invalid OpusHead signature: {signature}");
                return false;
            }

            // Parse header fields
            var version = packet[8];
            _channels = packet[9];
            PreSkip = BitConverter.ToUInt16(packet, 10);
            var inputSampleRate = BitConverter.ToUInt32(packet, 12);
            _outputGain = BitConverter.ToInt16(packet, 16);
            var mappingFamily = packet[18];

            // Opus always decodes at 48kHz
            _sampleRate = 48000;

            // Convert output gain from Q7.8 format to dB
            var gainDb = _outputGain / 256.0f;
            var linearGain = (float)Math.Pow(10.0, gainDb / 20.0);

            if (_isVerboseLogging)
            {
                Debug.Log($"[OggOpusParser] OpusHead: v{version}, {_channels}ch, preSkip={PreSkip}, inputRate={inputSampleRate}");
                Debug.Log($"[OggOpusParser] Output gain: {_outputGain} (Q7.8) = {gainDb:F2}dB = {linearGain:F3}x linear, mapping={mappingFamily}");
            }
            return true;
        }

        /// <summary>
        /// Parse OpusTags packet (optional)
        /// </summary>
        private void ParseOpusTags(byte[] packet)
        {
            if (packet.Length < 8)
            {
                return;
            }

            var signature = Encoding.ASCII.GetString(packet, 0, 8);
            if (signature != OPUS_TAGS_SIGNATURE)
            {
                return;
            }

            if(_isVerboseLogging)
             Debug.Log("[OggOpusParser] OpusTags packet found (skipping details)");
        }

        /// <summary>
        /// Read bytes from stream with partial read support
        /// </summary>
        /// <returns>Number of bytes actually read</returns>
        private int ReadStreamBytesPartial(byte[] buffer, int offset, int count)
        {
            var totalRead = 0;
            var attempts = 0;
            const int maxAttempts = 20; // Fewer attempts for partial reads

            while (totalRead < count && attempts < maxAttempts)
            {
                var bytesRead = _inputStream.Read(buffer, offset + totalRead, count - totalRead);

                if (bytesRead == 0)
                {
                    // Check if we're at stream end
                    if (_inputStream.Position == _inputStream.Length)
                    {
                        // Return what we have so far
                        break;
                    }

                    // In Unity, we should return and try again next frame
                    // instead of blocking the thread
                    attempts++;
                    if (attempts >= maxAttempts)
                    {
                        break; // Give up after max attempts
                    }
                    // Don't block - let caller retry
                    continue;
                }
                else
                {
                    totalRead += bytesRead;
                    attempts = 0; // Reset attempts on successful read
                }
            }

            return totalRead;
        }

        /// <summary>
        /// Read bytes from stream with buffering support for incomplete reads
        /// </summary>
        private bool ReadStreamBytes(byte[] buffer, int offset, int count)
        {
            var totalRead = 0;
            var attempts = 0;
            const int maxAttempts = 50; // Increased from 10 for streaming scenarios

            while (totalRead < count && attempts < maxAttempts)
            {
                var bytesRead = _inputStream.Read(buffer, offset + totalRead, count - totalRead);

                if (bytesRead == 0)
                {
                    // No more data available
                    if (totalRead == 0)
                    {
                        return false; // End of stream
                    }

                    // For streaming, check if we're at a natural stream boundary
                    if (_inputStream.Position == _inputStream.Length)
                    {
                        // We've read all available data in the stream
                        // This is not necessarily an error in streaming scenarios
                        // Only log this warning in verbose mode
                        if (_isVerboseLogging)
                        {
                            Debug.Log($"[OggOpusParser] Partial read: got {totalRead}/{count} bytes at stream end");
                        }
                        return false; // Signal end of current data
                    }

                    // Don't block - return false to let caller retry
                    // The streaming decoder will handle retrying
                    attempts++;
                    if (attempts >= maxAttempts)
                    {
                        return false; // Give up after max attempts
                    }
                }
                else
                {
                    totalRead += bytesRead;
                    attempts = 0; // Reset attempts on successful read
                }
            }

            if (totalRead < count)
            {
                Debug.LogWarning($"[OggOpusParser] Incomplete read after {maxAttempts} attempts: {totalRead}/{count} bytes");
            }

            return totalRead == count;
        }

        /// <summary>
        /// Read and parse a single Ogg page with streaming support
        /// </summary>
        private bool ReadOggPage()
        {
            // Read page header
            if (!ReadStreamBytes(_headerBuffer, 0, 27))
            {
                return false; // End of stream or error
            }

            // Verify capture pattern
            if (_headerBuffer[0] != 'O' || _headerBuffer[1] != 'g' ||
                _headerBuffer[2] != 'g' || _headerBuffer[3] != 'S')
            {
                // Different ElevenLabs models may produce different stream formats
                // This is not necessarily an error - could be data from a different model
                // Only log in debug builds to reduce console spam
                if (Debug.isDebugBuild && _isVerboseLogging)
                {
                    Debug.LogWarning($"[OggOpusParser] Non-OGG data at position {_inputStream.Position - 27}: {_headerBuffer[0]:X2} {_headerBuffer[1]:X2} {_headerBuffer[2]:X2} {_headerBuffer[3]:X2}");
                }

                // Try to find the next valid OGG header by scanning ahead
                // Increased from 1024 to 16384 bytes to handle larger gaps in streaming audio
                // This prevents premature stream termination when non-OGG data spans multiple packets
                var scanLimit = Math.Min(16384, _inputStream.Length - _inputStream.Position);
                for (var i = 0; i < scanLimit - 3; i++)
                {
                    if (_inputStream.ReadByte() == 'O' &&
                        _inputStream.ReadByte() == 'g' &&
                        _inputStream.ReadByte() == 'g' &&
                        _inputStream.ReadByte() == 'S')
                    {
                        // Found a potential OGG header, rewind to start of it
                        _inputStream.Position -= 4;
                        if (_isVerboseLogging)
                        {
                            Debug.Log($"[OggOpusParser] Found next OGG header at position {_inputStream.Position} after skipping {i} bytes");
                        }
                        // Try parsing again from this position (recursive call)
                        return ReadOggPage();
                    }
                    // Rewind 3 bytes to check overlapping patterns
                    _inputStream.Position -= 3;
                }

                // No valid OGG header found in scan range
                // In streaming scenarios, this might be temporary - more data could arrive later
                // Only fail if we've truly reached the end of the available stream data
                if (_inputStream.Position >= _inputStream.Length)
                {
                    // We've scanned all available data and found no OGG header
                    // This is likely the end of the stream or corrupt data
                    Debug.LogWarning($"[OggOpusParser] No OGG header found after scanning {scanLimit} bytes. Stream may be corrupt or ended.");
                    return false;
                }

                // More data might arrive in streaming - return false but don't log as error
                return false;
            }

            // Parse header fields
            //var version = _headerBuffer[4];
            //var headerType = _headerBuffer[5];
            //var granulePosition = BitConverter.ToUInt64(_headerBuffer, 6);
            //var serialNumber = BitConverter.ToUInt32(_headerBuffer, 14);
            var pageSequence = BitConverter.ToUInt32(_headerBuffer, 18);
            //var checksum = BitConverter.ToUInt32(_headerBuffer, 22);
            var pageSegments = _headerBuffer[26];

            // Check page sequence continuity
            if (_seenPageSequences.Contains(pageSequence))
            {
                // We've seen this page before - likely due to rewind
                // This is OK in streaming scenarios
            }
            else
            {
                // New page - check continuity
                if (_lastPageSequence != uint.MaxValue)
                {
                    var expectedSequence = _lastPageSequence + 1;
                    if (pageSequence != expectedSequence && pageSequence > _lastPageSequence)
                    {
                        // Only warn if it's a forward jump (not a rewind)
                        Debug.LogWarning($"[OggOpusParser] Page sequence jump! Expected {expectedSequence}, got {pageSequence}. Possible data loss.");
                    }
                }
                _lastPageSequence = pageSequence;
            }

            _seenPageSequences.Add(pageSequence);

            // Read segment table
            if (!ReadStreamBytes(_headerBuffer, 27, pageSegments))
            {
                // This can happen at the end of a stream, especially for single-chunk streams
                // Check if we're at the natural end of the stream
                if (_inputStream.Position >= _inputStream.Length || _inputStream.Length == 0)
                {
                    // This is a normal end-of-stream condition, not an error
                    if (_isVerboseLogging)
                    {
                        Debug.Log("[OggOpusParser] Reached end of stream while reading segment table - this is normal for single-chunk streams");
                    }
                    return false; // Signal end of stream gracefully
                }
                else
                {
                    // This is an actual error - we expected more data but couldn't read it
                    Debug.LogError($"[OggOpusParser] Failed to read segment table at position {_inputStream.Position}, stream length {_inputStream.Length}");
                    return false;
                }
            }

            // Calculate total page size
            var totalPageSize = 0;
            for (var i = 0; i < pageSegments; i++)
            {
                totalPageSize += _headerBuffer[27 + i];
            }

            // Read page data with streaming support
            var pageData = new byte[totalPageSize];

            // Try to read the page data, but be more lenient for streaming
            if (totalPageSize > 0)
            {
                var actualBytesRead = ReadStreamBytesPartial(pageData, 0, totalPageSize);
                if (actualBytesRead <= 0)
                {
                    Debug.LogWarning("[OggOpusParser] No page data available");
                    return false;
                }

                if (actualBytesRead < totalPageSize)
                {
                    // In streaming scenarios, partial pages are common at chunk boundaries
                    // This is normal behavior for streaming - suppress warning
                    // Debug.LogWarning($"[OggOpusParser] Partial page read: {actualBytesRead}/{totalPageSize} bytes at position {_inputStream.Position}");

                    // IMPORTANT: We need to rewind the stream to before this page header
                    // so we can retry reading this page when more data arrives
                    var rewindPosition = _inputStream.Position - actualBytesRead - 27 - pageSegments;
                    if (rewindPosition >= 0 && _inputStream.CanSeek)
                    {
                        _inputStream.Position = rewindPosition;

                        if(_isVerboseLogging)
                            Debug.Log($"[OggOpusParser] Rewound stream to position {rewindPosition} to retry this page later");
                    }

                    // Return false to indicate no complete page available right now
                    return false;
                }
            }

            // Extract packets from page
            var dataOffset = 0;
            var currentPacket = new List<byte>();

            // If there's a continued packet from previous page, start with that
            if (_continuedPacket.Count > 0)
            {
                currentPacket.AddRange(_continuedPacket);
                _continuedPacket.Clear();

                if (_isVerboseLogging)
                    Debug.Log($"[OggOpusParser] Continuing packet from previous page ({currentPacket.Count} bytes so far)");
            }

            for (var i = 0; i < pageSegments; i++)
            {
                int segmentSize = _headerBuffer[27 + i];

                // Add segment to current packet
                if (segmentSize > 0)
                {
                    currentPacket.AddRange(new ArraySegment<byte>(pageData, dataOffset, segmentSize));
                    dataOffset += segmentSize;
                }

                // If segment size < 255, it's the end of a packet
                if (segmentSize < 255)
                {
                    if (currentPacket.Count > 0)
                    {
                        _pendingPackets.Add(currentPacket.ToArray());
                        currentPacket.Clear();
                    }
                }
            }

            // Handle continued packet (spans to next page)
            if (currentPacket.Count > 0)
            {
                // Last segment was 255, packet continues in next page
                _continuedPacket.AddRange(currentPacket);

                if (_isVerboseLogging)
                    Debug.Log($"[OggOpusParser] Packet continues to next page ({_continuedPacket.Count} bytes buffered)");
            }

            return true;
        }
    }
}