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
        /// Unique request identifier (matches WebSocket requestId field)
        /// </summary>
        public string RequestId { get; set; }

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
        /// TTS provider (e.g., "elevenlabs", "voxtral", "cartesia")
        /// </summary>
        public string TtsProvider { get; set; } = "elevenlabs";

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
        /// ElevenLabs voice stability (0.0 to 1.0) - controls consistency
        /// </summary>
        public float VoiceStability { get; set; } = 0.5f;

        /// <summary>
        /// ElevenLabs voice similarity boost (0.0 to 1.0) - controls voice matching
        /// </summary>
        public float VoiceSimilarityBoost { get; set; } = 0.75f;

        /// <summary>
        /// ElevenLabs voice style exaggeration (0.0 to 1.0)
        /// </summary>
        public float VoiceStyle { get; set; } = 0.0f;

        /// <summary>
        /// ElevenLabs speaker boost - enhances clarity and presence
        /// </summary>
        public bool VoiceUseSpeakerBoost { get; set; } = true;

        /// <summary>
        /// ElevenLabs voice speed (0.7 to 1.2) - controls speech rate
        /// </summary>
        public float VoiceSpeed { get; set; } = 1.0f;
    }
}