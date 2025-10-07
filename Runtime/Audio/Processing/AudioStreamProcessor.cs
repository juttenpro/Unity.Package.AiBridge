using System;
using Tsc.AIBridge.Audio.Capture;
using Tsc.AIBridge.Audio.Codecs;
using Tsc.AIBridge.Audio.Playback;
using UnityEngine;
using Tsc.AIBridge.Core;

namespace Tsc.AIBridge.Audio.Processing
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
    /// - Decodes received Opus/OGG streams to PCM for playback
    /// - Manages audio buffering for RuleSystem validation
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
    /// - Upload: Raw Opus frames (64kbps)
    /// - Download: Opus/OGG stream from server
    /// - Output: 48kHz mono PCM to StreamingAudioPlayer
    /// 
    /// KEY COMPONENTS:
    /// - OpusAudioEncoder: PCM to Opus encoding (for upload)
    /// - OpusStreamDecoder: Queue-based Opus to PCM decoding (for playback)
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

        // Audio buffering for RuleSystem validation flow
        private readonly System.Collections.Concurrent.ConcurrentQueue<byte[]> _audioQueue = new();
        private bool _isBuffering;
        private readonly object _bufferLock = new();

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
        /// Start encoding audio with a new session.
        /// ALWAYS starts in buffering mode to handle:
        /// - RuleSystem approval delay (frames)
        /// - WebSocket reconnect scenarios
        /// - Session startup confirmation wait
        /// - Interruption detection period
        /// - Network latency compensation
        /// </summary>
        public ConversationSession StartEncoding()
        {
            Debug.Log("[AudioStreamProcessor] StartEncoding called");

            // NOTE: Session is now created externally by ConversationSessionManager
            // We should NOT create our own session here anymore
            // The session will be set via SetCurrentSession()

            // ALWAYS start in buffering mode
            // This is the default behavior - audio is buffered until FlushBuffer() is called
            lock (_bufferLock)
            {
                _isBuffering = true;
                _audioQueue.Clear(); // Clear any leftover data from previous session
                if (_isVerboseLogging)
                    Debug.Log("[AudioStreamProcessor] Started in BUFFERING mode (default) - audio will be queued until FlushBuffer()");
            }

            // Start Opus encoder
            if (_opusEncoder != null)
            {
                Debug.Log($"[AudioStreamProcessor] Calling _opusEncoder.StartRecording()...");
                _opusEncoder.StartRecording();
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
                    Debug.Log($"[AudioStreamProcessor] Replacing session {_currentSession.RequestId} with {session?.RequestId}");
            }

            _currentSession = session;
            if (_isVerboseLogging && session != null)
                Debug.Log($"[AudioStreamProcessor] 📌 SESSION SET: {session.RequestId} for NPC: {session.NpcName}");
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
        /// Enable audio buffering mode (for RuleSystem validation flow).
        /// Audio will be queued instead of fired via OnOpusAudioEncoded event.
        /// NOTE: Since StartEncoding() now buffers by default, this method is idempotent
        /// and safe to call multiple times. It does NOT clear the queue.
        /// </summary>
        public void StartBuffering()
        {
            lock (_bufferLock)
            {
                if (!_isBuffering)
                {
                    _isBuffering = true;
                    if (_isVerboseLogging)
                        Debug.Log("[AudioStreamProcessor] Buffering mode enabled - audio will be queued");
                }
                else
                {
                    if (_isVerboseLogging)
                        Debug.Log($"[AudioStreamProcessor] Already buffering ({_audioQueue.Count} chunks) - ignoring duplicate call");
                }
            }
        }

        /// <summary>
        /// Approve buffered audio and flush to WebSocket.
        /// Disables buffering mode and fires OnOpusAudioEncoded for all queued chunks.
        /// </summary>
        public void FlushBuffer()
        {
            lock (_bufferLock)
            {
                if (!_isBuffering)
                {
                    Debug.LogWarning("[AudioStreamProcessor] FlushBuffer called but not in buffering mode!");
                    return;
                }

                var chunkCount = _audioQueue.Count;
                if (_isVerboseLogging)
                    Debug.Log($"[AudioStreamProcessor] Flushing {chunkCount} buffered audio chunks");

                // Fire event for all buffered chunks
                while (_audioQueue.TryDequeue(out var audioData))
                {
                    OnOpusAudioEncoded?.Invoke(audioData);
                }

                _isBuffering = false;

                if (_isVerboseLogging)
                    Debug.Log($"[AudioStreamProcessor] Buffer flushed - {chunkCount} chunks sent");
            }
        }

        /// <summary>
        /// Discard buffered audio without sending.
        /// Clears the queue but PRESERVES buffering mode (for retry/next attempt).
        /// </summary>
        public void DiscardBuffer()
        {
            lock (_bufferLock)
            {
                if (!_isBuffering)
                {
                    Debug.LogWarning("[AudioStreamProcessor] DiscardBuffer called but not in buffering mode!");
                    return;
                }

                var discardedCount = _audioQueue.Count;
                _audioQueue.Clear();
                // NOTE: Keep _isBuffering = true so system can buffer again
                // Use case: RuleSystem rejection, failed interruption - need to retry

                if (_isVerboseLogging)
                    Debug.Log($"[AudioStreamProcessor] Buffer discarded - {discardedCount} chunks dropped, buffering mode preserved");
            }
        }

        /// <summary>
        /// Check if currently in buffering mode
        /// </summary>
        public bool IsBuffering => _isBuffering;
        
        /// <summary>
        /// Process audio data from recorder
        /// HOT PATH: Called ~50x/second - NO Debug.Log allowed!
        /// </summary>
        public void ProcessRecordingData(float[] samples)
        {
            // Fast path: Check encoding state
            if (_opusEncoder == null)
            {
                if (_isVerboseLogging)
                    Debug.LogWarning("[AudioStreamProcessor] OpusEncoder is null - audio will be ignored");
                return;
            }

            if (!_opusEncoder.IsRecording)
            {
                // This is normal after interruption - don't spam logs
                // Samples keep coming in briefly after encoder stops
                return;
            }

            // Always use Opus encoding - redundant check removed for performance
            _opusEncoder.ProcessAudioData(samples);
        }
        
        // REMOVED: TryGetEncodedAudio - no longer needed after removing queue-based architecture
        // Audio is now sent directly via OnOpusAudioEncoded event
        
        /// <summary>
        /// Handle encoded audio from Opus encoder
        /// HOT PATH: Called ~50x/second - NO Debug.Log allowed!
        /// </summary>
        private void HandleEncodedAudio(byte[] encodedData)
        {
            if (encodedData == null || encodedData.Length == 0)
            {
                if (_isVerboseLogging)
                    Debug.LogWarning("[AudioStreamProcessor] HandleEncodedAudio received null or empty data!");
                return;
            }

            lock (_bufferLock)
            {
                if (_isBuffering)
                {
                    // Buffer mode: Queue audio for later validation
                    _audioQueue.Enqueue(encodedData);

                    // Periodic logging only (every 50 chunks = ~1 second)
                    if (_isVerboseLogging && _audioQueue.Count % 50 == 0)
                    {
                        Debug.Log($"[AudioStreamProcessor] Buffered {_audioQueue.Count} audio chunks");
                    }
                    return;
                }
            }

            // Direct mode: Fire event immediately (validation already approved or not needed)
            if (OnOpusAudioEncoded != null)
            {
                OnOpusAudioEncoded.Invoke(encodedData);
            }
            else if (_isVerboseLogging)
            {
                Debug.LogError($"[AudioStreamProcessor] ❌ NO LISTENERS for OnOpusAudioEncoded - {encodedData.Length} bytes LOST!");
            }
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
        /// HOT PATH: Called for every incoming audio chunk - NO Debug.Log allowed!
        /// </summary>
        public void ProcessReceivedAudio(byte[] audioData)
        {
            if (audioData == null || audioData.Length == 0)
            {
                if (_isVerboseLogging)
                    Debug.LogWarning("[AudioStreamProcessor] ProcessReceivedAudio called with null/empty data");
                return;
            }

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
        /// CRITICAL FIX: ALWAYS call AudioPlayer.EndStream() to reset playback state!
        /// This fixes turn 2+ metrics not being displayed due to _isPlaybackStarted staying true.
        /// </summary>
        public void EndAudioStream()
        {
            lock (_streamLock)
            {
                if (!_isStreamingAudio)
                {
                    // CRITICAL FIX: Still call EndStream() on AudioPlayer to reset playback state!
                    // Without this, _isPlaybackStarted stays true from previous turn, breaking turn 2+ metrics
                    if (_audioPlayer != null)
                    {
                        _audioPlayer.EndStream();
                        Debug.Log("[AudioStreamProcessor] EndAudioStream called without active stream - still calling EndStream() to reset playback state");
                    }
                    else
                    {
                        Debug.LogWarning("[AudioStreamProcessor] EndAudioStream called without active stream and no AudioPlayer");
                    }
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
        /// <returns>Number of packets decoded</returns>
        /// <exception cref="InvalidOperationException">Thrown when decoder is not initialized</exception>
        public int GetDecoderPacketCount()
        {
            if (_opusStreamDecoder == null)
                throw new InvalidOperationException("[AudioStreamProcessor] Cannot get packet count - decoder not initialized. Call StartAudioStream() first.");

            return _opusStreamDecoder.GetPacketCount();
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