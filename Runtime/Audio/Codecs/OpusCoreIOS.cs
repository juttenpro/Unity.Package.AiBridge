// iOS-specific OpusSharp.Core replacement using DllImport("__Internal")
//
// WHY: On iOS, native libraries (.a) are statically linked into the binary.
// IL2CPP's DllImport("opus") generates dlopen("opus") which fails on iOS
// because iOS doesn't support dynamic library loading.
// DllImport("__Internal") resolves symbols from the main binary where
// libopus.a is statically linked.
//
// HOW: OpusSharp.Core.dll is excluded from iOS builds (via .meta).
// This file provides the same types (same namespace, same class names)
// so existing code compiles without changes on iOS.
//
// This file ONLY compiles on iOS device builds — in the editor the DLL is used.

#if UNITY_IOS && !UNITY_EDITOR

using System;
using System.Runtime.InteropServices;

namespace OpusSharp.Core
{
    /// <summary>
    /// iOS replacement for OpusSharp predefined values.
    /// </summary>
    public static class OpusPredefinedValues
    {
        public const int OPUS_APPLICATION_VOIP = 2048;
        public const int OPUS_APPLICATION_AUDIO = 2049;
        public const int OPUS_APPLICATION_RESTRICTED_LOWDELAY = 2051;

        // Encoder CTL requests
        public const int OPUS_SET_BITRATE_REQUEST = 4002;
        public const int OPUS_SET_COMPLEXITY_REQUEST = 4010;
    }

    /// <summary>
    /// iOS Opus encoder using DllImport("__Internal") for statically linked libopus.a.
    /// Matches the OpusSharp.Core.OpusEncoder API surface used by OpusAudioEncoder.
    /// </summary>
    public class OpusEncoder : IDisposable
    {
        [DllImport("__Internal")]
        private static extern IntPtr opus_encoder_create(int Fs, int channels, int application, out int error);

        [DllImport("__Internal")]
        private static extern int opus_encode_float(IntPtr st, float[] pcm, int frame_size, byte[] data, int max_data_bytes);

        [DllImport("__Internal")]
        private static extern void opus_encoder_destroy(IntPtr st);

        private IntPtr _encoder;
        private bool _disposed;

        public OpusEncoder(int sampleRate, int channels, int application)
        {
            _encoder = opus_encoder_create(sampleRate, channels, application, out int error);
            if (error != 0 || _encoder == IntPtr.Zero)
                throw new Exception($"Failed to create Opus encoder: error code {error}");
        }

        public int Encode(float[] pcm, int frameSize, byte[] output, int maxOutputSize)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(OpusEncoder));

            int result = opus_encode_float(_encoder, pcm, frameSize, output, maxOutputSize);
            if (result < 0)
                throw new Exception($"Opus encode error: {result}");

            return result;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_encoder != IntPtr.Zero)
            {
                opus_encoder_destroy(_encoder);
                _encoder = IntPtr.Zero;
            }
        }
    }

    /// <summary>
    /// iOS Opus decoder using DllImport("__Internal") for statically linked libopus.a.
    /// Matches the OpusSharp.Core.OpusDecoder API surface used by OpusStreamDecoder.
    /// </summary>
    public class OpusDecoder : IDisposable
    {
        [DllImport("__Internal")]
        private static extern IntPtr opus_decoder_create(int Fs, int channels, out int error);

        [DllImport("__Internal")]
        private static extern int opus_decode_float(IntPtr st, byte[] data, int len, float[] pcm, int frame_size, int decode_fec);

        [DllImport("__Internal")]
        private static extern void opus_decoder_destroy(IntPtr st);

        private IntPtr _decoder;
        private bool _disposed;

        public OpusDecoder(int sampleRate, int channels)
        {
            _decoder = opus_decoder_create(sampleRate, channels, out int error);
            if (error != 0 || _decoder == IntPtr.Zero)
                throw new Exception($"Failed to create Opus decoder: error code {error}");
        }

        public int Decode(byte[] data, int dataLength, float[] pcm, int frameSize, bool fec)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(OpusDecoder));

            int result = opus_decode_float(_decoder, data, dataLength, pcm, frameSize, fec ? 1 : 0);
            if (result < 0)
                throw new Exception($"Opus decode error: {result}");

            return result;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_decoder != IntPtr.Zero)
            {
                opus_decoder_destroy(_decoder);
                _decoder = IntPtr.Zero;
            }
        }
    }
}

#endif
