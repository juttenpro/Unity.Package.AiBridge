using System;

namespace Tsc.AIBridge.Core
{
    /// <summary>
    /// Parameters for establishing a conversation session with the AI backend
    /// </summary>
    [Serializable]
    public class ConnectionParameters
    {
        /// <summary>
        /// Unique session identifier
        /// </summary>
        public string SessionId { get; set; }

        /// <summary>
        /// TTS voice ID
        /// </summary>
        public string VoiceId { get; set; }

        /// <summary>
        /// TTS model (e.g., "eleven_turbo_v2_5")
        /// </summary>
        public string Model { get; set; }

        /// <summary>
        /// Language code (e.g., "nl-NL")
        /// </summary>
        public string Language { get; set; }

        /// <summary>
        /// STT provider (e.g., "google", "azure")
        /// </summary>
        public string SttProvider { get; set; }

        /// <summary>
        /// LLM provider (e.g., "openai", "vertexai", "azure-openai")
        /// </summary>
        public string LlmProvider { get; set; }

        /// <summary>
        /// LLM model (e.g., "gpt-4o-mini", "gemini-1.5-flash")
        /// </summary>
        public string LlmModel { get; set; }

        /// <summary>
        /// Temperature for LLM responses (0.0 - 1.0)
        /// </summary>
        public float Temperature { get; set; }

        /// <summary>
        /// Maximum tokens for LLM response
        /// </summary>
        public int MaxTokens { get; set; }

        /// <summary>
        /// TTS streaming mode (e.g., "batch", "sentence")
        /// </summary>
        public string TtsStreamingMode { get; set; }

        /// <summary>
        /// Audio format (e.g., "opus", "pcm")
        /// </summary>
        public string AudioFormat { get; set; }

        /// <summary>
        /// Audio sample rate in Hz
        /// </summary>
        public int SampleRate { get; set; }

        /// <summary>
        /// Audio bitrate in bps
        /// </summary>
        public int Bitrate { get; set; }

        /// <summary>
        /// Number of audio channels (1 = mono, 2 = stereo)
        /// </summary>
        public int ChannelCount { get; set; }

        /// <summary>
        /// Enable metrics tracking
        /// </summary>
        public bool EnableMetrics { get; set; }

        /// <summary>
        /// Create default connection parameters
        /// </summary>
        public static ConnectionParameters CreateDefault()
        {
            return new ConnectionParameters
            {
                SessionId = Guid.NewGuid().ToString(),
                VoiceId = "onyx",
                Model = "eleven_turbo_v2_5",
                Language = "nl-NL",
                SttProvider = "google",
                LlmProvider = "openai",
                LlmModel = "gpt-4o-mini",
                Temperature = 0.7f,
                MaxTokens = 500,
                TtsStreamingMode = "batch",
                AudioFormat = "opus",
                SampleRate = 48000,
                Bitrate = 64000,
                ChannelCount = 1,
                EnableMetrics = true
            };
        }
    }
}