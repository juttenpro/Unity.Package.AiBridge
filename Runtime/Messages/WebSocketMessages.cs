using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Tsc.AIBridge.Messages
{

    /// <summary>
    /// Simplified conversation parameters for internal use
    /// </summary>
    [Serializable]
    public class ConversationParameters
    {
        public string language;
        public string llmProvider;  // Added: vertexai, openai, azure-openai
        public string llmModel;
        public string ttsModel;
        public string sttProvider;
        public string voiceId;
        public int maxTokens;
        public float temperature;

        // PersonaSO settings that affect backend behavior
        public string ttsStreamingMode;  // "batch" or "sentence" - from PersonaSO.ttsStreamingMode
    }

    /// <summary>
    /// Types of animation markers that can be embedded in sentences
    /// </summary>
    public enum AnimationMarkerType
    {
        Emotion,           // Facial expressions (happy, sad, angry, etc.)
        Gesture,           // Physical animations (wave, nod, shrug, etc.)
        SpeechModulation   // Voice characteristics (soft, loud, fast, etc.)
    }
    /// <summary>
    /// Base class for all WebSocket messages exchanged between Unity client and backend API.
    /// Provides common fields for message type identification, request tracking, and timestamping.
    /// Using PascalCase for property names with JsonProperty attributes for camelCase serialization.
    /// </summary>
    [Serializable]
    public abstract class WebSocketMessageBase
    {
        [JsonProperty("type")]
        public string Type;

        [JsonProperty("requestId")]
        public string RequestId;

        [JsonProperty("timestamp")]
        public long? Timestamp;
    }

    /// <summary>
    /// Client -> Server Messages
    /// </summary>

    /// <summary>
    /// Message sent from client to server to initiate a new conversation session.
    /// Contains configuration for STT, TTS, and the complete chat history.
    /// </summary>
    [Serializable]
    public class SessionStartMessage : WebSocketMessageBase
    {
        /// <summary>
        /// Language code for STT and TTS (e.g., "nl-NL", "en-US")
        /// </summary>
        [JsonProperty("languageCode")]
        public string LanguageCode;

        /// <summary>
        /// ElevenLabs voice ID for TTS generation
        /// </summary>
        [JsonProperty("voiceId")]
        public string VoiceId;

        /// <summary>
        /// Complete chat history including system prompt and previous turns
        /// </summary>
        [JsonProperty("messages")]
        public List<ChatMessage> Messages;

        /// <summary>
        /// Speech-to-text provider ("google" or "azure")
        /// </summary>
        [JsonProperty("sttProvider")]
        public string SttProvider { get; set; }

        /// <summary>
        /// TTS provider ("elevenlabs", "voxtral", "cartesia")
        /// </summary>
        [JsonProperty("ttsProvider")]
        public string TtsProvider { get; set; } = "elevenlabs";

        // REMOVED: Interruptable field - managed locally via PersonaSO

        /// <summary>
        /// Audio format for input audio ("opus" or "pcm")
        /// </summary>
        [JsonProperty("audioFormat")]
        public string AudioFormat { get; set; } = "opus";

        /// <summary>
        /// Sample rate for audio in Hz - UPSTREAM (microphone capture for STT)
        /// </summary>
        [JsonProperty("sampleRate")]
        public int SampleRate { get; set; } = 16000;

        /// <summary>
        /// Opus bitrate in bits per second - UPSTREAM (microphone → backend)
        /// 64kbps provides good quality for voice recognition
        /// </summary>
        [JsonProperty("opusBitrate")]
        public int OpusBitrate { get; set; } = 64000;

        /// <summary>
        /// TTS streaming mode ("batch" or "sentence")
        /// </summary>
        [JsonProperty("ttsStreamingMode")]
        public string TtsStreamingMode { get; set; } = "batch";

        /// <summary>
        /// LLM provider to use ("vertexai", "openai", "azure-openai")
        /// </summary>
        [JsonProperty("llmProvider")]
        public string LlmProvider { get; set; } = "vertexai";

        /// <summary>
        /// LLM model to use (e.g., "gemini-2.5-flash", "gpt-4o-mini")
        /// </summary>
        [JsonProperty("llmModel")]
        public string LlmModel { get; set; } = "gemini-2.5-flash";

        /// <summary>
        /// TTS model to use (e.g., "eleven_flash_v2_5", "eleven_turbo_v2_5")
        /// </summary>
        [JsonProperty("ttsModel")]
        public string TtsModel { get; set; } = "eleven_flash_v2_5";

        /// <summary>
        /// Maximum tokens for LLM response
        /// </summary>
        [JsonProperty("maxTokens")]
        public int MaxTokens { get; set; } = 500;

        /// <summary>
        /// Temperature for LLM response generation (0.0 - 1.0)
        /// </summary>
        [JsonProperty("temperature")]
        public float Temperature { get; set; } = 0.7f;

        /// <summary>
        /// TTS output format ("opus" or "pcm")
        /// </summary>
        [JsonProperty("ttsOutputFormat")]
        public string TtsOutputFormat { get; set; } = "opus";

        /// <summary>
        /// Enable performance metrics tracking
        /// </summary>
        [JsonProperty("enableMetrics")]
        public bool EnableMetrics { get; set; }

        /// <summary>
        /// Custom vocabulary for STT (medical terms, uncommon words)
        /// </summary>
        [JsonProperty("customVocabulary")]
        public List<string> CustomVocabulary { get; set; }

        /// <summary>
        /// Boost value for custom vocabulary (0-20, higher = more likely to be recognized)
        /// </summary>
        [JsonProperty("customVocabularyBoost")]
        public float CustomVocabularyBoost { get; set; } = 10.0f;

        /// <summary>
        /// ElevenLabs voice stability (0.0 to 1.0) - controls consistency
        /// Lower values allow more expressive/variable speech
        /// </summary>
        [JsonProperty("voiceStability")]
        public float VoiceStability { get; set; } = 0.5f;

        /// <summary>
        /// ElevenLabs voice similarity boost (0.0 to 1.0) - controls voice matching
        /// Higher values make the voice more similar to the original
        /// </summary>
        [JsonProperty("voiceSimilarityBoost")]
        public float VoiceSimilarityBoost { get; set; } = 0.75f;

        /// <summary>
        /// ElevenLabs voice style exaggeration (0.0 to 1.0) - for newer models
        /// Controls the expressiveness and style of the speech
        /// </summary>
        [JsonProperty("voiceStyle")]
        public float VoiceStyle { get; set; } = 0.0f;

        /// <summary>
        /// ElevenLabs speaker boost - enhances clarity and presence
        /// </summary>
        [JsonProperty("voiceUseSpeakerBoost")]
        public bool VoiceUseSpeakerBoost { get; set; } = true;

        /// <summary>
        /// ElevenLabs voice speed (0.7 to 1.2) - controls speech rate
        /// Default 1.0 is normal speed, lower is slower, higher is faster
        /// </summary>
        [JsonProperty("voiceSpeed")]
        public float VoiceSpeed { get; set; } = 1.0f;

        /// <summary>
        /// Optional ISO 639-1 language code to force TTS pronunciation (e.g., "nl", "en", "de").
        /// When set, ElevenLabs uses this language instead of auto-detecting.
        /// Prevents accent drift (e.g., Flemish instead of Dutch).
        /// When null/empty, ElevenLabs auto-detects the language (default behavior).
        /// </summary>
        [JsonProperty("ttsLanguageCode")]
        public string TtsLanguageCode { get; set; }

        #region Context Caching (Gemini Cost Optimization)

        /// <summary>
        /// Full Gemini cached content resource name.
        /// Format: "projects/{project}/locations/{location}/cachedContents/{id}"
        /// Obtained from POST /api/cache/ensure endpoint via ContextCacheManager.
        /// When provided, VertexAIService uses this cache for 75% cost reduction on cached tokens.
        /// Note: When using cache, system prompt should NOT be included in Messages (it's in the cache).
        /// </summary>
        [JsonProperty("contextCacheName")]
        public string ContextCacheName { get; set; }

        #endregion

        public SessionStartMessage()
        {
            Type = WebSocketMessageTypes.SessionStart;
        }
    }

    /// <summary>
    /// Message sent to indicate the user has stopped speaking.
    /// Triggers the backend to finalize STT processing and begin LLM response generation.
    /// </summary>
    [Serializable]
    public class EndOfSpeechMessage : WebSocketMessageBase
    {
        public EndOfSpeechMessage()
        {
            Type = WebSocketMessageTypes.EndOfSpeech;
        }
    }

    /// <summary>
    /// Message sent to indicate all audio data has been transmitted.
    /// Marks the end of the audio stream for the current conversation turn.
    /// </summary>
    [Serializable]
    public class EndOfAudioMessage : WebSocketMessageBase
    {
        public EndOfAudioMessage()
        {
            Type = WebSocketMessageTypes.EndOfAudio;
        }
    }

    /// <summary>
    /// Keepalive message to maintain WebSocket connection.
    /// Prevents connection timeout during periods of inactivity.
    /// </summary>
    [Serializable]
    public class PingMessage : WebSocketMessageBase
    {
        public PingMessage()
        {
            Type = WebSocketMessageTypes.Ping;
        }
    }

    /// <summary>
    /// Message to notify the server of Unity's pause state.
    /// Used to suspend/resume audio processing when the application is paused.
    /// </summary>
    [Serializable]
    public class PauseStateMessage : WebSocketMessageBase
    {
        [JsonProperty("isPaused")]
        public bool IsPaused;

        public PauseStateMessage()
        {
            Type = WebSocketMessageTypes.PauseState;
        }
    }

    /// <summary>
    /// EMERGENCY cancel - stops ALL processing (STT, LLM, TTS).
    /// Use for app crash, scene switch, force quit.
    /// Server should clean up any partial processing, chat history updates, or metadata.
    /// </summary>
    [Serializable]
    public class SessionCancelMessage : WebSocketMessageBase
    {
        [JsonProperty("reason")]
        public string Reason;

        public SessionCancelMessage()
        {
            Type = WebSocketMessageTypes.SessionCancel;
        }
    }

    /// <summary>
    /// User interrupted NPC speech - stop TTS but let LLM finish for metadata/intent.
    /// NORMAL interruption during conversation.
    /// Backend stops TTS generation (save bandwidth) but keeps LLM running for complete response.
    /// Backend sends conversationComplete with wasInterrupted=true.
    /// Unity uses this to determine partial response for chat history.
    /// </summary>
    [Serializable]
    public class InterruptionOccurredMessage : WebSocketMessageBase
    {
        [JsonProperty("reason")]
        public string Reason;

        public InterruptionOccurredMessage()
        {
            Type = WebSocketMessageTypes.InterruptionOccurred;
        }
    }

    /// <summary>
    /// Message for text-only input without audio.
    /// Used for NPC-initiated conversations or testing without microphone.
    /// Skips STT pipeline and goes directly to LLM.
    /// </summary>
    [Serializable]
    public class TextInputMessage : WebSocketMessageBase
    {
        /// <summary>
        /// The text input to process (instead of audio)
        /// </summary>
        [JsonProperty("text")]
        public string Text;

        /// <summary>
        /// Whether this is an NPC-initiated conversation (NPC speaks first)
        /// </summary>
        [JsonProperty("isNpcInitiated")]
        public bool IsNpcInitiated;

        /// <summary>
        /// Unified conversation context containing all parameters, chat history, and settings
        /// </summary>
        [JsonProperty("context")]
        public ConversationContext Context;

        public TextInputMessage()
        {
            Type = "textinput";  // Lowercase per protocol
        }
    }

    /// <summary>
    /// Direct TTS request - sends text directly to TTS service without LLM processing.
    /// Useful for pre-scripted NPC dialogue, system messages, or when LLM is not needed.
    /// </summary>
    [Serializable]
    public class DirectTTSMessage : WebSocketMessageBase
    {
        /// <summary>
        /// The text to be converted to speech
        /// </summary>
        [JsonProperty("text")]
        public string Text;

        /// <summary>
        /// Optional voice override for this specific TTS request.
        /// If not specified, uses the voice from connection parameters.
        /// </summary>
        [JsonProperty("voice")]
        public string Voice;

        /// <summary>
        /// Optional TTS model override (e.g., "eleven_turbo_v2_5", "eleven_turbo_v2")
        /// If not specified, uses the model from connection parameters.
        /// </summary>
        [JsonProperty("model")]
        public string Model;

        public DirectTTSMessage()
        {
            Type = "directtts";  // Lowercase per protocol
        }
    }

    /// <summary>
    /// Message for conversation analysis.
    /// Uses existing WebSocket connection for simplicity.
    /// Server responds with analysis result.
    /// </summary>
    [Serializable]
    public class AnalysisRequestMessage : WebSocketMessageBase
    {
        /// <summary>
        /// All parameters in one context object
        /// </summary>
        [JsonProperty("context")]
        public ConversationContext Context;

        public AnalysisRequestMessage()
        {
            Type = "analysisrequest";  // Lowercase per protocol
        }
    }

    /// <summary>
    /// Message sent when user interrupts NPC speech.
    /// Contains the partial text that the user actually heard before interrupting.
    /// Used to update chat history with accurate representation of what was said.
    /// </summary>
    // REMOVED: InterruptionOccurredMessage - backend doesn't need interruption details
    // Use SessionCancelMessage instead for simplicity
    // Partial text tracking is now handled locally in Unity

    /// <summary>
    /// Response message for analysis request.
    /// Contains analysis result and timing metrics.
    /// </summary>
    [Serializable]
    public class AnalysisResponseMessage : WebSocketMessageBase
    {
        /// <summary>
        /// The analysis result text
        /// </summary>
        [JsonProperty("analysis")]
        public string Analysis;

        /// <summary>
        /// Complete LLM response object
        /// </summary>
        [JsonProperty("llmResponse")]
        public object LlmResponse;

        /// <summary>
        /// Metadata including token usage
        /// </summary>
        [JsonProperty("metadata")]
        public object Metadata;

        /// <summary>
        /// Timing information
        /// </summary>
        [JsonProperty("timing")]
        public object Timing;

        public AnalysisResponseMessage()
        {
            Type = "analysisresponse";  // Lowercase per protocol
        }
    }

    /// <summary>
    /// Server -> Client Messages
    /// </summary>

    /// <summary>
    /// Server confirmation that WebSocket connection is established.
    /// Contains connection ID and initial network quality assessment.
    /// </summary>
    [Serializable]
    public class ConnectionEstablishedMessage : WebSocketMessageBase
    {
        [JsonProperty("connectionId")]
        public string ConnectionId;

        [JsonProperty("networkQuality")]
        public string NetworkQuality;
    }

    /// <summary>
    /// Server confirmation that a conversation session has started.
    /// Indicates the backend is ready to receive audio data.
    /// </summary>
    [Serializable]
    public class SessionStartedMessage : WebSocketMessageBase
    {
    }

    /// <summary>
    /// Speech-to-text transcription result from the backend.
    /// Can be either interim (partial) or final transcription of user's speech.
    /// </summary>
    [Serializable]
    public class TranscriptionMessage : WebSocketMessageBase
    {
        [JsonProperty("text")]
        public string Text;

        [JsonProperty("isFinal")]
        public bool IsFinal;

        [JsonProperty("timing")]
        public SttTiming Timing;
    }

    /// <summary>
    /// Timing metrics for speech-to-text processing.
    /// Used for latency tracking and performance monitoring.
    /// </summary>
    [Serializable]
    public class SttTiming
    {
        [JsonProperty("serviceCreationMs")]
        public double ServiceCreationMs;

        [JsonProperty("configurationMs")]
        public double ConfigurationMs;

        [JsonProperty("firstResultMs")]
        public double FirstResultMs;

        [JsonProperty("totalProcessingMs")]
        public double TotalProcessingMs;

        [JsonProperty("pttToAudioMs")]
        public double PttToAudioMs; // Unity-side PTT timing

        [JsonProperty("provider")]
        public string Provider;

        [JsonProperty("audioChunks")]
        public int AudioChunks;

        [JsonProperty("audioBytes")]
        public long AudioBytes;
    }

    /// <summary>
    /// Timing metrics for Large Language Model processing.
    /// Tracks response generation latency and token throughput.
    /// </summary>
    [Serializable]
    public class LlmTiming
    {
        [JsonProperty("firstResponseMs")]
        public double FirstResponseMs;

        [JsonProperty("totalResponseMs")]
        public double TotalResponseMs;

        [JsonProperty("llmWaitMs")]
        public double? LlmWaitMs;  // Time from LLM start to first TTS request (user-perceived wait)

        [JsonProperty("model")]
        public string Model;

        [JsonProperty("chunkCount")]
        public int ChunkCount;

        [JsonProperty("totalTokens")]
        public int TotalTokens;
    }

    // TTS timing removed - not useful

    /// <summary>
    /// AI assistant's text response generated by the LLM.
    /// Contains the response text, complete LLM response object, intents array, and timing metrics.
    /// </summary>
    [Serializable]
    public class AiResponseMessage : WebSocketMessageBase
    {
        [JsonProperty("text")]
        public string Text;

        [JsonProperty("timing")]
        public LlmTiming Timing;

        [JsonProperty("llmResponse")]
        public Newtonsoft.Json.Linq.JObject LlmResponse;

        /// <summary>
        /// Intent classifications extracted from the LLM response.
        /// Response-level labels describing the purpose of the complete AI response.
        /// Examples: "goodbye", "ask_question", "express_feelings", "provide_information", "request_action", "acknowledge"
        /// </summary>
        [JsonProperty("intents")]
        public string[] Intents;

        /// <summary>
        /// Raw LLM response text BEFORE marker extraction.
        /// Contains all INTENT, GESTURE, EMOTION markers for LogViewer debugging.
        /// Used for debugging and logging the complete LLM output with all metadata markers intact.
        /// </summary>
        [JsonProperty("rawResponseText")]
        public string RawResponseText;
    }

    /// <summary>
    /// Typed data class for LLM response from backend.
    /// Replaces fragile JObject indexer access with strongly-typed properties.
    /// Shared between AIBridge and RuleSystem for consistent LLM response handling.
    /// </summary>
    [Serializable]
    public class LlmResponseData
    {
        /// <summary>
        /// Cleaned text without metadata markers (for display and TTS)
        /// </summary>
        public string Text { get; set; }

        /// <summary>
        /// Raw response text with all [INTENT:], [GESTURE:], [EMOTION:] markers intact.
        /// Used for LogViewer debugging to see complete LLM output.
        /// </summary>
        public string RawResponseText { get; set; }

        /// <summary>
        /// List of extracted intent values from response.
        /// Used by RuleSystem for workflow decisions (e.g., goodbye→close case).
        /// Empty array if no intents detected.
        /// </summary>
        public string[] Intents { get; set; } = Array.Empty<string>();
    }

    /// <summary>
    /// Indicates the start of TTS audio streaming from the backend.
    /// Contains metadata about the audio format and encoding.
    /// </summary>
    [Serializable]
    public class AudioStreamStartMessage : WebSocketMessageBase
    {
        [JsonProperty("format")]
        public string Format; // "opus" or "pcm"

        [JsonProperty("sampleRate")]
        public int SampleRate;

        [JsonProperty("bitrate")]
        public int? Bitrate; // For opus

        [JsonProperty("timing")]
        public TtsTiming Timing; // TTS timing info

        // REMOVED: Interruptable field - managed locally via PersonaSO

        /// <summary>
        /// Time from EndOfSpeech to first TTS request (ms)
        /// Helps identify latency bottlenecks
        /// </summary>
        [JsonProperty("firstTtsRequestMs")]
        public double? FirstTtsRequestMs;
    }

    [Serializable]
    public class TtsTiming
    {
        [JsonProperty("firstAudioMs")]
        public double? FirstAudioMs;

        [JsonProperty("totalProcessingMs")]
        public double? TotalProcessingMs;

        [JsonProperty("latencyMs")]
        public double? LatencyMs;

        [JsonProperty("latencyLevel")]
        public string LatencyLevel;
    }

    /// <summary>
    /// Indicates the end of TTS audio streaming.
    /// Marks completion of the audio response for the current turn.
    /// Includes verification data to ensure all audio was received.
    /// </summary>
    [Serializable]
    public class AudioStreamEndMessage : WebSocketMessageBase
    {
        /// <summary>
        /// Total number of audio chunks that were sent
        /// </summary>
        [JsonProperty("totalChunksSent")]
        public int TotalChunksSent;

        /// <summary>
        /// Total bytes of audio data sent
        /// </summary>
        [JsonProperty("totalBytesSent")]
        public long TotalBytesSent;

        /// <summary>
        /// Number of sentences that were processed
        /// </summary>
        [JsonProperty("sentenceCount")]
        public int SentenceCount;

        /// <summary>
        /// The last sentence text for verification (helps debugging)
        /// </summary>
        [JsonProperty("lastSentence")]
        public string LastSentence;

        /// <summary>
        /// Total number of OGG/Opus streams sent (for verification)
        /// Unity can compare this with received streams to detect loss
        /// </summary>
        [JsonProperty("totalStreamsSent")]
        public int TotalStreamsSent;
    }

    /// <summary>
    /// Error message from the backend indicating a processing failure.
    /// Contains error details and optional error code for debugging.
    /// </summary>
    [Serializable]
    public class ErrorMessage : WebSocketMessageBase
    {
        [JsonProperty("code")]
        public string Code;

        [JsonProperty("message")]
        public string Message;

        [JsonProperty("details")]
        public string Details;
    }

    [Serializable]
    public class SimpleNotificationMessage : WebSocketMessageBase
    {
        [JsonProperty("data")]
        public string Data;
    }

    /// <summary>
    /// Message sent when STT fails to produce a transcript
    /// Allows Unity to handle the situation gracefully (e.g., say "Pardon?")
    /// </summary>
    [Serializable]
    public class NoTranscriptMessage : WebSocketMessageBase
    {
        [JsonProperty("reason")]
        public string Reason = "No speech detected";

        [JsonProperty("audioDuration")]
        public int AudioDuration; // milliseconds

        [JsonProperty("sttProvider")]
        public string SttProvider = "";

        [JsonProperty("confidence")]
        public float? Confidence;
    }

    /// <summary>
    /// Performance metrics message sent by backend when EnableMetrics=true
    /// Contains actual wait times for STT, LLM, and TTS stages
    /// </summary>
    [Serializable]
    public class LatencyMetricsMessage : WebSocketMessageBase
    {
        [JsonProperty("sttWaitMs")]
        public double? SttWaitMs; // Time from PTT release to transcription complete

        [JsonProperty("llmWaitMs")]
        public double? LlmWaitMs; // Time from LLM start to first TTS request

        [JsonProperty("ttsWaitMs")]
        public double? TtsWaitMs; // Time from TTS request to first audio chunk

        [JsonProperty("endToEndMs")]
        public double? EndToEndMs; // Total session time

        [JsonProperty("details")]
        public object Details; // Optional additional information (can be object or string)
    }

    /// <summary>
    /// Metadata message sent before audio chunks to enable real-time character animations.
    /// Contains inline markers for emotions, gestures, and speech modulation with timing information.
    /// Enables Unity to pre-load animations before TTS audio arrives for synchronized playback.
    /// </summary>
    [Serializable]
    public class SentenceMetadataMessage : WebSocketMessageBase
    {
        /// <summary>
        /// Index of this sentence in the complete response (0-based)
        /// </summary>
        [JsonProperty("sentenceIndex")]
        public int SentenceIndex;

        /// <summary>
        /// The complete sentence text including metadata markers
        /// </summary>
        [JsonProperty("sentenceText")]
        public string SentenceText;

        /// <summary>
        /// Estimated duration for this sentence in milliseconds
        /// Used for relative timing calculations
        /// </summary>
        [JsonProperty("estimatedDurationMs")]
        public int EstimatedDurationMs;

        /// <summary>
        /// List of metadata markers found in this sentence
        /// </summary>
        [JsonProperty("markers")]
        public List<MetadataMarker> Markers = new List<MetadataMarker>();
    }

    /// <summary>
    /// Individual metadata marker for character animations.
    /// Supports three types: EMOTION (facial expressions), GESTURE (physical animations),
    /// and SPEECH (voice modulation for ElevenLabs v3).
    /// </summary>
    [Serializable]
    public class MetadataMarker
    {
        /// <summary>
        /// Type of metadata marker
        /// </summary>
        [JsonProperty("type")]
        [JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
        public AnimationMarkerType Type;

        /// <summary>
        /// Value of the marker (e.g., "happy", "point", "soft,warm")
        /// </summary>
        [JsonProperty("value")]
        public string Value;

        /// <summary>
        /// Character position in the sentence where marker appears
        /// </summary>
        [JsonProperty("position")]
        public int Position;

        /// <summary>
        /// Relative position in sentence (0.0 to 1.0)
        /// Used for language-agnostic timing calculation
        /// </summary>
        [JsonProperty("relativePosition")]
        public double RelativePosition;

        /// <summary>
        /// Estimated timing in milliseconds when this marker should trigger
        /// Calculated as: (relativePosition * estimatedDuration)
        /// </summary>
        [JsonProperty("estimatedTimingMs")]
        public int EstimatedTimingMs;
    }

    /// <summary>
    /// Message sent when a full conversation turn is complete.
    /// Used to signal end of processing and trigger cleanup if needed.
    /// </summary>
    [Serializable]
    public class ConversationCompleteMessage : WebSocketMessageBase
    {
        /// <summary>
        /// Optional performance metrics for this conversation turn
        /// </summary>
        [JsonProperty("metrics")]
        public object Metrics;

        public ConversationCompleteMessage()
        {
            Type = "conversationComplete";
        }
    }
}