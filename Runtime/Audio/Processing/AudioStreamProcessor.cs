using System;
using SimulationCrew.AIBridge.Audio.Capture;
using SimulationCrew.AIBridge.Audio.Codecs;
using SimulationCrew.AIBridge.Audio.Playback;
using SimulationCrew.AIBridge.Audio.Processing;
using UnityEngine;
using SimulationCrew.AIBridge.Core;

namespace SimulationCrew.AIBridge.Audio.Processing
{
    /// <summary>
    /// AUDIO STREAM PROCESSOR - Central audio pipeline for encoding and decoding.
    /// 
    /// PRIMARY RESPONSIBILITY:
    /// Manages all audio format conversions and codec operations.
    /// Bridge between Unity's AudioClip format and network-ready Opus format.
    /// 
    /// WHAT THIS CLASS DOES:
    /// - Encodes microphone PCM to Opus (64kbps) for upload
    /// - Wraps Opus in OGG container for STT compatibility
    /// - Decodes received Opus/OGG streams to PCM for playback
    /// - Manages audio buffering for smooth playback
    /// - Handles session-based audio state
    /// 
    /// WHAT THIS CLASS DOES NOT DO:
    /// - Microphone access (delegated to Unity's Microphone API)
    /// - Network transmission (handled by NetworkMessageController)
    /// - Playback to speakers (handled by StreamingAudioPlayer)
    /// - Interruption detection (handled by InterruptionController)
    /// 
    /// AUDIO FORMATS:
    /// - Input: 48kHz mono PCM from microphone
    /// - Upload: Opus 64kbps in OGG container
    /// - Download: Opus in OGG container from server
    /// - Output: 48kHz mono PCM to StreamingAudioPlayer
    /// 
    /// KEY COMPONENTS:
    /// - OpusAudioEncoder: PCM to Opus encoding
    /// - OggOpusEncoder: Wraps Opus in OGG container
    /// - OpusStreamDecoder: Queue-based Opus to PCM decoding
    /// - AudioRingBuffer: Circular buffer for audio chunks
    /// 
    /// DEPENDENCIES:
    /// - StreamingAudioPlayer: For audio playback
    /// - Concentus: Opus codec library
    /// - ConversationSession: Session tracking
    /// </summary>
    public class AudioStreamProcessor : IDisposable
    {
        public bool IsEncoding => _opusEncoder != null && _opusEncoder.IsRecording;
        public bool IsStreamingAudio => _isStreamingAudio;
        
        // Events
        public event Action<byte[]> OnOpusAudioEncoded;
        public event Action OnPlaybackStarted;
        public event Action OnPlaybackComplete;

        private OpusAudioEncoder _opusEncoder;
        private OggOpusEncoder _oggOpusEncoder; // For wrapping Opus in OGG container
        private OpusStreamDecoder _opusStreamDecoder; // Decoder - no stream boundaries!
        private StreamingAudioPlayer _audioPlayer;
        private AudioRingBuffer _audioBuffer;
                
        // State
        private bool _isStreamingAudio;
        private bool _isOpusStream;
        private readonly object _streamLock = new();
        
        // Current audio session
        private ConversationSession _currentSession;
        private readonly bool _isVerboseLogging;
        private bool _isDisposed;

        /// <summary>
        /// Initializes the audio processor with required components and settings.
        /// </summary>
        public AudioStreamProcessor(StreamingAudioPlayer audioPlayer, int opusBitrate = 24000, float bufferDuration = 0.5f, bool isVerboseLogging = false)
        {
            _isVerboseLogging = isVerboseLogging;
            _audioPlayer = audioPlayer; // Can be null for encoding-only mode (e.g., SpeechInputHandler)
            
            if(_isVerboseLogging)
                Debug.Log($"[AudioStreamProcessor] Audio mode: Opus, Playback: {(audioPlayer != null ? "Enabled" : "Disabled (encoding-only)")}");
            
            try
            {
                // Initialize Opus encoder (normal class now)
                _opusEncoder = new OpusAudioEncoder(MicrophoneCapture.Frequency, 1, opusBitrate, isVerboseLogging);
                _opusEncoder.OnAudioEncoded += HandleEncodedAudio;
                
                if(_isVerboseLogging)
                    Debug.Log("[AudioStreamProcessor] OpusAudioEncoder initialized successfully");
                
                // Trigger initial event
                //OnOpusAudioEncoded?.Invoke(Array.Empty<byte>());
                
                // Initialize OGG/Opus wrapper (already a regular class)
                _oggOpusEncoder = new OggOpusEncoder();
                _oggOpusEncoder.Initialize(MicrophoneCapture.Frequency, 1, isVerboseLogging);
                _oggOpusEncoder.OnOggPacketReady += HandleOggPacketReady;
                
                if(_isVerboseLogging)
                    Debug.Log("[AudioStreamProcessor] OggOpusEncoder initialized successfully");
                
                // Only initialize decoder and playback events if audioPlayer is provided
                if (_audioPlayer != null)
                {
                    // Initialize SIMPLIFIED OGG/Opus stream decoder (normal class now)
                    _opusStreamDecoder = new OpusStreamDecoder(isVerboseLogging);
                    _opusStreamDecoder.OnAudioDecoded += HandleDecodedAudio;

                    if(_isVerboseLogging)
                        Debug.Log($"[AudioStreamProcessor] OpusStreamDecoder initialized - using continuous stream mode");
                    
                    // Subscribe to playback events
                    StreamingAudioPlayer.OnPlaybackStartedStatic += HandlePlaybackStarted;
                    _audioPlayer.OnPlaybackComplete.AddListener(HandlePlaybackComplete);
                }
                else if(_isVerboseLogging)
                {
                    Debug.Log("[AudioStreamProcessor] Running in encoding-only mode (no decoder/playback)");
                }
                
                if(_isVerboseLogging)
                    Debug.Log("[AudioStreamProcessor] Initialization complete");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AudioStreamProcessor] Initialization failed: {ex.Message}\n{ex.StackTrace}");
                throw;
            }
        }
        
        /// <summary>
        /// Start encoding audio with a new session
        /// </summary>
        public ConversationSession StartEncoding()
        {
            if (_isVerboseLogging)
                Debug.Log("[AudioStreamProcessor] StartEncoding called");
            
            // NOTE: Session is now created externally by ConversationSessionManager
            // We should NOT create our own session here anymore
            // The session will be set via SetCurrentSession()
            
            // Start Opus encoder
            if (_opusEncoder != null)
            {
                _opusEncoder.StartRecording();
                if(_isVerboseLogging)
                    Debug.Log($"[AudioStreamProcessor] Opus encoder started - IsRecording: {_opusEncoder.IsRecording}");
            }
            else
            {
                Debug.LogError("[AudioStreamProcessor] OpusEncoder is null - cannot start recording!");
            }
            // DON'T start OGG wrapper - we send raw Opus frames to backend
            
            return _currentSession; // Return the externally set session
        }
        
        /// <summary>
        /// Set the current audio session for this processor
        /// Called by SimpleSessionManager when a new session is created
        /// </summary>
        public void SetCurrentSession(ConversationSession session)
        {
            if (_currentSession != null && _currentSession != session)
            {
                // Session replacement is normal when quickly pressing PTT - not a warning condition
                if (_isVerboseLogging)
                    Debug.Log($"[AudioStreamProcessor] Replacing session {_currentSession.SessionId} with {session?.SessionId}");
            }

            _currentSession = session;
            if (_isVerboseLogging && session != null)
                Debug.Log($"[AudioStreamProcessor] 📌 SESSION SET: {session.SessionId} for NPC: {session.NpcName}");
        }
        
        /// <summary>
        /// Stop encoding audio
        /// </summary>
        public void StopEncoding()
        {
            _opusEncoder?.StopRecording();
            // DON'T end OGG stream - we're not using it
        }
        
        /// <summary>
        /// Process audio data from recorder
        /// </summary>
        public void ProcessRecordingData(float[] samples)
        {
            // Debug: Check encoding state
            if (_opusEncoder == null)
            {
                Debug.LogWarning("[AudioStreamProcessor] OpusEncoder is null - audio will be ignored");
                return;
            }
            
            if (!_opusEncoder.IsRecording)
            {
                // This is normal after interruption - don't spam logs
                // Samples keep coming in briefly after encoder stops
                return;
            }
            
            // Always use Opus encoding
            if (_opusEncoder != null && _opusEncoder.IsRecording)
            {
                // Process audio through Opus encoder
                _opusEncoder.ProcessAudioData(samples);
            }
        }
        
        // REMOVED: TryGetEncodedAudio - no longer needed after removing queue-based architecture
        // Audio is now sent directly via OnOpusAudioEncoded event
        
        /// <summary>
        /// Handle encoded audio from Opus encoder
        /// </summary>
        private void HandleEncodedAudio(byte[] encodedData)
        {
            if (encodedData == null || encodedData.Length == 0)
                return;
                
            // REMOVED: Queue to session - this was causing double buffering
            // The SendLoopController is not used in the refactored architecture
            // Audio is sent directly via the OnOpusAudioEncoded event
            
            // Raise event for consumers (SpeechInputHandler -> RequestOrchestrator)
            if (OnOpusAudioEncoded != null)
            {
                // Removed verbose logging - was causing log spam
                // Debug.Log($"[AudioStreamProcessor] 🔊 OPUS ENCODED: {encodedData.Length} bytes -> Event fired to {OnOpusAudioEncoded.GetInvocationList().Length} listeners");
                OnOpusAudioEncoded.Invoke(encodedData);
            }
            else
            {
                Debug.LogError($"[AudioStreamProcessor] ❌ NO LISTENERS for OnOpusAudioEncoded - {encodedData.Length} bytes LOST!");
            }
        }
        
        /// <summary>
        /// Handle OGG packet from wrapper (NOT USED - we send raw Opus)
        /// </summary>
        private void HandleOggPacketReady(byte[] oggPacket)
        {
            // NOT USED - we send raw Opus frames, not OGG-wrapped
            if (_isVerboseLogging)
                Debug.LogWarning("[AudioStreamProcessor] OGG packet received but not used - we send raw Opus");
        }
        
        /// <summary>
        /// Start streaming received audio
        /// </summary>
        public void StartAudioStream(bool isOpus, int sampleRate = 48000)
        {
            lock (_streamLock)
            {
                if (_isStreamingAudio)
                {
                    if (_isVerboseLogging)
                        Debug.Log("[AudioStreamProcessor] Audio stream already active");
                    return;
                }
                
                _isStreamingAudio = true;
                _isOpusStream = isOpus;
                
                if (_audioPlayer != null)
                {
                    // Start streaming with appropriate sample rate
                    _audioPlayer.StartStream(sampleRate);
                    
                    if (_isVerboseLogging)
                        Debug.Log($"[AudioStreamProcessor] Started audio stream - Opus: {isOpus}, Sample rate: {sampleRate}Hz");
                }
            }
        }
        
        /// <summary>
        /// Process received audio data
        /// </summary>
        public void ProcessReceivedAudio(byte[] audioData)
        {
            if (audioData == null || audioData.Length == 0)
            {
                Debug.LogWarning("[AudioStreamProcessor] ProcessReceivedAudio called with null/empty data");
                return;
            }
            
            // Log first 4 bytes for OGG header detection
            //if (audioData.Length >= 4 && audioData[0] == 0x4F && audioData[1] == 0x67 && audioData[2] == 0x67 && audioData[3] == 0x53)
            //{
            //    Debug.Log($"[AudioStreamProcessor] Received OGG header chunk: {audioData.Length} bytes");
            //}
            //else if (_isVerboseLogging)
            //{
            //    Debug.Log($"[AudioStreamProcessor] Received audio chunk: {audioData.Length} bytes (not OGG header)");
            //}
                
            lock (_streamLock)
            {
                if (!_isStreamingAudio)
                {
                    if (_isVerboseLogging)
                        Debug.LogWarning($"[AudioStreamProcessor] Received audio but stream not started - ignoring data");
                    return;
                }
                
                if (_isOpusStream)
                {
                    if (_opusStreamDecoder == null)
                    {
                        Debug.LogError("[AudioStreamProcessor] OpusStreamDecoder is null!");
                        return;
                    }
                    
                    // Process through Opus decoder
                    _opusStreamDecoder.ProcessData(audioData);
                }
                else
                {
                    Debug.LogError("[AudioStreamProcessor] Non-Opus audio not supported");
                }
            }
        }
        
        /// <summary>
        /// Handle decoded audio from Opus decoder
        /// </summary>
        private void HandleDecodedAudio(float[] decodedSamples)
        {
            if (decodedSamples != null && decodedSamples.Length > 0)
            {
                if (_audioPlayer != null)
                {
                    _audioPlayer.AddAudioData(decodedSamples);
                    
                }
                else
                {
                    Debug.LogWarning($"[AudioStreamProcessor] No player available - dropped {decodedSamples.Length} samples");
                }
            }
        }
        
        /// <summary>
        /// End the audio stream
        /// </summary>
        public void EndAudioStream()
        {
            lock (_streamLock)
            {
                if (!_isStreamingAudio)
                {
                    Debug.LogWarning("[AudioStreamProcessor] EndAudioStream called without active stream");
                    return;
                }
                
                if (_isVerboseLogging)
                    Debug.Log("[AudioStreamProcessor] Ending audio stream - flushing decoder buffers");
                
                // CRITICAL: Flush the Opus decoder to ensure last audio is processed
                if (_isOpusStream && _opusStreamDecoder != null)
                {
                    var remainingAudio = _opusStreamDecoder.FlushRemainingAudio();
                    if (remainingAudio != null && remainingAudio.Length > 0)
                    {
                        _audioPlayer?.AddAudioData(remainingAudio);
                        
                        if (_isVerboseLogging)
                            Debug.Log($"[AudioStreamProcessor] Flushed {remainingAudio.Length} remaining samples to player");
                    }
                    else if (_isVerboseLogging)
                    {
                        Debug.Log("[AudioStreamProcessor] No remaining audio to flush");
                    }
                }
                
                _isStreamingAudio = false;
                
                // Signal the real-time player that the stream is complete  
                if (_audioPlayer != null)
                {
                    _audioPlayer.EndStream();
                    
                    if (_isVerboseLogging)
                        Debug.Log("[AudioStreamProcessor] Audio stream ended - decoder processing complete");
                }
            }
        }
        
        /// <summary>
        /// Stop all audio processing and playback immediately without flushing
        /// Used for interruptions where we want to stop audio INSTANTLY
        /// </summary>
        public void StopAllAudio()
        {
            if (_isVerboseLogging)
                Debug.Log($"[AudioStreamProcessor] StopAllAudio called - Session: {_currentSession}, Streaming: {_isStreamingAudio}");
            
            StopEncoding();
            
            // CRITICAL: Don't call EndAudioStream() as it flushes remaining audio
            // Instead, directly stop the stream without flushing
            lock (_streamLock)
            {
                if (_isStreamingAudio)
                {
                    _isStreamingAudio = false;
                    
                    // Clear decoder buffers WITHOUT flushing to player
                    if(_isVerboseLogging)
                        Debug.Log("[AudioStreamProcessor] Calling Reset() on decoder from StopAllAudio");

                    _opusStreamDecoder?.Reset();
                    
                    if (_isVerboseLogging)
                        Debug.Log("[AudioStreamProcessor] Forcefully stopped audio stream without flushing");
                }
            }
            
            // Stop playback immediately
            if (_audioPlayer != null)
            {
                _audioPlayer.StopPlayback();
            }
            
            _currentSession = null;
            
            if (_isVerboseLogging)
                Debug.Log("[AudioStreamProcessor] Stopped all audio processing immediately");
        }
        
        
        /// <summary>
        /// Reset only the decoder without stopping audio playback
        /// Used after interruption to ensure clean state for next audio stream
        /// </summary>
        public void ResetDecoder()
        {
            lock (_streamLock)
            {
                _opusStreamDecoder?.Reset();
                if (_isVerboseLogging)
                    Debug.Log("[AudioStreamProcessor] Decoder reset - ready for new audio stream");
            }
        }
        
        /// <summary>
        /// Get decoder packet count for verification
        /// </summary>
        public int GetDecoderPacketCount()
        {
            return _opusStreamDecoder?.GetPacketCount() ?? -1;
        }
        
        private void HandlePlaybackStarted()
        {
            OnPlaybackStarted?.Invoke();
        }
        
        private void HandlePlaybackComplete()
        {
            OnPlaybackComplete?.Invoke();
        }
        
        public void Dispose()
        {
            if (_isDisposed) return;
            
            // Unsubscribe from events
            if (_opusEncoder != null)
            {
                _opusEncoder.OnAudioEncoded -= HandleEncodedAudio;
                _opusEncoder.Dispose();
            }
            
            if (_oggOpusEncoder != null)
            {
                _oggOpusEncoder.OnOggPacketReady -= HandleOggPacketReady;
            }
            
            if (_opusStreamDecoder != null)
            {
                _opusStreamDecoder.OnAudioDecoded -= HandleDecodedAudio;
                _opusStreamDecoder.Dispose();
            }
            
            if (_audioPlayer != null)
            {
                StreamingAudioPlayer.OnPlaybackStartedStatic -= HandlePlaybackStarted;
                _audioPlayer.OnPlaybackComplete.RemoveListener(HandlePlaybackComplete);
            }
            
            _isDisposed = true;
            
            if (_isVerboseLogging)
                Debug.Log("[AudioStreamProcessor] Disposed");
        }
    }
}