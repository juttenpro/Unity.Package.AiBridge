using System;
using UnityEngine;
using Tsc.AIBridge.Core;
using Tsc.AIBridge.Audio.Processing;

namespace Tsc.AIBridge.Controllers
{
    /// <summary>
    /// Handles audio recording events and coordination.
    /// Extracted from StreamingApiClient to reduce complexity.
    /// Manages recording lifecycle and audio data processing.
    /// </summary>
    public class AudioRecordingController
    {
        private readonly string _personaName;
        private readonly AudioStreamProcessor _audioProcessor;
        private readonly LatencyTracker _latencyTracker;
        private readonly bool _enableVerboseLogging;
        
        /// <summary>
        /// Event fired when recording starts
        /// </summary>
        public event Action OnRecordingStarted;
        
        /// <summary>
        /// Event fired when recording stops
        /// </summary>
        public event Action OnRecordingStopped;
        
        /// <summary>
        /// Event fired when audio data is received
        /// </summary>
        public event Action<float[]> OnAudioDataReceived;
        
        public AudioRecordingController(
            string personaName,
            AudioStreamProcessor audioProcessor,
            LatencyTracker latencyTracker,
            bool enableVerboseLogging = false)
        {
            _personaName = personaName ?? "Unknown";
            _audioProcessor = audioProcessor ?? throw new ArgumentNullException(nameof(audioProcessor));
            _latencyTracker = latencyTracker ?? throw new ArgumentNullException(nameof(latencyTracker));
            _enableVerboseLogging = enableVerboseLogging;
        }
        
        /// <summary>
        /// Handle PTT button press
        /// </summary>
        public void HandlePttPressed()
        {
            _latencyTracker.MarkRecordingStart();
            
            if (_enableVerboseLogging)
                Debug.Log($"[{_personaName}] PTT button pressed - marking recording start");
        }
        
        /// <summary>
        /// Handle PTT button release
        /// </summary>
        public void HandlePttReleased()
        {
            _latencyTracker.StartMeasurement();
            
            if (_enableVerboseLogging)
                Debug.Log($"[{_personaName}] PTT button released - starting latency measurement");
        }
        
        /// <summary>
        /// Handle recording started event
        /// </summary>
        public void HandleRecordingStarted(ConversationSession session)
        {
            if (session == null)
            {
                Debug.LogError($"[{_personaName}] Recording started without session!");
                return;
            }
            
            // Set session on audio processor
            _audioProcessor.SetCurrentSession(session);
            
            // Start encoder if not already encoding
            if (!_audioProcessor.IsEncoding)
            {
                _audioProcessor.StartEncoding();

                if (_enableVerboseLogging)
                    Debug.Log($"[{_personaName}] Started audio encoder for session {session.SessionId}");
            }
            else if (_enableVerboseLogging)
            {
                Debug.Log($"[{_personaName}] Audio encoder already running for session {session.SessionId}");
            }
            
            OnRecordingStarted?.Invoke();
        }
        
        /// <summary>
        /// Handle incoming audio data
        /// </summary>
        public void HandleRecordingData(float[] samples)
        {
            if (samples == null || samples.Length == 0)
                return;
                
            // Process and buffer audio data
            _audioProcessor.ProcessRecordingData(samples);
            
            if (_enableVerboseLogging && samples.Length > 0)
            {
                Debug.Log($"[{_personaName}] Processing {samples.Length} audio samples");
            }
            
            OnAudioDataReceived?.Invoke(samples);
        }
        
        /// <summary>
        /// Handle recording stopped event
        /// </summary>
        public void HandleRecordingStopped(ConversationSession session)
        {
            // Stop the encoder
            _audioProcessor.StopEncoding();
            
            if (_enableVerboseLogging)
                Debug.Log($"[{_personaName}] Stopped audio encoder");
            
            if (session != null && session.IsRecording)
            {
                session.IsRecording = false;
                
                if (_enableVerboseLogging)
                {
                    Debug.Log($"[{_personaName}] Recording stopped for session {session.SessionId}");
                    Debug.Log($"[{_personaName}] Chunks sent: {session.ChunksSent}");
                }
            }
            
            OnRecordingStopped?.Invoke();
        }
        
        /// <summary>
        /// Check if currently encoding
        /// </summary>
        public bool IsEncoding => _audioProcessor?.IsEncoding ?? false;
    }
}