using System;
using Tsc.AIBridge.Data;

namespace Tsc.AIBridge.Core
{
    /// <summary>
    /// Interface for tracking latency metrics in the streaming pipeline
    /// </summary>
    public interface ILatencyTracker
    {
        /// <summary>
        /// Event fired when latency statistics are updated
        /// </summary>
        event Action<LatencyStats> OnLatencyStatsUpdated;
        
        /// <summary>
        /// Marks the start of a recording session
        /// </summary>
        void MarkRecordingStart();
        
        /// <summary>
        /// Marks when the first audio chunk is sent
        /// </summary>
        void MarkFirstChunkSent();
        
        /// <summary>
        /// Marks when transcription is received
        /// </summary>
        void MarkTranscriptionReceived(string transcription);
        
        /// <summary>
        /// Marks when AI response is received
        /// </summary>
        void MarkAIResponseReceived(string response);
        
        /// <summary>
        /// Marks when audio streaming starts
        /// </summary>
        void MarkAudioStreamStart();
        
        /// <summary>
        /// Marks when playback starts
        /// </summary>
        void MarkPlaybackStart(float bufferDuration);
        
        /// <summary>
        /// Resets all latency measurements
        /// </summary>
        void Reset();
    }
}