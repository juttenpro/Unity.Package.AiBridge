namespace Tsc.AIBridge.Audio.VAD
{
    /// <summary>
    /// Simple Voice Activity Detection processor
    /// Used for clean audio sources like NPC speech
    /// Core logic: Volume threshold + pause tolerance
    /// </summary>
    public class SimpleVADProcessor : VADProcessorBase
    {
        // Configuration (simplified)
        private readonly float _volumeThreshold;

        /// <summary>
        /// Create a new simple VAD processor
        /// </summary>
        /// <param name="name">Name for debugging (e.g. "NPC")</param>
        /// <param name="volumeThreshold">RMS volume threshold for speech detection</param>
        /// <param name="isVerboseLogging">Enable debug logging</param>
        public SimpleVADProcessor(string name, float volumeThreshold = 0.001f, bool isVerboseLogging = false)
            : base(name, isVerboseLogging)
        {
            _volumeThreshold = volumeThreshold;
        }

        /// <summary>
        /// Legacy constructor for backwards compatibility
        /// </summary>
        [System.Obsolete("Use simplified constructor with volumeThreshold parameter")]
        public SimpleVADProcessor(string name, float startThreshold, float stopThreshold, float smoothingFactor = 0.3f, float holdTime = 0.3f, bool isVerboseLogging = false)
            : base(name, isVerboseLogging)
        {
            // Use average of start/stop thresholds for simplified volume threshold
            _volumeThreshold = (startThreshold + stopThreshold) / 2f;
        }

        /// <summary>
        /// Process audio frame with simple volume + pause detection
        /// </summary>
        /// <param name="audioData">Audio samples</param>
        /// <param name="deltaTime">Time since last frame (for pause timing)</param>
        /// <returns>True if speech is detected</returns>
        public override bool ProcessAudioFrame(float[] audioData, float deltaTime = 0f)
        {
            if (audioData == null || audioData.Length == 0)
                return CurrentlySpeaking;

            // Simple volume measurement (RMS)
            var volume = CalculateRMS(audioData);

            // Get delta time (if not provided, estimate ~50Hz audio callbacks)
            if (deltaTime <= 0)
                deltaTime = 0.02f;

            // Use base class common logic
            var audioAboveThreshold = volume > _volumeThreshold;
            return ProcessSpeechDetection(audioAboveThreshold, deltaTime);
        }

        /// <summary>
        /// Get current silence duration (for debugging)
        /// </summary>
        public float GetSilenceDuration() => SilenceTimer;

        /// <summary>
        /// Current detection threshold
        /// </summary>
        public override float CurrentThreshold => _volumeThreshold;

    }
}