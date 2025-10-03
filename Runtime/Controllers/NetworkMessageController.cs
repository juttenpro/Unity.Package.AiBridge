using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using Tsc.AIBridge.Core;
using Tsc.AIBridge.Messages;
using Tsc.AIBridge.WebSocket;
using Tsc.AIBridge.Audio.Capture;

namespace Tsc.AIBridge.Controllers
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
            Debug.Log($"[{_personaName}] SendSessionStartAsync called for session: {session?.RequestId ?? "NULL"}");
            
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
                RequestId = session.RequestId,  // Use RequestId field for session ID
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
                SampleRate = MicrophoneCapture.Frequency,  // UPSTREAM: 16kHz for STT
                OpusBitrate = MicrophoneCapture.UPSTREAM_OPUS_BITRATE  // UPSTREAM: 16kbps
            };
            
            // Use WebSocketClient for SessionStart - Fire and forget (event-driven!)
            Debug.Log($"[{_personaName}] Calling SendSessionStartAsync for {session.RequestId}");
            
            // CRITICAL: Wait for SessionStart to be sent (including JWT auth and WebSocket connection)
            try
            {
                await clientInstance.SendSessionStartAsync(sessionStartMsg);
                
                OnSessionStartSent?.Invoke(session.RequestId);
                
                // Always log SessionStart for debugging
                Debug.Log($"[{_personaName}] SessionStart successfully sent for {session.RequestId} with {sessionStartMsg.Messages?.Count ?? 0} messages");
                
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
                RequestId = session.RequestId,
                Text = text,
                IsNpcInitiated = false,
                Context = new ConversationContext
                {
                    messages = messages ?? new List<ChatMessage>(),
                    voiceId = parameters?.voiceId,
                    ttsStreamingMode = parameters?.ttsStreamingMode,
                    llmModel = parameters?.llmModel,
                    llmProvider = parameters?.llmProvider,
                    language = parameters?.language,
                    temperature = parameters?.temperature ?? 0,
                    maxTokens = parameters?.maxTokens ?? 0,
                    ttsModel = parameters?.ttsModel,
                    sttProvider = parameters?.sttProvider,
                    // Audio settings removed - API always uses opus_48000_64
                    systemPrompt = null // No system prompt for user-initiated
                }
            };
            
            // Send via WebSocketClient - it will handle connection internally
            _ = WebSocketClient.Instance.SendTextInputAsync(textInputMsg);
            
            if (_enableVerboseLogging)
                Debug.Log($"[{_personaName}] Sending TextInput: '{text}'");
            
            OnTextInputSent?.Invoke(session.RequestId);
            
            if (_enableVerboseLogging)
                Debug.Log($"[{_personaName}] TextInput sent: '{text}'");
        }

        /// <summary>
        /// Send DirectTTS message - text directly to TTS without LLM processing.
        /// Useful for scripted NPC dialogue, system messages, or pre-defined responses.
        /// </summary>
        /// <param name="session">The conversation session</param>
        /// <param name="text">The text to convert to speech</param>
        /// <param name="voice">Optional voice override (null = use default)</param>
        /// <param name="model">Optional TTS model override (null = use default)</param>
        public void SendDirectTTS(
            ConversationSession session,
            string text,
            string voice = null,
            string model = null)
        {
            if (session == null || string.IsNullOrEmpty(text))
            {
                Debug.LogWarning($"[{_personaName}] Cannot send DirectTTS without session and text");
                return;
            }

            var directTtsMsg = new DirectTTSMessage
            {
                Type = "directtts",  // Lowercase per protocol
                RequestId = session.RequestId,
                Text = text,
                Voice = voice,  // Optional - null means use default
                Model = model   // Optional - null means use default
            };

            // Send via WebSocketClient - it will handle connection internally
            _ = WebSocketClient.Instance.SendDirectTTSAsync(directTtsMsg);

            if (_enableVerboseLogging)
                Debug.Log($"[{_personaName}] Sending DirectTTS: '{text}' (voice: {voice ?? "default"}, model: {model ?? "default"})");

            // Signal that DirectTTS was sent (could add event if needed)
            OnTextInputSent?.Invoke(session.RequestId);

            if (_enableVerboseLogging)
                Debug.Log($"[{_personaName}] DirectTTS sent: '{text}'");
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
            Debug.Log($"[{_personaName}] Calling SendEndOfSpeechAsync for {session.RequestId}");
            _ = WebSocketClient.Instance.SendEndOfSpeechAsync(session.RequestId);
            
            OnEndOfSpeechSent?.Invoke(session.RequestId);
            
            // Always log EndOfSpeech for debugging
            Debug.Log($"[{_personaName}] EndOfSpeech sent for {session.RequestId}");
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
