using System;
using UnityEngine;
using Tsc.AIBridge.Audio.Processing;

namespace Tsc.AIBridge.Handlers
{
    /// <summary>
    /// Handles binary audio messages received from the WebSocket.
    /// Responsible for detecting OGG streams and processing audio data.
    /// Extracted from StreamingApiClient for better separation of concerns.
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
        
        /// <summary>
        /// Event fired when an OGG stream is detected
        /// </summary>
        public event Action<int> OnOggStreamDetected;
        
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
            
            // CRITICAL: Block audio from interrupted RequestId
            // But allow audio from new RequestId (follow-up response)
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
                    if (_enableVerboseLogging)
                        Debug.Log($"[{_personaName}] First OGG header - starting audio playback");
                    
                    // Auto-start audio stream on first OGG chunk
                    // ElevenLabs Opus is always 48kHz according to their documentation
                    _audioProcessor.StartAudioStream(isOpus: true, sampleRate: 48000);
                }
                else if (_enableVerboseLogging)
                {
                    // ElevenLabs may send multiple OGG containers per sentence for streaming efficiency
                    // This is normal and doesn't indicate new sentences
                    Debug.Log($"[{_personaName}] Additional OGG header #{_receivedStreamCount} - part of ongoing stream");
                }
                
                OnOggStreamDetected?.Invoke(_receivedStreamCount);
            }
            
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
        /// Mark that an interruption has occurred in this session
        /// </summary>
        public void MarkInterruption()
        {
            _interruptionOccurredThisSession = true;
            _interruptedRequestId = _currentRequestId; // Remember which RequestId was interrupted
            Debug.Log($"[{_personaName}] Interruption marked for RequestId: {_interruptedRequestId} - blocking its audio");
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
            
            if (_enableVerboseLogging)
                Debug.Log($"[{_personaName}] Processing RequestId: {requestId}");
        }
        
        /// <summary>
        /// Get the current RequestId being processed
        /// </summary>
        public string CurrentRequestId => _currentRequestId;
    }
}