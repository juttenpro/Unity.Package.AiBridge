using System;
using UnityEngine;
using Tsc.AIBridge.Audio.Processing;

namespace Tsc.AIBridge.Handlers
{
    /// <summary>
    /// Handles binary audio messages received from the WebSocket.
    /// Responsible for detecting OGG streams and processing audio data.
    /// Extracted from StreamingApiClient for better separation of concerns.
    ///
    /// QUEUE INTEGRATION:
    /// This handler supports integration with NpcAudioPlayer's queue system via events:
    /// - OnStreamingAudioStarting: Fired when first OGG header detected. Returns false to buffer.
    /// - OnStreamingAudioReleased: Fired when buffered audio starts playing.
    ///
    /// When buffering is requested (OnStreamingAudioStarting returns false):
    /// 1. Raw Opus chunks are stored in AudioStreamProcessor._queueBufferedOpusQueue
    /// 2. No decoding occurs - raw bytes are preserved for later
    /// 3. When ReleaseBufferedAudio() is called, chunks are decoded and played
    /// 4. ClearBufferedAudio() discards buffered chunks without playing
    ///
    /// This allows AI streaming audio to wait in queue behind scripted audio when needed.
    /// </summary>
    public class AudioMessageHandler
    {
        private readonly string _personaName;
        private readonly AudioStreamProcessor _audioProcessor;
        private readonly bool _enableVerboseLogging;
        
        private int _receivedStreamCount = 0;
        private bool _interruptionOccurredThisSession = false;
        private string _currentRequestId = null;
        private string _interruptedRequestId = null; // Track which RequestId was interrupted
        private bool _waitingForNewSession = false; // Block all audio until SessionStarted after interruption
        private bool _isBufferingForQueue = false; // True when waiting in queue, data is buffered
        
        /// <summary>
        /// Event fired when an OGG stream is detected
        /// </summary>
        public event Action<int> OnOggStreamDetected;

        /// <summary>
        /// Event fired when streaming audio is about to start (first OGG header detected).
        /// Listeners can return false to indicate the stream should be buffered instead of played immediately.
        /// If no listeners or all return true, playback starts immediately.
        /// </summary>
        public event Func<bool> OnStreamingAudioStarting;

        /// <summary>
        /// Event fired when buffered audio should start playing.
        /// This is called when the queue releases the streaming request.
        /// </summary>
        public event Action OnStreamingAudioReleased;
        
        public AudioMessageHandler(
            string personaName,
            AudioStreamProcessor audioProcessor,
            bool enableVerboseLogging = false)
        {
            _personaName = personaName ?? "Unknown";
            _audioProcessor = audioProcessor ?? throw new ArgumentNullException(nameof(audioProcessor));
            _enableVerboseLogging = enableVerboseLogging;
        }
        
        /// <summary>
        /// Process incoming binary audio data from WebSocket
        /// </summary>
        /// <param name="data">Raw audio data (OGG/Opus format)</param>
        public void ProcessBinaryMessage(byte[] data)
        {
            if (_audioProcessor == null)
            {
                Debug.LogWarning($"[{_personaName}] Ignoring binary - audio processor not initialized");
                return;
            }
            
            // CRITICAL: Block ALL audio after interruption until SessionStarted from new request
            // This prevents old audio chunks (still in transit) from playing after interruption
            if (_waitingForNewSession)
            {
                Debug.Log($"[{_personaName}] Blocking audio chunk - waiting for SessionStarted from new request (current: {_currentRequestId})");
                return;
            }

            // Legacy check: Block audio from interrupted RequestId
            // This shouldn't trigger anymore since we use _waitingForNewSession, but keep as safety net
            if (_interruptionOccurredThisSession &&
                !string.IsNullOrEmpty(_interruptedRequestId) &&
                _currentRequestId == _interruptedRequestId)
            {
                if (_enableVerboseLogging)
                    Debug.Log($"[{_personaName}] Ignoring binary from interrupted RequestId: {_interruptedRequestId}");
                return;
            }
            
            // Debug: Log first bytes to see what we're receiving
            if (_enableVerboseLogging && data != null && data.Length >= 4)
            {
                Debug.Log($"[{_personaName}] First 4 bytes: {data[0]:X2}-{data[1]:X2}-{data[2]:X2}-{data[3]:X2} (expecting OggS: 4F-67-67-53)");
            }
            
            // SIMPLICITY FIRST: First audio chunk IS the signal to start playback!
            // NOTE: OGG headers can appear multiple times per sentence due to ElevenLabs streaming optimization
            // We only care about the FIRST OGG header to start playback, not counting them
            if (IsOggHeader(data))
            {
                _receivedStreamCount++;

                if (_enableVerboseLogging)
                    Debug.Log($"[{_personaName}] Detected OGG header #{_receivedStreamCount}");

                // Only start NEW audio stream on FIRST OGG chunk
                if (_receivedStreamCount == 1)
                {
                    // Check if we should start immediately or buffer for queue
                    bool canStartImmediately = true;
                    if (OnStreamingAudioStarting != null)
                    {
                        // If any listener returns false, we should buffer
                        canStartImmediately = OnStreamingAudioStarting.Invoke();
                    }

                    if (canStartImmediately)
                    {
                        if (_enableVerboseLogging)
                            Debug.Log($"[{_personaName}] First OGG header - starting audio playback immediately");

                        // Auto-start audio stream on first OGG chunk
                        // ElevenLabs Opus is always 48kHz according to their documentation
                        _audioProcessor.StartAudioStream(isOpus: true, sampleRate: 48000);
                        _isBufferingForQueue = false;
                    }
                    else
                    {
                        if (_enableVerboseLogging)
                            Debug.Log($"[{_personaName}] First OGG header - buffering for queue (waiting for other audio to finish)");

                        // Start buffering mode - data will be queued instead of played
                        _isBufferingForQueue = true;
                        _audioProcessor.StartBufferingForQueue();
                    }
                }
                else if (_enableVerboseLogging)
                {
                    // ElevenLabs may send multiple OGG containers per sentence for streaming efficiency
                    // This is normal and doesn't indicate new sentences
                    Debug.Log($"[{_personaName}] Additional OGG header #{_receivedStreamCount} - part of ongoing stream");
                }

                OnOggStreamDetected?.Invoke(_receivedStreamCount);
            }

            // Queue data - AudioStreamProcessor handles whether to buffer or process immediately
            _audioProcessor.ProcessReceivedAudio(data);
        }
        
        /// <summary>
        /// Check if data starts with OGG header magic bytes
        /// </summary>
        private bool IsOggHeader(byte[] data)
        {
            return data != null && 
                   data.Length >= 4 && 
                   data[0] == 0x4F && // 'O'
                   data[1] == 0x67 && // 'g'
                   data[2] == 0x67 && // 'g'
                   data[3] == 0x53;   // 'S'
        }
        
        /// <summary>
        /// Release buffered audio and start playback.
        /// Called when the queue allows this streaming request to play.
        /// </summary>
        public void ReleaseBufferedAudio()
        {
            if (!_isBufferingForQueue)
            {
                if (_enableVerboseLogging)
                    Debug.Log($"[{_personaName}] ReleaseBufferedAudio called but not in buffering mode");
                return;
            }

            if (_enableVerboseLogging)
                Debug.Log($"[{_personaName}] Releasing buffered audio - starting playback");

            _isBufferingForQueue = false;

            // Tell AudioStreamProcessor to flush buffered data and start playback
            _audioProcessor.ReleaseBufferedAudio();

            OnStreamingAudioReleased?.Invoke();
        }

        /// <summary>
        /// Check if currently buffering audio for queue
        /// </summary>
        public bool IsBufferingForQueue => _isBufferingForQueue;

        /// <summary>
        /// Clear buffered audio without playing it.
        /// Called when Replace mode is used and buffered streaming audio should be discarded.
        /// </summary>
        public void ClearBufferedAudio()
        {
            if (!_isBufferingForQueue)
            {
                return;
            }

            if (_enableVerboseLogging)
                Debug.Log($"[{_personaName}] Clearing buffered audio (discarded due to Replace mode)");

            _isBufferingForQueue = false;
            _receivedStreamCount = 0;

            // Tell AudioStreamProcessor to discard buffered data
            _audioProcessor.ClearBufferedAudio();
        }

        /// <summary>
        /// Mark that an interruption has occurred in this session
        /// </summary>
        public void MarkInterruption()
        {
            _interruptionOccurredThisSession = true;
            _interruptedRequestId = _currentRequestId; // Remember which RequestId was interrupted
            _waitingForNewSession = true; // Block ALL audio until SessionStarted from new request
            Debug.Log($"[{_personaName}] Interruption marked for RequestId: {_interruptedRequestId} - blocking ALL audio until new SessionStarted");
        }
        
        /// <summary>
        /// Reset handler state for a new session
        /// </summary>
        public void Reset()
        {
            if (_enableVerboseLogging)
                Debug.Log($"[{_personaName}] AudioMessageHandler reset - Previous stream count: {_receivedStreamCount}");

            _receivedStreamCount = 0;

            // Reset interruption flag to allow new audio
            if (_interruptionOccurredThisSession)
            {
                Debug.Log($"[{_personaName}] Interruption flag cleared - ready for new audio");
                _interruptionOccurredThisSession = false;
                _interruptedRequestId = null;
            }

            // CRITICAL: Don't clear _waitingForNewSession here!
            // It will be cleared when SessionStarted arrives with new RequestId

            // CRITICAL FIX: Reset decoder to clear old audio buffers after interruption
            // This prevents corrupt audio state (old data mixing with new stream)
            // See: unity_session_2025-10-14_12-14-26.log line 535 (222,613 bytes from old stream)
            if (_audioProcessor != null)
            {
                _audioProcessor.ResetDecoder();
                if (_enableVerboseLogging)
                    Debug.Log($"[{_personaName}] Decoder reset - old audio buffers cleared");
            }
        }
        
        /// <summary>
        /// Get the number of OGG streams received
        /// </summary>
        public int ReceivedStreamCount => _receivedStreamCount;
        
        /// <summary>
        /// Check if interruption has occurred
        /// </summary>
        public bool InterruptionOccurred => _interruptionOccurredThisSession;
        
        /// <summary>
        /// Called when a new request is started with a RequestId
        /// Automatically resets state if RequestId changes
        /// </summary>
        public void OnNewRequest(string requestId)
        {
            // If RequestId changes, reset state automatically
            if (!string.IsNullOrEmpty(requestId) &&
                !string.IsNullOrEmpty(_currentRequestId) &&
                requestId != _currentRequestId)
            {
                if (_enableVerboseLogging)
                    Debug.Log($"[{_personaName}] RequestId changed from {_currentRequestId} to {requestId} - resetting audio state");

                Reset();
            }

            _currentRequestId = requestId;

            // CRITICAL: Clear _waitingForNewSession when SessionStarted arrives with new RequestId
            // This allows audio from the new request to flow through
            if (_waitingForNewSession)
            {
                _waitingForNewSession = false;
                Debug.Log($"[{_personaName}] SessionStarted received for new RequestId: {requestId} - audio unblocked");
            }

            if (_enableVerboseLogging)
                Debug.Log($"[{_personaName}] Processing RequestId: {requestId}");
        }
        
        /// <summary>
        /// Get the current RequestId being processed
        /// </summary>
        public string CurrentRequestId => _currentRequestId;
    }
}