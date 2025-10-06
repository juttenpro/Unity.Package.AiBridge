using System;
using UnityEngine;
using Newtonsoft.Json;
using Tsc.AIBridge.Core;
using Tsc.AIBridge.Messages;

namespace Tsc.AIBridge.WebSocket
{
    /// <summary>
    /// Processes and routes incoming WebSocket messages from the conversation server.
    /// Handles deserialization of different message types and raises appropriate events.
    /// Key responsibilities:
    /// - Parse and validate incoming JSON messages
    /// - Deserialize messages to strongly-typed objects
    /// - Route messages to appropriate event handlers
    /// - Track latency metrics for transcription and AI responses
    /// - Handle both Unity and backend message format variations
    /// </summary>
    public class ConversationMetadataHandler
    {
        private readonly string _personaName;
        private readonly LatencyTracker _latencyTracker;
        private readonly bool _enableVerboseLogging;

        // Events for different message types
        public event Action<string> OnTranscription;
        public event Action<string> OnAIResponse;
        public event Action<string> OnError;
        // AudioStreamStart removed - audio starts automatically on first chunk
        public event Action<AudioStreamEndMessage> OnAudioStreamEnd;  // Now passes complete message with verification data
        public event Action OnConnectionEstablished;
        public event Action OnSessionStarted;  // Added for session confirmation
        public event Action<ConversationTurnResponse> OnMetadataReceived;
        public event Action<NoTranscriptMessage> OnNoTranscript;
        public event Action<BufferHintMessage> OnBufferHint;
        public event Action<SentenceMetadataMessage> OnSentenceMetadata;
        public event Action<bool> OnConversationComplete;  // bool indicates if audio was received
        
        // Track last NPC response for interruption handling
        public string LastNpcResponse { get; private set; }
        
        // Interruption settings for current response
        public float? ResponsePersistenceTime { get; private set; }
        public bool? ResponseAllowInterruption { get; private set; }
        
        // Track last RequestId for automatic state cleanup
        public string LastRequestId { get; private set; }
        
        public ConversationMetadataHandler(string personaName, LatencyTracker latencyTracker, bool enableVerboseLogging = false)
        {
            _personaName = personaName;
            _latencyTracker = latencyTracker;
            _enableVerboseLogging = enableVerboseLogging;
        }
        
        /// <summary>
        /// Processes incoming text message from the server.
        /// Parses the message type and routes to appropriate handler.
        /// Supports both camelCase (backend) and PascalCase (Unity) field names.
        /// </summary>
        /// <param name="message">Raw JSON message string from WebSocket</param>
        public virtual void ProcessMessage(string message)
        {
            // Don't log here - StreamingApiClient handles logging to avoid duplication

            try
            {
                if (string.IsNullOrEmpty(message))
                {
                    Debug.LogWarning($"[{_personaName ?? "Unknown"}] Received null or empty message");
                    return;
                }

                // First parse as JObject to get the type field without deserializing
                // Try both "type" (backend format) and "Type" (Unity format) for compatibility
                var jsonObj = Newtonsoft.Json.Linq.JObject.Parse(message);
                var typeToken = jsonObj["type"] ?? jsonObj["Type"];

                if (typeToken != null)
                {
                    var messageType = typeToken.ToString();

                    if (!string.IsNullOrEmpty(messageType))
                    {
                        HandleTypedMessage(messageType, message);
                        return;
                    }
                }

                Debug.LogWarning($"[{_personaName ?? "Unknown"}] Message without Type field: {message}");
            }
            catch (Exception ex)
            {
                // Log full exception details for better debugging
                Debug.LogError($"[{_personaName ?? "Unknown"}] Error processing message: {ex.Message}\nStack trace: {ex.StackTrace}\nMessage: {message}");
            }
        }
        
        private void HandleTypedMessage(string messageType, string json)
        {
            // Don't log here - StreamingApiClient handles message logging
            

            switch (messageType)
            {
                case WebSocketMessageTypes.ConnectionEstablished:
                    var connMsg = JsonConvert.DeserializeObject<ConnectionEstablishedMessage>(json);
                    Debug.Log($"[{_personaName}] Connection established: {connMsg.ConnectionId}, Quality: {connMsg.NetworkQuality}");
                    OnConnectionEstablished?.Invoke();
                    break;
                    
                case WebSocketMessageTypes.SessionStarted:
                    // Parse the SessionStarted message to get RequestId
                    var sessionMsg = JsonConvert.DeserializeObject<SessionStartedMessage>(json);
                    if (_enableVerboseLogging)
                        Debug.Log($"[{_personaName}] Session started - confirmation received for RequestId: {sessionMsg?.RequestId}");
                    
                    // CRITICAL: Store RequestId immediately when session starts
                    // This ensures audio state resets BEFORE first audio chunk arrives
                    if (sessionMsg != null && !string.IsNullOrEmpty(sessionMsg.RequestId))
                    {
                        LastRequestId = sessionMsg.RequestId;
                        if (_enableVerboseLogging)
                            Debug.Log($"[{_personaName}] RequestId updated early from SessionStarted: {sessionMsg.RequestId}");
                    }
                    
                    OnSessionStarted?.Invoke();
                    break;
                    
                case WebSocketMessageTypes.Transcription:
                case "transcript": // Backend sends lowercase "transcript"
                    var transcMsg = JsonConvert.DeserializeObject<TranscriptionMessage>(json);

                    if (transcMsg != null && transcMsg.IsFinal)
                    {
                        // Log timing data if available
                        //if (transcMsg.Timing != null)
                        //{
                        //    Debug.Log($"[{_personaName}] STT Timing - Service: {transcMsg.Timing.ServiceCreationMs:F1}ms, " +
                        //             $"Config: {transcMsg.Timing.ConfigurationMs:F1}ms, " +
                        //             $"FirstResult: {transcMsg.Timing.FirstResultMs:F1}ms, " +
                        //             $"Total: {transcMsg.Timing.TotalProcessingMs:F1}ms, " +
                        //             $"Provider: {transcMsg.Timing.Provider}");
                        //}

                        // Only track latency if metrics are enabled
                        if (_latencyTracker != null)
                        {
                            _latencyTracker.MarkSttComplete(transcMsg.Text, transcMsg.Timing);
                        }
                        OnTranscription?.Invoke(transcMsg.Text);
                        OnMetadataReceived?.Invoke(new ConversationTurnResponse { PlayerText = transcMsg.Text });
                    }
                    else
                    {
                        // Interim transcriptions - don't log to reduce spam
                        // Debug.Log($"[{_personaName}] Interim transcription: '{transcMsg.Text}'");
                    }
                    break;
                    
                case WebSocketMessageTypes.AiResponse:
                    var aiMsg = JsonConvert.DeserializeObject<AiResponseMessage>(json);
                    //Debug.Log($"[{_personaName}] Received AiResponse: \"{aiMsg.Text}\" (Timing: {(aiMsg.Timing != null ? $"{aiMsg.Timing.FirstResponseMs}ms" : "null")})");
                    
                    // CRITICAL: Log the COMPLETE response for debugging audio artifacts
                    Debug.Log($"[{_personaName}] COMPLETE AI RESPONSE (full text):\n{aiMsg.Text}");
                    //Debug.Log($"[{_personaName}] Response length: {aiMsg.Text?.Length ?? 0} characters, estimated sentences: {aiMsg.Text?.Split(new[] {'.', '!', '?'}, System.StringSplitOptions.RemoveEmptyEntries).Length ?? 0}")
                    
                    // Debug logging to investigate timing issue
                    if (aiMsg.Timing != null)
                    {
                        //Debug.Log($"[{_personaName}] LLM Timing Details - FirstResponseMs: {aiMsg.Timing.FirstResponseMs}, TotalResponseMs: {aiMsg.Timing.TotalResponseMs}, Model: {aiMsg.Timing.Model}, ChunkCount: {aiMsg.Timing.ChunkCount}");
                    }
                    else
                    {
                        Debug.LogWarning($"[{_personaName}] AiResponse received with NULL timing data!");
                    }
                    
                    // Store last NPC response for interruption handling
                    LastNpcResponse = aiMsg.Text;
                    
                    // Store RequestId for automatic state cleanup
                    if (!string.IsNullOrEmpty(aiMsg.RequestId))
                    {
                        LastRequestId = aiMsg.RequestId;
                    }
                    
                    // Extract interruption metadata from LLM response if present
                    ExtractInterruptionMetadata(aiMsg);
                    
                    // Only track latency if metrics are enabled
                    if (_latencyTracker != null)
                    {
                        _latencyTracker.MarkLlmComplete(aiMsg.Text, aiMsg.Timing);
                    }
                    OnAIResponse?.Invoke(aiMsg.Text);
                    OnMetadataReceived?.Invoke(new ConversationTurnResponse { NpcResponseText = aiMsg.Text });
                    break;
                    
                case WebSocketMessageTypes.AudioStreamStart:
                    // REMOVED: AudioStreamStart is no longer sent by backend
                    // Audio now starts automatically when first chunk (with OGG header) is received
                    Debug.LogWarning($"[{_personaName}] Received unexpected AudioStreamStart message - this message type has been deprecated");
                    break;
                    
                case WebSocketMessageTypes.AudioStreamEnd:
                    var audioEndMsg = JsonConvert.DeserializeObject<AudioStreamEndMessage>(json);
                    //Debug.Log($"[{_personaName}] Audio stream ended - Chunks sent: {audioEndMsg.TotalChunksSent}, " +
                    //          $"Bytes: {audioEndMsg.TotalBytesSent}, Sentences: {audioEndMsg.SentenceCount}, " +
                    //          $"Last: '{audioEndMsg.LastSentence}'");
                    
                    // Pass complete message for verification
                    OnAudioStreamEnd?.Invoke(audioEndMsg);
                    break;
                    
                case WebSocketMessageTypes.SpeakingStart:
                    //Debug.Log($"[{_personaName}] Server started TTS - expecting audio stream");
                    // Only track latency if metrics are enabled
                    if (_latencyTracker != null)
                    {
                        _latencyTracker.MarkTtsStart();
                    }
                    // Don't invoke OnAudioStreamStart here - AudioStreamStart message will handle it
                    // This prevents duplicate audio stream initialization
                    break;
                    
                case WebSocketMessageTypes.SpeakingEnd:
                   // Debug.Log($"[{_personaName}] Server finished TTS - audio stream complete");
                    // Don't invoke OnAudioStreamEnd here - AudioStreamEnd message will handle it
                    // This prevents premature stream termination
                    break;
                    
                case WebSocketMessageTypes.Error:
                    var errorMsg = JsonConvert.DeserializeObject<ErrorMessage>(json);
                    Debug.LogError($"[{_personaName}] Server error [{errorMsg.Code}]: {errorMsg.Message} - {errorMsg.Details}");
                    OnError?.Invoke($"[{errorMsg.Code}] {errorMsg.Message}");
                    break;
                    
                case WebSocketMessageTypes.ProcessingStart:
                case WebSocketMessageTypes.ProcessingEnd:
                case WebSocketMessageTypes.ListeningStarted:
                case WebSocketMessageTypes.ListeningStopped:
                case WebSocketMessageTypes.SpeechEnded:
                    Debug.Log($"[{_personaName}] {messageType}");
                    break;
                    
                case WebSocketMessageTypes.ConversationComplete:
                    // CRITICAL FIX: Extract RequestId from conversationComplete message to verify it matches current session
                    // Without this check, turn 1's conversationComplete can complete turn 2's session during overlapping turns!
                    var completeMsg = JsonConvert.DeserializeObject<ConversationCompleteMessage>(json);
                    var completeRequestId = completeMsg?.RequestId;

                    //Debug.Log($"[{_personaName}] Conversation completed - checking if cleanup needed");

                    // Check if audio was received via RequestOrchestrator
                    var orchestrator = RequestOrchestrator.Instance;
                    var audioReceived = false;
                    if (orchestrator != null)
                    {
                        // CRITICAL: Only check/complete if this message is for the CURRENT session
                        // This prevents turn 1's conversationComplete from completing turn 2's session
                        var currentSessionId = orchestrator.GetCurrentSessionId();
                        if (currentSessionId == completeRequestId)
                        {
                            audioReceived = orchestrator.GetCurrentSessionStreamsReceived() > 0;

                            // If no audio was received (e.g. NoTranscript case), we need to clean up the session
                            // With audio, AudioStreamEnd handles cleanup. Without audio, we do it here.
                            if (!audioReceived)
                            {
                                Debug.Log($"[{_personaName}] No audio received for session {completeRequestId} - completing session now");
                                orchestrator.CompleteCurrentSession();
                            }
                            else
                            {
                                Debug.Log($"[{_personaName}] Audio was received ({orchestrator.GetCurrentSessionStreamsReceived()} streams) for session {completeRequestId} - AudioStreamEnd will handle cleanup");
                            }
                        }
                        else
                        {
                            Debug.Log($"[{_personaName}] conversationComplete for old session {completeRequestId}, current is {currentSessionId} - ignoring cleanup");
                        }
                    }

                    // Notify listeners
                    OnConversationComplete?.Invoke(audioReceived);
                    break;
                    
                case WebSocketMessageTypes.NoTranscript:
                    var noTranscriptMsg = JsonConvert.DeserializeObject<NoTranscriptMessage>(json);
                    Debug.LogWarning($"[{_personaName}] No transcript detected - Reason: {noTranscriptMsg.Reason}, " +
                                   $"Duration: {noTranscriptMsg.AudioDuration}ms, Provider: {noTranscriptMsg.SttProvider}");
                    
                    // No need to mark anything on latency tracker - session completion is handled by StreamingApiClient
                    
                    // Notify listeners so they can handle this (e.g., play "Pardon?" audio)
                    OnNoTranscript?.Invoke(noTranscriptMsg);
                    break;
                    
                case WebSocketMessageTypes.BufferHint:
                case "bufferHint": // Backend sends lowercase "bufferHint"
                    var bufferHintMsg = JsonConvert.DeserializeObject<BufferHintMessage>(json);
                    if (bufferHintMsg != null)
                    {
                        Debug.Log($"[{_personaName}] ✅ BufferHint received - TTS Latency: {bufferHintMsg.TtsLatencyMs}ms ({bufferHintMsg.LatencyLevel}), " +
                                 $"Buffer: {bufferHintMsg.RecommendedBufferSize}, Network: {bufferHintMsg.NetworkQuality}");

                        // Update TTS latency in tracker (required for metrics reporting)
                        if (_latencyTracker != null && bufferHintMsg.TtsLatencyMs > 0)
                        {
                            _latencyTracker.UpdateTtsLatency(bufferHintMsg.TtsLatencyMs, bufferHintMsg.LatencyLevel ?? "Unknown");
                            Debug.Log($"[{_personaName}] Updated LatencyTracker with TTS latency: {bufferHintMsg.TtsLatencyMs}ms");
                        }
                        else
                        {
                            Debug.LogWarning($"[{_personaName}] Cannot update TTS latency - LatencyTracker is null or TTS latency is 0");
                        }

                        // Notify adaptive audio buffering system
                        OnBufferHint?.Invoke(bufferHintMsg);
                    }
                    else
                    {
                        Debug.LogError($"[{_personaName}] Failed to deserialize BufferHint message: {json}");
                    }
                    break;
                
                case "sync":
                case "Sync":
                    // Sync message is used for keeping connection alive or synchronization
                    // No action needed, just acknowledge we received it
                    //Debug.Log($"[{_personaName}] Sync message received");
                    break;
                    
                case WebSocketMessageTypes.LatencyMetrics:
                case "latencyMetrics": // Backend sends lowercase "latencyMetrics"
                    var metricsMsg = JsonConvert.DeserializeObject<LatencyMetricsMessage>(json);
                    if (metricsMsg != null)
                    {
                        //Debug.Log($"[{_personaName}] Performance Metrics - STT wait: {metricsMsg.SttWaitMs:F0}ms, " +
                        //          $"LLM wait: {metricsMsg.LlmWaitMs:F0}ms, TTS wait: {metricsMsg.TtsWaitMs:F0}ms, " +
                        //          $"End-to-end: {metricsMsg.EndToEndMs:F0}ms");
                        
                        // Update LLM latency if we didn't have it yet
                        if (metricsMsg.LlmWaitMs.HasValue && metricsMsg.LlmWaitMs.Value > 0)
                        {
                            // Create a minimal LlmTiming object with the wait time
                            var llmTiming = new LlmTiming
                            {
                                LlmWaitMs = metricsMsg.LlmWaitMs.Value,
                                FirstResponseMs = 0, // Not available from latencyMetrics
                                TotalResponseMs = 0,
                                ChunkCount = 0
                            };
                            // Only track latency if metrics are enabled
                            if (_latencyTracker != null)
                            {
                                _latencyTracker.MarkLlmComplete("", llmTiming);
                            }
                        }
                        
                        // Update TTS latency in the tracker if available
                        if (metricsMsg.TtsWaitMs.HasValue && metricsMsg.TtsWaitMs.Value > 0)
                        {
                            // Only track latency if metrics are enabled
                            if (_latencyTracker != null)
                            {
                                _latencyTracker.UpdateTtsLatency(metricsMsg.TtsWaitMs.Value, "Metrics");
                            }
                        }
                        
                        // Log additional details if available
                        //if (metricsMsg.Details != null)
                        //{
                        //    Debug.Log($"[{_personaName}] Metrics details: {JsonConvert.SerializeObject(metricsMsg.Details)}");
                        //}
                    }
                    break;
                
                case WebSocketMessageTypes.SentenceMetadata:
                case "sentenceMetadata": // Backend sends lowercase "sentenceMetadata"
                    var sentenceMetadata = JsonConvert.DeserializeObject<SentenceMetadataMessage>(json);
                    if (sentenceMetadata != null)
                    {
                        //Debug.Log($"[{_personaName}] Animation Metadata - Sentence {sentenceMetadata.SentenceIndex}: " +
                        //         $"\"{sentenceMetadata.SentenceText}\" ({sentenceMetadata.Markers.Count} markers, {sentenceMetadata.EstimatedDurationMs}ms)");
                        
                        // Fire event for animation handlers to process
                        OnSentenceMetadata?.Invoke(sentenceMetadata);
                    }
                    break;
                    
                default:
                    Debug.LogWarning($"[{_personaName}] Unhandled message type: {messageType}");
                    break;
            }
        }
        
        /// <summary>
        /// Extracts interruption metadata from LLM response.
        /// Allows per-response override of interruption settings.
        /// Example LLM response metadata:
        /// {
        ///   "interruption": {
        ///     "allowInterruption": false,  // Disable interruption for this critical response
        ///     "persistenceTime": 0.5        // Or allow quick interruption for casual response
        ///   }
        /// }
        /// </summary>
        private void ExtractInterruptionMetadata(AiResponseMessage aiMsg)
        {
            // Reset per-response overrides
            ResponsePersistenceTime = null;
            ResponseAllowInterruption = null;
            
            if (aiMsg?.LlmResponse == null)
                return;
            
            try
            {
                // Check for interruption metadata in LLM response
                if (aiMsg.LlmResponse.TryGetValue("interruption", out var interruptionToken))
                {
                    var interruption = interruptionToken as Newtonsoft.Json.Linq.JObject;
                    if (interruption != null)
                    {
                        // Extract allowInterruption override
                        if (interruption.TryGetValue("allowInterruption", out var allowToken))
                        {
                            ResponseAllowInterruption = allowToken.ToObject<bool>();
                            Debug.Log($"[{_personaName}] Response override - allowInterruption: {ResponseAllowInterruption}");
                        }
                        
                        // Extract persistenceTime override (in seconds)
                        if (interruption.TryGetValue("persistenceTime", out var persistenceToken))
                        {
                            ResponsePersistenceTime = persistenceToken.ToObject<float>();
                            Debug.Log($"[{_personaName}] Response override - persistenceTime: {ResponsePersistenceTime}s");
                        }
                        
                        // Could also support nearEndThreshold override if needed
                        // if (interruption.TryGetValue("nearEndThreshold", out var nearEndToken))
                        // {
                        //     ResponseNearEndThreshold = nearEndToken.Value<float>();
                        // }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{_personaName}] Error extracting interruption metadata: {ex.Message}");
            }
        }
        
    }
}