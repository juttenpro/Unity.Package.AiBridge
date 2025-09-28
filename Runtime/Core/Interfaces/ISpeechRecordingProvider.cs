using System;

namespace Tsc.AIBridge.Core
{
    /// <summary>
    /// Provides speech recording functionality without specific implementation dependencies.
    /// </summary>
    public interface ISpeechRecordingProvider
    {
        /// <summary>
        /// Whether recording is currently active.
        /// </summary>
        bool IsRecording { get; }

        /// <summary>
        /// Starts recording audio.
        /// </summary>
        void StartRecording();

        /// <summary>
        /// Stops recording audio.
        /// </summary>
        void StopRecording();

        /// <summary>
        /// Event fired when audio data is available.
        /// </summary>
        event Action<float[]> OnAudioData;

        /// <summary>
        /// Event fired when recording starts.
        /// </summary>
        event Action OnRecordingStarted;

        /// <summary>
        /// Event fired when recording stops.
        /// </summary>
        event Action OnRecordingStopped;
    }
}