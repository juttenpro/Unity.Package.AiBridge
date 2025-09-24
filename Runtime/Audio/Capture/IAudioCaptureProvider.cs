using System;

namespace SimulationCrew.AIBridge.Audio.Capture
{
    /// <summary>
    /// Interface for audio capture providers.
    /// Allows different implementations (microphone, file playback, network stream, etc.)
    /// </summary>
    public interface IAudioCaptureProvider
    {
        /// <summary>
        /// Event fired when capture starts
        /// </summary>
        event Action OnCaptureStarted;

        /// <summary>
        /// Event fired when capture stops
        /// </summary>
        event Action OnCaptureStopped;

        /// <summary>
        /// Event fired when audio data is available
        /// </summary>
        event Action<float[]> OnAudioDataAvailable;

        /// <summary>
        /// Event fired when an error occurs
        /// </summary>
        event Action<string> OnError;

        /// <summary>
        /// Current capture state
        /// </summary>
        bool IsCapturing { get; }

        /// <summary>
        /// Sample rate of captured audio
        /// </summary>
        int SampleRate { get; }

        /// <summary>
        /// Number of channels (1 = mono, 2 = stereo)
        /// </summary>
        int Channels { get; }

        /// <summary>
        /// Current audio volume level (0-1)
        /// </summary>
        float CurrentVolume { get; }

        /// <summary>
        /// Start capturing audio
        /// </summary>
        void StartCapture();

        /// <summary>
        /// Stop capturing audio
        /// </summary>
        void StopCapture();

        /// <summary>
        /// Select audio input device
        /// </summary>
        /// <param name="deviceName">Name of the device</param>
        /// <returns>True if device was selected successfully</returns>
        bool SelectDevice(string deviceName);

        /// <summary>
        /// Get list of available audio devices
        /// </summary>
        string[] GetAvailableDevices();
    }
}