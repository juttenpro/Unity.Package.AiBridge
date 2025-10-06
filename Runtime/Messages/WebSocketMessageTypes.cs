namespace Tsc.AIBridge.Messages
{
    /// <summary>
    /// Defines all WebSocket message types used in the conversation protocol
    /// These MUST match between client and server
    /// </summary>
    public static class WebSocketMessageTypes
    {
        // Client -> Server messages
        public const string SessionStart = "SessionStart";
        public const string EndOfSpeech = "EndOfSpeech";
        public const string EndOfAudio = "EndOfAudio"; // Sent after last audio chunk
        public const string StartSpeaking = "StartSpeaking";
        public const string StopSpeaking = "StopSpeaking";
        public const string UpdateParameters = "UpdateParameters";
        public const string Ping = "Ping";
        public const string PauseState = "PauseState";
        // DEPRECATED: InterruptionOccurred - use SessionCancel instead (backend doesn't need interruption details)
        public const string SessionCancel = "sessioncancel"; // Cancel a session by RequestId (simple, no domain knowledge) - lowercase per protocol

        // Server -> Client messages
        public const string ConnectionEstablished = "ConnectionEstablished";
        public const string SessionStarted = "SessionStarted";
        public const string ListeningStarted = "ListeningStarted";
        public const string ListeningStopped = "ListeningStopped";
        public const string SpeechEnded = "SpeechEnded";
        public const string ProcessingStart = "ProcessingStart";
        public const string ProcessingEnd = "ProcessingEnd";
        public const string TranscriptionInterim = "TranscriptionInterim";
        public const string Transcription = "Transcription";
        public const string AiResponse = "AiResponse";
        public const string SpeakingStart = "SpeakingStart";
        public const string SpeakingEnd = "SpeakingEnd";
        public const string AudioStreamStart = "AudioStreamStart";
        public const string AudioStreamEnd = "AudioStreamEnd";
        public const string QualityChanged = "QualityChanged";
        public const string ParametersUpdated = "ParametersUpdated";
        public const string Pong = "Pong";
        public const string Error = "Error";
        public const string NoTranscript = "NoTranscript";
        public const string BufferHint = "BufferHint";
        public const string LatencyMetrics = "LatencyMetrics";
        public const string SentenceMetadata = "SentenceMetadata"; // Real-time character animation metadata
        public const string ConversationComplete = "conversationComplete"; // Lowercase per protocol
    }

    /// <summary>
    /// Protocol constants for WebSocket communication
    /// </summary>
    public static class WebSocketProtocol
    {
        /// <summary>
        /// Magic byte to identify binary audio data vs JSON messages
        /// Audio data will start with this byte
        /// </summary>
        public const byte AudioDataMarker = 0xAD; // "Audio Data"

        /// <summary>
        /// Magic byte for JSON control messages sent as binary
        /// </summary>
        public const byte JsonMessageMarker = 0x7B; // '{' character

        /// <summary>
        /// Default sample rate for audio (UPSTREAM)
        /// </summary>
        public const int DefaultSampleRate = 16000;

        /// <summary>
        /// Default Opus bitrate for UPSTREAM (64kbps - good quality for voice recognition)
        /// </summary>
        public const int DefaultOpusBitrate = 64000;
    }
}