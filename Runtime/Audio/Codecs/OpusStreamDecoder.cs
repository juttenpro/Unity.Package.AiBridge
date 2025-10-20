using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using OpusSharp.Core;

namespace Tsc.AIBridge.Audio.Codecs
{
    /// <summary>
    /// Opus stream decoder - treats all incoming data as ONE continuous stream.
    /// NO stream boundary resets. SIMPLICITY FIRST principle.
    /// This is a normal class, not a MonoBehaviour.
    /// NOTE: Requires OpusSharp package to be installed.
    /// </summary>
    public class OpusStreamDecoder : IDisposable
    {
        // Events
        public event Action<float[]> OnAudioDecoded;

        // Events for logging
        public static event Action<string> OnDecoderError;
        public static event Action<int, int> OnDecoderInitialized;

        // Components
        private OggOpusParser _oggParser;
        private OpusDecoder _opusDecoder;

        // Single continuous stream
        private MemoryStream _continuousStream;

        // Buffers
        private byte[] _opusPacketBuffer;
        private float[] _decodedSamples;

        // Configuration
        private int _sampleRate = 16000;
        private int _channels = 1;
        private int _maxFrameSize;

        // State
        private bool _isInitialized;
        private bool _parserInitialized;
        private long _totalBytesReceived;
        private int _totalPacketsDecoded;
        private long _totalSamplesDecoded;
        private long _streamPosition;
        private bool _isVerboseLogging;
        private bool _isDisposed;

        /// <summary>
        /// Initializes the SIMPLIFIED decoder for continuous stream processing.
        /// </summary>
        public OpusStreamDecoder(bool isVerboseLogging = false)
        {
            _isVerboseLogging = isVerboseLogging;

            try
            {
                // Create single continuous stream
                _continuousStream = new MemoryStream();

                // Create OGG parser - ONCE for entire session
                _oggParser = new OggOpusParser();

                // Pre-allocate buffers
                _opusPacketBuffer = new byte[4000]; // Max Opus packet size
                _maxFrameSize = (int)(0.120f * 48000); // 120ms at 48kHz
                _decodedSamples = new float[_maxFrameSize * 2]; // Stereo buffer

                if (_isVerboseLogging)
                    Debug.Log("[OpusStreamDecoder] Initialized - continuous stream mode");
                _isInitialized = true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[OpusStreamDecoder] Failed to initialize: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Process incoming data - NO stream boundary detection, just continuous processing.
        /// SIMPLICITY FIRST: All data is part of ONE stream.
        /// </summary>
        public void ProcessData(byte[] data)
        {
            if (!_isInitialized)
            {
                Debug.LogError("[OpusStreamDecoder] Not initialized");
                return;
            }

            if (data == null || data.Length == 0)
            {
                Debug.LogWarning("[OpusStreamDecoder] ProcessData called with null/empty data");
                return;
            }


            if (_isVerboseLogging)
                Debug.Log($"[OpusStreamDecoder] Processing chunk: {data.Length} bytes (total: {_totalBytesReceived + data.Length})");

            // CRITICAL: Seek to end before writing to ensure we append, not overwrite!
            _continuousStream.Seek(0, SeekOrigin.End);
            _continuousStream.Write(data, 0, data.Length);
            _totalBytesReceived += data.Length;

            // Use the WORKING approach from yesterday
            try
            {
                if (_continuousStream != null && _streamPosition < _continuousStream.Length)
                {
                    _continuousStream.Position = _streamPosition;
                    ProcessAvailablePackets();
                    _streamPosition = _continuousStream.Position;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[OpusStreamDecoder] Error: {ex.Message}");
                OnDecoderError?.Invoke(ex.Message);
            }
        }

        /// <summary>
        /// Process all available Opus packets - no stream boundary handling (WORKING VERSION)
        /// </summary>
        private void ProcessAvailablePackets()
        {
            // Initialize parser and decoder if needed
            if (!_parserInitialized)
            {
                if (!_oggParser.Initialize(_continuousStream, _isVerboseLogging))
                {
                    Debug.LogError("[OpusStreamDecoder] Failed to initialize OGG parser");
                    return;
                }

                // Create decoder from parsed header info
                _opusDecoder = new OpusDecoder(_oggParser.SampleRate, _oggParser.Channels);
                _channels = _oggParser.Channels;
                _sampleRate = _oggParser.SampleRate;

                OnDecoderInitialized?.Invoke(_sampleRate, _channels);
                _parserInitialized = true;

                if (_isVerboseLogging)
                    Debug.Log($"[OpusStreamDecoder] Decoder initialized: {_sampleRate}Hz, {_channels}ch");
            }

            if (_opusDecoder == null)
                return;

            var packetsThisBatch = 0;

            while (true)
            {
                Array.Clear(_opusPacketBuffer, 0, _opusPacketBuffer.Length);

                var packetSize = _oggParser.ReadNextOpusPacket(_opusPacketBuffer);
                if (packetSize <= 0)
                    break; // No more packets available

                try
                {
                    // Use the WORKING signature from the previous version
                    var frameSize = 960; // Standard 20ms at 48kHz
                    var decodedSampleCount = _opusDecoder.Decode(
                        _opusPacketBuffer,
                        packetSize,
                        _decodedSamples,
                        frameSize,
                        false
                    );

                    if (decodedSampleCount > 0)
                    {
                        // Send decoded audio
                        var outputSamples = new float[decodedSampleCount * _channels];
                        Array.Copy(_decodedSamples, outputSamples, outputSamples.Length);

                        OnAudioDecoded?.Invoke(outputSamples);

                        _totalPacketsDecoded++;
                        _totalSamplesDecoded += decodedSampleCount;
                        packetsThisBatch++;
                    }
                }
                catch (Exception ex) when (ex.Message.Contains("OPUS_INVALID_PACKET"))
                {
                    // Skip invalid packets silently - they're rare and not critical
                    if (_isVerboseLogging)
                        Debug.LogWarning($"[OpusStreamDecoder] Skipped invalid packet");
                }
            }

            // Only log packet processing in verbose mode
            if (_isVerboseLogging && packetsThisBatch > 0 && _totalPacketsDecoded % 100 == 0)
            {
                Debug.Log($"[OpusStreamDecoder] Processed {packetsThisBatch} packets (total: {_totalPacketsDecoded})");
            }
        }

        /// <summary>
        /// Flush any remaining audio - processes any remaining data in the stream
        /// </summary>
        public float[] FlushRemainingAudio()
        {
            if (_isVerboseLogging)
                Debug.Log($"[OpusStreamDecoder] Flushing - {_totalPacketsDecoded} packets processed");

            var collectedAudio = new List<float>();

            Action<float[]> collector = (samples) => {
                collectedAudio.AddRange(samples);
            };

            OnAudioDecoded += collector;

            try
            {
                // Process any remaining data
                if (_continuousStream != null && _streamPosition < _continuousStream.Length)
                {
                    _continuousStream.Position = _streamPosition;
                    ProcessAvailablePackets();
                    _streamPosition = _continuousStream.Position;
                }
            }
            finally
            {
                OnAudioDecoded -= collector;
            }

            var result = collectedAudio.Count > 0 ? collectedAudio.ToArray() : null;

            if (_isVerboseLogging && result != null)
                Debug.Log($"[OpusStreamDecoder] Flushed {result.Length} samples");

            return result;
        }

        /// <summary>
        /// Get current packet count for debugging
        /// </summary>
        public int GetPacketCount()
        {
            return _totalPacketsDecoded;
        }

        /// <summary>
        /// Reset the decoder state immediately without flushing
        /// Used for interruptions where we want to stop processing instantly
        /// </summary>
        public void Reset()
        {
            if (_isVerboseLogging)
                Debug.Log($"[OpusStreamDecoder] Reset called after {_totalPacketsDecoded} packets, {_totalBytesReceived} bytes");

            // CRITICAL FIX: Dispose and recreate stream instead of SetLength()
            // SetLength() fails if stream is already disposed or in read-only mode
            // This happens during Unity OnDestroy() cleanup sequences
            if (_continuousStream != null)
            {
                _continuousStream.Dispose();
                _continuousStream = new MemoryStream();
            }
            _streamPosition = 0;

            // Reset parser state
            _parserInitialized = false;
            _oggParser = new OggOpusParser();

            // Dispose and recreate decoder
            _opusDecoder?.Dispose();
            _opusDecoder = null;

            // Reset counters
            _totalPacketsDecoded = 0;
            _totalSamplesDecoded = 0;
            _totalBytesReceived = 0;

            if (_isVerboseLogging)
                Debug.Log("[OpusStreamDecoder] Reset - cleared all buffers without flushing");
        }

        public void Dispose()
        {
            if (_isDisposed) return;

            _continuousStream?.Dispose();
            _opusDecoder?.Dispose();
            _oggParser = null;
            _isDisposed = true;

            if (_isVerboseLogging)
                Debug.Log($"[OpusStreamDecoder] Disposed - Total decoded: {_totalPacketsDecoded} packets, {_totalSamplesDecoded} samples");
        }
    }
}