using System;
using System.Text;

namespace Tsc.AIBridge.Utilities
{
    /// <summary>
    /// Utility class for unwrapping binary audio chunks that contain RequestId headers.
    /// This enables robust routing of parallel audio streams to the correct NPC in multi-NPC scenarios.
    ///
    /// Binary Frame Format:
    /// [Magic: 0xAD][RequestId Length: 1 byte][RequestId: N bytes][Audio Data: Rest]
    ///
    /// Matches backend implementation in ApiOrchestrator.Utilities.BinaryAudioWrapper
    /// </summary>
    public static class BinaryAudioWrapper
    {
        /// <summary>
        /// Magic marker byte to identify wrapped audio frames.
        /// 0xAD = "Audio Data"
        /// </summary>
        private const byte AUDIO_DATA_MARKER = 0xAD;

        /// <summary>
        /// Unwrap audio chunk to extract RequestId and audio data.
        /// Returns (requestId, audioData) tuple.
        /// STRICT MODE: Throws exception if data is not wrapped (no backward compatibility).
        /// </summary>
        /// <param name="data">Binary frame data from WebSocket</param>
        /// <returns>Tuple of (RequestId, AudioData)</returns>
        /// <exception cref="ArgumentException">When audio data is null, empty, or not wrapped with RequestId</exception>
        public static (string requestId, byte[] audioData) UnwrapAudioChunk(byte[] data)
        {
            if (data == null || data.Length == 0)
                throw new ArgumentException("Audio data cannot be null or empty", nameof(data));

            // Check for wrapped format
            if (data.Length > 2 && data[0] == AUDIO_DATA_MARKER)
            {
                int requestIdLength = data[1];

                // Validate length
                if (data.Length < 2 + requestIdLength)
                {
                    throw new InvalidOperationException($"Invalid wrapped audio format: data length {data.Length} < {2 + requestIdLength}");
                }

                string requestId = Encoding.UTF8.GetString(data, 2, requestIdLength);
                int audioOffset = 2 + requestIdLength;
                byte[] audioData = new byte[data.Length - audioOffset];
                Array.Copy(data, audioOffset, audioData, 0, audioData.Length);

                return (requestId, audioData);
            }

            // Unwrapped data - STRICT MODE: This is an error
            throw new InvalidOperationException("Audio data is not wrapped with RequestId. All audio must be wrapped.");
        }

        /// <summary>
        /// Check if binary data appears to be wrapped with RequestId header.
        /// </summary>
        /// <param name="data">Binary data to check</param>
        /// <returns>True if data has the magic marker indicating wrapped format</returns>
        public static bool IsWrapped(byte[] data)
        {
            return data != null && data.Length > 2 && data[0] == AUDIO_DATA_MARKER;
        }
    }
}
