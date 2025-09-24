using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using SimulationCrew.AIBridge.Core;
using SimulationCrew.AIBridge.Messages;
using SimulationCrew.AIBridge.WebSocket;
using SimulationCrew.AIBridge.Audio.Capture;
using Newtonsoft.Json;

namespace SimulationCrew.AIBridge.Controllers
{
    /// <summary>
    /// Network message builder and sender - formats messages, NOT connection manager.
    /// 
    /// RESPONSIBILITIES:
    /// ✅ DO: Build properly formatted WebSocket messages
    /// ✅ DO: Add required fields (audio format, sample rate, etc.)
    /// ✅ DO: Convert between message types (Message → ChatMessage)
    /// ✅ DO: Call WebSocketClient.SendXAsync methods
    /// ✅ DO: Fire events when messages are sent
    /// 
    /// ❌ DON'T: Check if connected (WebSocketClient handles this)
    /// ❌ DON'T: Manage connection state or timing
    /// ❌ DON'T: Implement retry logic
    /// ❌ DON'T: Make decisions about when to connect
    /// ❌ DON'T: Store WebSocket references or state
    /// 
    /// PRINCIPLE: Message factory, NOT connection manager!
    /// Just builds messages and calls WebSocketClient.SendXAsync.
    /// WebSocketClient will ensure connection before sending.
    /// </summary>
    public class NetworkMessageController
    {
        private readonly string _personaName;
        private readonly bool _enableVerboseLogging;
        
        // Events for tracking message sending
        public event Action<string> OnSessionStartSent;
        public event Action<string> OnEndOfSpeechSent;
        // OnEndOfAudioSent removed - not used in current implementation
        public event Action<string> OnTextInputSent;
        
        public NetworkMessageController(
            string personaName,
            bool enableVerboseLogging = false)
        {
            _personaName = personaName ?? "Unknown";
            _enableVerboseLogging = enableVerboseLogging;
        }
        
        /// <summary>
        /// Send SessionStart message with all required parameters and wait for connection
        /// </summary>
        public virtual async Task<bool> SendSessionStartAsync(
            ConversationSession session,
            List<ChatMessage> messages,
            ConversationParameters parameters)
        {
            Debug.Log($"[{_personaName}] SendSessionStartAsync called for session: {session?.SessionId ?? "NULL"}");
            
            if (session == null)
            {
                Debug.LogWarning($"[{_personaName}] Cannot send SessionStart without session");
                return false;
            }
            
            // Get WebSocketClient instance - SendSessionStartAsync will handle connection
            var clientInstance = WebSocketClient.Instance;
            if (clientInstance == null)
            {
                Debug.LogError($"[{_personaName}] WebSocketClient.Instance is null!");
                return false;
            }
            
            Debug.Log($"[{_personaName}] Preparing to send SessionStart - connection will be established if needed");
            
            // Build proper SessionStartMessage
            var sessionStartMsg = new SessionStartMessage
            {
                Type = WebSocketMessageTypes.SessionStart,
                RequestId = session.SessionId,  // Use RequestId field for session ID
                Messages = messages,  // Now directly uses ChatMessage list
                LanguageCode = parameters?.language ?? "nl-NL",
                VoiceId = parameters?.voiceId,
                LlmProvider = parameters?.llmProvider,  // CRITICAL: was missing, causing no LLM response!
                LlmModel = parameters?.llmModel,
                TtsModel = parameters?.ttsModel,
                SttProvider = parameters?.sttProvider,
                MaxTokens = parameters?.maxTokens ?? 500,
                Temperature = parameters?.temperature ?? 0.7f,
                // Use TTS streaming mode from PersonaSO via parameters
                TtsStreamingMode = parameters?.ttsStreamingMode ?? "batch",  // Default to batch if not specified
                // CRITICAL audio format fields - without these STT cannot decode audio!
                AudioFormat = "opus",      // Must be opus for compressed audio
                SampleRate = MicrophoneCapture.Frequency,  // Use actual recording frequency (16000)
                OpusBitrate = 64000        // 64kbps Opus encoding
            };
            
            // Use WebSocketClient for SessionStart - Fire and forget (event-driven!)
            Debug.Log($"[{_personaName}] Calling SendSessionStartAsync for {session.SessionId}");
            
            // CRITICAL: Wait for SessionStart to be sent (including JWT auth and WebSocket connection)
            try
            {
                await clientInstance.SendSessionStartAsync(sessionStartMsg);
                
                OnSessionStartSent?.Invoke(session.SessionId);
                
                // Always log SessionStart for debugging
                Debug.Log($"[{_personaName}] SessionStart successfully sent for {session.SessionId} with {sessionStartMsg.Messages?.Count ?? 0} messages");
                
                // Log critical audio configuration
                Debug.Log($"[{_personaName}] Audio config: Format={sessionStartMsg.AudioFormat}, SampleRate={sessionStartMsg.SampleRate}, Bitrate={sessionStartMsg.OpusBitrate}");
                
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[{_personaName}] Failed to send SessionStart: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Send TextInput message for NPC-initiated conversations
        /// </summary>
        public void SendTextInput(
            ConversationSession session,
            string text,
            List<ChatMessage> messages,
            ConversationParameters parameters)
        {
            if (session == null || string.IsNullOrEmpty(text))
            {
                Debug.LogWarning($"[{_personaName}] Cannot send TextInput without session and text");
                return;
            }
            
            // ARCHITECTURE: Don't check IsConnected - let WebSocketClient handle that internally
            
            var textInputMsg = new TextInputMessage
            {
                Type = "textinput",  // Lowercase per protocol
                RequestId = session.SessionId,
                Text = text,
                Messages = messages ?? new List<ChatMessage>(),
                ConversationParameters = parameters
            };
            
            // Send via WebSocketClient - it will handle connection internally
            _ = WebSocketClient.Instance.SendTextInputAsync(textInputMsg);
            
            if (_enableVerboseLogging)
                Debug.Log($"[{_personaName}] Sending TextInput: '{text}'");
            
            OnTextInputSent?.Invoke(session.SessionId);
            
            if (_enableVerboseLogging)
                Debug.Log($"[{_personaName}] TextInput sent: '{text}'");
        }
        
        /// <summary>
        /// Send EndOfSpeech message
        /// </summary>
        public virtual void SendEndOfSpeech(ConversationSession session)
        {
            if (session == null)
            {
                if (_enableVerboseLogging)
                    Debug.Log($"[{_personaName}] Cannot send EndOfSpeech without session");
                return;
            }
            
            if (!WebSocketClient.Instance?.IsConnected ?? true)
            {
                if (_enableVerboseLogging)
                    Debug.Log($"[{_personaName}] Not connected - skipping EndOfSpeech");
                return;
            }
            
            // Use WebSocketClient for EndOfSpeech - Fire and forget (event-driven!)
            Debug.Log($"[{_personaName}] Calling SendEndOfSpeechAsync for {session.SessionId}");
            _ = WebSocketClient.Instance.SendEndOfSpeechAsync(session.SessionId);
            
            OnEndOfSpeechSent?.Invoke(session.SessionId);
            
            // Always log EndOfSpeech for debugging
            Debug.Log($"[{_personaName}] EndOfSpeech sent for {session.SessionId}");
        }
        
        /// <summary>
        /// Send binary audio data
        /// </summary>
        public virtual void SendAudioData(byte[] audioData)
        {
            if (audioData == null || audioData.Length == 0)
            {
                return;
            }
            
            if (!WebSocketClient.Instance?.IsConnected ?? true)
            {
                if (_enableVerboseLogging)
                    Debug.Log($"[{_personaName}] Not connected - buffering audio data");
                return;
            }
            
            // Fire-and-forget for performance
            _ = WebSocketClient.Instance.SendBinaryAsync(audioData);
            
            if (_enableVerboseLogging && audioData.Length > 0)
            {
                Debug.Log($"[{_personaName}] Sent {audioData.Length} bytes of audio data");
            }
        }
        
        /// <summary>
        /// Send an analysis request to evaluate a conversation
        /// </summary>
        //public async Task SendAnalysisRequestAsync(string systemPrompt, string llmModel = "gpt-4o-mini", float temperature = 0.7f, int maxTokens = 500)
        //{
        //    if (!WebSocketClient.Instance?.IsConnected ?? true)
        //    {
        //        Debug.LogError($"[{_personaName}] Cannot send analysis request - not connected");
        //        return;
        //    }

        //    var analysisRequest = new AnalysisRequestMessage
        //    {
        //        SystemPrompt = systemPrompt,
        //        LlmModel = llmModel,
        //        Temperature = temperature,  // NOW USED!
        //        MaxTokens = maxTokens,      // NOW USED!
        //        RequestId = Guid.NewGuid().ToString(),
        //        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        //    };

        //    Debug.Log($"[{_personaName}] Sending analysis request with model={llmModel}, temp={temperature}, maxTokens={maxTokens}");

        //    await WebSocketClient.Instance.SendAnalysisRequestAsync(analysisRequest);
        //}

    }
}
