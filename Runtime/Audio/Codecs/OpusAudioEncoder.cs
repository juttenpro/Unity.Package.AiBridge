using System;
using UnityEngine;

// Use OpusSharp when the define is available
#if OPUSSHARP_AVAILABLE
using OpusSharp.Core;

namespace Tsc.AIBridge.Audio.Codecs
{
    /// <summary>
    /// High-performance Opus encoder using OpusSharp native library.
    /// Optimized for low-latency speech encoding in VR training scenarios.
    /// This is a normal class, not a MonoBehaviour.
    /// NOTE: Requires OpusSharp package to be installed.
    /// </summary>
    public class OpusAudioEncoder : IDisposable
    {
        // Events
        public event Action<byte[]> OnAudioEncoded;

        // Properties
        public bool IsRecording => _isRecording;
        public int FrameSizeMs => (_frameSize * 1000) / _sampleRate;

        // Encoder instance
        private OpusEncoder _encoder;

        // Configuration
        private int _sampleRate;
        private int _frameSize;

        // Buffers
        private float[] _sampleBuffer;
        private byte[] _opusBuffer;
        private float[] _accumulationBuffer;
        private int _accumulationIndex;

        // State
        private bool _isRecording;
        private int _frameCounter;
        private bool _isVerboseLogging;
        private bool _isDisposed;

        /// <summary>
        /// Initialize the Opus encoder
        /// </summary>
        public OpusAudioEncoder(int sampleRate = 16000, int channels = 1, int bitrate = 24000, bool isVerboseLogging = false)
        {
            _isVerboseLogging = isVerboseLogging;
            _sampleRate = sampleRate;

            // 20ms frame size is optimal for low latency
            _frameSize = sampleRate / 50; // 20ms at given sample rate

            try
            {
                // Create encoder with OpusSharp
                _encoder = new OpusEncoder(sampleRate, channels, OpusPredefinedValues.OPUS_APPLICATION_VOIP);

                // Initialize buffers
                _sampleBuffer = new float[_frameSize * channels];
                _opusBuffer = new byte[4000]; // Max Opus frame size
                _accumulationBuffer = new float[sampleRate * channels]; // 1 second buffer

                if (_isVerboseLogging)
                {
                    Debug.Log($"[OpusAudioEncoder] Initialized with OpusSharp: {sampleRate}Hz, {channels}ch, {bitrate}bps");
                    Debug.Log($"[OpusAudioEncoder] Frame size: {_frameSize} samples ({FrameSizeMs}ms)");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[OpusAudioEncoder] Failed to initialize: {e.Message}");
                throw;
            }
        }

        /// <summary>
        /// Starts the audio recording/encoding process.
        /// </summary>
        public void StartRecording()
        {
            if (_isRecording) return;

            _isRecording = true;
            _accumulationIndex = 0;
            _frameCounter = 0;
            Array.Clear(_accumulationBuffer, 0, _accumulationBuffer.Length);

            if (_isVerboseLogging)
                Debug.Log("[OpusAudioEncoder] Started recording");
        }

        /// <summary>
        /// Stops the audio recording/encoding process.
        /// </summary>
        public void StopRecording()
        {
            if (!_isRecording) return;

            _isRecording = false;

            // Encode any remaining samples
            if (_accumulationIndex > 0)
            {
                FlushRemainingAudio();
            }

            if (_isVerboseLogging)
                Debug.Log($"[OpusAudioEncoder] Stopped recording - Total frames: {_frameCounter}");
        }

        /// <summary>
        /// Process audio data from microphone
        /// </summary>
        public void ProcessAudioData(float[] samples)
        {
            if (!_isRecording || samples == null || samples.Length == 0)
            {
                return;
            }

            // Add samples to accumulation buffer
            var samplesToProcess = samples.Length;
            var sourceIndex = 0;

            while (samplesToProcess > 0)
            {
                var spaceAvailable = _accumulationBuffer.Length - _accumulationIndex;
                var samplesToCopy = Math.Min(samplesToProcess, spaceAvailable);

                Array.Copy(samples, sourceIndex, _accumulationBuffer, _accumulationIndex, samplesToCopy);
                _accumulationIndex += samplesToCopy;
                sourceIndex += samplesToCopy;
                samplesToProcess -= samplesToCopy;

                // Process complete frames
                while (_accumulationIndex >= _frameSize)
                {
                    EncodeFrame();
                }
            }
        }

        private void EncodeFrame()
        {
            // Copy frame from accumulation buffer
            Array.Copy(_accumulationBuffer, 0, _sampleBuffer, 0, _frameSize);

            // Shift remaining samples to the beginning
            var remainingSamples = _accumulationIndex - _frameSize;
            if (remainingSamples > 0)
            {
                Array.Copy(_accumulationBuffer, _frameSize, _accumulationBuffer, 0, remainingSamples);
            }
            _accumulationIndex = remainingSamples;

            try
            {
                // Encode the frame
                var encodedBytes = _encoder.Encode(_sampleBuffer, _frameSize, _opusBuffer, _opusBuffer.Length);

                if (encodedBytes > 0)
                {
                    // Create properly sized output buffer
                    var outputBuffer = new byte[encodedBytes];
                    Array.Copy(_opusBuffer, 0, outputBuffer, 0, encodedBytes);

                    // Raise event with encoded data
                    OnAudioEncoded?.Invoke(outputBuffer);
                    _frameCounter++;

                    if (_isVerboseLogging && _frameCounter % 50 == 0)
                    {
                        Debug.Log($"[OpusAudioEncoder] Encoded frame {_frameCounter}: {encodedBytes} bytes");
                    }
                }
                else if (_isVerboseLogging)
                {
                    Debug.LogWarning($"[OpusAudioEncoder] Encode returned 0 bytes!");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[OpusAudioEncoder] Encoding error: {e.Message}\n{e.StackTrace}");
            }
        }

        private void FlushRemainingAudio()
        {
            if (_accumulationIndex < _frameSize)
            {
                // Pad with silence
                Array.Clear(_accumulationBuffer, _accumulationIndex, _frameSize - _accumulationIndex);
                _accumulationIndex = _frameSize;
            }

            EncodeFrame();
        }

        public void Dispose()
        {
            if (_isDisposed) return;

            StopRecording();
            _encoder?.Dispose();
            _encoder = null;
            _isDisposed = true;

            if (_isVerboseLogging)
                Debug.Log("[OpusAudioEncoder] Disposed");
        }
    }
}
#else
namespace Tsc.AIBridge.Audio.Codecs
{
    /// <summary>
    /// Placeholder when OpusSharp is not available
    /// </summary>
    public class OpusAudioEncoder : System.IDisposable
    {
        public event System.Action<byte[]> OnAudioEncoded;
        public bool IsRecording => false;
        public int FrameSizeMs => 20;

        public OpusAudioEncoder(int sampleRate = 16000, int channels = 1, int bitrate = 24000, bool isVerboseLogging = false)
        {
            UnityEngine.Debug.LogError("[OpusAudioEncoder] OpusSharp NOT available! Audio encoding is disabled!");
            UnityEngine.Debug.LogError("[OpusAudioEncoder] To fix: Add 'OPUSSHARP_AVAILABLE' to Player Settings > Scripting Define Symbols");
            UnityEngine.Debug.LogError("[OpusAudioEncoder] Or make sure OpusSharp package is properly installed");
        }

        public void StartRecording()
        {
            UnityEngine.Debug.LogError("[OpusAudioEncoder] StartRecording called but OpusSharp not available!");
        }

        public void StopRecording() { }
        public void ProcessAudioData(float[] samples) { }
        public void Dispose() { }
    }
}
#endif