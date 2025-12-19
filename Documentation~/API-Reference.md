# AI Bridge API Reference

Complete API documentation for the AI Bridge package.

## Table of Contents

1. [Core Classes](#core-classes)
   - [RequestOrchestrator](#requestorchestrator)
   - [WebSocketClient](#websocketclient)
   - [NpcClientBase](#npcclientbase)
   - [SimpleNpcClient](#simplenpcclient)
2. [Audio Classes](#audio-classes)
   - [StreamingAudioPlayer](#streamingaudioplayer)
   - [MicrophoneCapture](#microphonecapture)
   - [SpeechInputHandler](#speechinputhandler)
3. [Interfaces](#interfaces)
   - [INpcConfiguration](#inpcconfiguration)
   - [INpcProvider](#inpcprovider)
   - [IConversationHistory](#iconversationhistory)
   - [IAudioCaptureProvider](#iaudiocaptureprovider)
   - [IApiKeyProvider](#iapikeyprovider)
4. [Data Types](#data-types)
   - [ConversationRequest](#conversationrequest)
   - [ChatMessage](#chatmessage)
   - [WebSocket Messages](#websocket-messages)
5. [Enums](#enums)

---

## Core Classes

### RequestOrchestrator

Central coordinator for all AI conversation flows.

**Namespace:** `Tsc.AIBridge.Core`

**Inheritance:** `MonoBehaviour`

#### Properties

| Property | Type | Description |
|----------|------|-------------|
| `Instance` | `RequestOrchestrator` | Singleton instance (static) |

#### Methods

##### StartAudioRequest

Starts an audio-based conversation request (user speaks to NPC).

```csharp
public void StartAudioRequest(INpcConfiguration npcConfig)
public void StartAudioRequest(string npcId)
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `npcConfig` | `INpcConfiguration` | NPC configuration for the conversation |
| `npcId` | `string` | NPC identifier to look up via INpcProvider |

##### EndAudioRequest

Ends the current audio recording and sends for processing.

```csharp
public void EndAudioRequest()
```

##### StartTextRequest

Starts a text-based request (NPC initiates or text input).

```csharp
public void StartTextRequest(INpcConfiguration npcConfig, string text)
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `npcConfig` | `INpcConfiguration` | NPC configuration |
| `text` | `string` | Text input (empty for NPC-initiated) |

##### StartConversationRequest

Starts a conversation with full configuration options.

```csharp
public void StartConversationRequest(ConversationRequest request)
```

##### CancelCurrentSession

Cancels any active conversation session.

```csharp
public void CancelCurrentSession(string reason = "User cancelled")
```

##### IsProcessingRequest

Returns whether a request is currently being processed.

```csharp
public bool IsProcessingRequest()
```

**Returns:** `true` if processing, `false` otherwise.

##### IsAudioPlaying

Returns whether audio is currently playing.

```csharp
public bool IsAudioPlaying()
```

##### HandlePauseStateChange

Notifies the orchestrator of application pause state changes.

```csharp
public void HandlePauseStateChange(bool isPaused, string source = "Unknown")
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `isPaused` | `bool` | Whether the application is paused |
| `source` | `string` | Source of the pause (for logging) |

#### Events

| Event | Type | Description |
|-------|------|-------------|
| `OnTranscriptionReceived` | `Action<string>` | Fired when STT transcription is received |
| `OnSttFailed` | `Action<NoTranscriptMessage>` | Fired when STT fails (timeout, silence, etc.) |
| `OnActiveNpcChanged` | `Action<NpcClientBase, INpcConfiguration>` | Fired when active NPC changes |
| `OnSessionStarted` | `Action` | Fired when a new session starts |
| `OnSessionEnded` | `Action` | Fired when a session completes |

#### Inspector Properties

| Property | Type | Description |
|----------|------|-------------|
| `speechInputHandler` | `SpeechInputHandler` | Required: Audio input handler |
| `interruptionManager` | `InterruptionManager` | Optional: Handles interruption detection |
| `npcProviderComponent` | `Component` | Optional: INpcProvider implementation |
| `enableMetrics` | `bool` | Enable latency tracking |
| `enableVerboseLogging` | `bool` | Enable detailed logging |

---

### WebSocketClient

Manages WebSocket connection to the backend service.

**Namespace:** `Tsc.AIBridge.WebSocket`

**Inheritance:** `MonoBehaviour`

#### Properties

| Property | Type | Description |
|----------|------|-------------|
| `Instance` | `WebSocketClient` | Singleton instance (static) |
| `IsConnected` | `bool` | Whether WebSocket is connected |
| `State` | `ConnectionState` | Current connection state |

#### Methods

##### EnsureConnectionAsync

Ensures WebSocket connection is established.

```csharp
public async Task<bool> EnsureConnectionAsync()
```

**Returns:** `true` if connected successfully.

##### Disconnect

Disconnects the WebSocket.

```csharp
public void Disconnect()
```

##### SendSessionStartAsync

Sends a session start message.

```csharp
public async Task SendSessionStartAsync(SessionStartMessage message)
```

##### SendBinaryAsync

Sends binary audio data.

```csharp
public async Task SendBinaryAsync(byte[] data, string requestId)
```

##### SendTextInputAsync

Sends text input to the backend.

```csharp
public async Task SendTextInputAsync(string npcId, string text, string requestId)
```

#### Events

| Event | Type | Description |
|-------|------|-------------|
| `OnConnectionStateChanged` | `Action<ConnectionState>` | Fired when connection state changes |
| `OnMessageReceived` | `Action<string>` | Fired when JSON message received |
| `OnBinaryReceived` | `Action<byte[]>` | Fired when binary data received |

#### Inspector Properties

| Property | Type | Description |
|----------|------|-------------|
| `apiBaseUrl` | `string` | Backend API base URL |
| `webSocketEndpoint` | `string` | WebSocket endpoint path |
| `establishConnectionOnStart` | `bool` | Connect automatically on Start |
| `sendWakeUpCall` | `bool` | Send wake-up to prevent cold start |
| `persistAcrossScenes` | `bool` | DontDestroyOnLoad |
| `apiKeyProviderComponent` | `Component` | IApiKeyProvider implementation |
| `enableVerboseLogging` | `bool` | Enable detailed logging |

---

### NpcClientBase

Abstract base class for NPC implementations.

**Namespace:** `Tsc.AIBridge.Core`

**Inheritance:** `MonoBehaviour`, `IConversationHistory`, `INpcMessageHandler`

#### Abstract Properties

| Property | Type | Description |
|----------|------|-------------|
| `NpcName` | `string` | Display name of the NPC |

#### Virtual Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `SystemPrompt` | `string` | `""` | System prompt for LLM |
| `VoiceId` | `string` | `""` | TTS voice identifier |
| `TtsModel` | `string` | `"eleven_turbo_v2_5"` | TTS model name |
| `TtsStreamingMode` | `string` | `"sentence"` | Streaming mode |
| `TtsSpeed` | `float` | `1.0f` | Voice speed multiplier |
| `LlmProvider` | `string` | `"openai"` | LLM provider |
| `LlmModel` | `string` | `"gpt-4o-mini"` | LLM model name |
| `Temperature` | `float` | `0.7f` | LLM temperature |
| `MaxTokens` | `int` | `500` | Max response tokens |
| `SttProvider` | `string` | `"google"` | STT provider |
| `Language` | `string` | `"en-US"` | Language code |
| `AllowInterruption` | `bool` | `true` | Allow user interruption |

#### Properties

| Property | Type | Description |
|----------|------|-------------|
| `NpcId` | `string` | Unique identifier (auto-generated GUID) |
| `IsActive` | `bool` | Whether NPC is active for conversation |
| `IsTalking` | `bool` | Whether NPC is currently speaking |
| `IsListening` | `bool` | Whether NPC is listening to user |
| `LastResponseText` | `string` | Last AI response text |

#### Methods

##### StopAudio

Stops current audio playback.

```csharp
public virtual void StopAudio()
```

##### PauseAudio

Pauses audio playback.

```csharp
public virtual void PauseAudio()
```

##### ResumeAudio

Resumes paused audio.

```csharp
public virtual void ResumeAudio()
```

##### GetApiHistoryAsChatMessages

Gets conversation history as chat messages.

```csharp
public virtual List<ChatMessage> GetApiHistoryAsChatMessages()
```

##### AddPlayerMessage

Adds a player message to history.

```csharp
public virtual void AddPlayerMessage(string message)
```

##### ClearHistory

Clears conversation history.

```csharp
public virtual void ClearHistory()
```

#### Events

| Event | Type | Description |
|-------|------|-------------|
| `OnSessionStarted` | `Action` | Fired when session starts |
| `OnAudioStarted` | `Action` | Fired when audio playback begins |
| `OnAudioStopped` | `Action` | Fired when audio playback ends |
| `OnAiResponseReceived` | `Action<AiResponseMessage>` | Fired when AI response received |
| `OnStartListening` | `Action` | Fired when NPC starts listening |
| `OnStopListening` | `Action` | Fired when NPC stops listening |
| `OnStartSpeaking` | `Action` | Fired when NPC starts speaking |
| `OnStopSpeaking` | `Action` | Fired when NPC stops speaking |

---

### SimpleNpcClient

Basic NPC implementation with Inspector-configurable properties.

**Namespace:** `Tsc.AIBridge.Core`

**Inheritance:** `NpcClientBase`

All properties from `NpcClientBase` are exposed as serialized fields in the Inspector.

#### Inspector Properties

| Property | Type | Description |
|----------|------|-------------|
| `npcName` | `string` | NPC display name |
| `systemPrompt` | `string` | System prompt (TextArea) |
| `voiceId` | `string` | ElevenLabs voice ID |
| `ttsModel` | `string` | TTS model |
| `voiceSpeed` | `float` | Voice speed (0.7-1.3) |
| `llmProvider` | `string` | LLM provider |
| `llmModel` | `string` | LLM model name |
| `temperature` | `float` | LLM temperature (0-1) |
| `sttProvider` | `string` | STT provider |
| `language` | `string` | Language code |
| `allowInterruption` | `bool` | Allow interruption |
| `audioSource` | `AudioSource` | Audio output |
| `streamingAudioPlayer` | `StreamingAudioPlayer` | Audio player |

---

## Audio Classes

### StreamingAudioPlayer

Handles real-time streaming audio playback.

**Namespace:** `Tsc.AIBridge.Audio.Playback`

**Inheritance:** `MonoBehaviour`

#### Properties

| Property | Type | Description |
|----------|------|-------------|
| `IsPlaying` | `bool` | Whether audio is playing |
| `IsPaused` | `bool` | Whether playback is paused |

#### Methods

##### StartStream

Starts a new audio stream.

```csharp
public void StartStream()
```

##### EnqueueAudioData

Adds audio samples to the playback buffer.

```csharp
public void EnqueueAudioData(float[] samples)
```

##### PausePlayback

Pauses audio playback.

```csharp
public virtual void PausePlayback()
```

##### ResumePlayback

Resumes paused playback.

```csharp
public virtual void ResumePlayback()
```

##### StopPlayback

Stops playback and clears buffers.

```csharp
public void StopPlayback()
```

#### Events

| Event | Type | Description |
|-------|------|-------------|
| `OnPlaybackStarted` | `Action` | Fired when playback begins |
| `OnPlaybackComplete` | `Action` | Fired when stream finishes |

#### Inspector Properties

| Property | Type | Description |
|----------|------|-------------|
| `audioSource` | `AudioSource` | Required: Audio output |
| `playbackCompleteTimeout` | `float` | Timeout for completion detection (0.05-2.0s) |

---

### MicrophoneCapture

Captures audio from the microphone.

**Namespace:** `Tsc.AIBridge.Audio.Capture`

**Inheritance:** `MonoBehaviour`, `IAudioCaptureProvider`

#### Properties

| Property | Type | Description |
|----------|------|-------------|
| `IsCapturing` | `bool` | Whether capture is active |
| `SampleRate` | `int` | Audio sample rate (16000) |
| `Channels` | `int` | Audio channels (1 = mono) |
| `CurrentVolume` | `float` | Current input volume (0-1) |

#### Methods

##### StartCapture

Starts microphone capture.

```csharp
public void StartCapture()
```

##### StopCapture

Stops microphone capture.

```csharp
public void StopCapture()
```

##### SelectDevice

Selects a microphone device.

```csharp
public bool SelectDevice(string deviceName)
```

**Returns:** `true` if device was found and selected.

##### GetAvailableDevices

Gets list of available microphone devices.

```csharp
public string[] GetAvailableDevices()
```

#### Events

| Event | Type | Description |
|-------|------|-------------|
| `OnCaptureStarted` | `Action` | Fired when capture begins |
| `OnCaptureStopped` | `Action` | Fired when capture ends |
| `OnAudioDataAvailable` | `Action<float[]>` | Fired with audio samples |
| `OnError` | `Action<string>` | Fired on error |

---

### SpeechInputHandler

Public interface for speech input control.

**Namespace:** `Tsc.AIBridge.Input`

**Inheritance:** `MonoBehaviour`

#### Properties

| Property | Type | Description |
|----------|------|-------------|
| `IsRecording` | `bool` | Whether currently recording |
| `RecordingMode` | `RecordingMode` | Current recording mode |

#### Methods

##### StartRecording

Manually starts recording (for PTT).

```csharp
public void StartRecording()
```

##### StopRecording

Manually stops recording.

```csharp
public void StopRecording()
```

#### Events

| Event | Type | Description |
|-------|------|-------------|
| `OnUserStartedSpeaking` | `Action` | User began speaking |
| `OnUserStoppedSpeaking` | `Action` | User stopped speaking |
| `OnAudioDataReceived` | `Action<float[]>` | Raw audio data |

#### Inspector Properties

| Property | Type | Description |
|----------|------|-------------|
| `audioCapture` | `IAudioCaptureProvider` | Audio capture component |
| `recordingMode` | `RecordingMode` | Recording mode selection |
| `pttKey` | `KeyCode` | Push-to-talk key |
| `fixedDelay` | `float` | Fixed delay after PTT release |
| `useSmartOffset` | `bool` | Enable VAD-based smart offset |

---

## Interfaces

### INpcConfiguration

Defines NPC configuration for conversations.

**Namespace:** `Tsc.AIBridge.Core`

```csharp
public interface INpcConfiguration
{
    // Identity
    string Id { get; }
    string Name { get; }

    // System prompt & history
    string SystemPrompt { get; }
    List<ChatMessage> Messages { get; }

    // Voice settings
    string VoiceId { get; }
    string TtsModel { get; }
    string TtsStreamingMode { get; }
    float TtsSpeed { get; }
    float TtsStability { get; }
    float TtsSimilarityBoost { get; }
    float TtsStyle { get; }
    bool TtsSpeakerBoost { get; }

    // LLM settings
    string LlmProvider { get; }
    string LlmModel { get; }
    float Temperature { get; }
    int MaxTokens { get; }

    // STT settings
    string SttProvider { get; }
    string Language { get; }
    string[] CustomVocabulary { get; }

    // Interruption settings
    bool AllowInterruption { get; }
    float InterruptionPersistenceTime { get; }

    // State
    bool IsActive { get; }
    bool IsTalking { get; }

    // Events
    event Action OnStartListening;
    event Action OnStopListening;
    event Action OnStartSpeaking;
    event Action OnStopSpeaking;
}
```

---

### INpcProvider

Service for finding NPCs in the scene.

**Namespace:** `Tsc.AIBridge.Core`

```csharp
public interface INpcProvider
{
    INpcConfiguration GetNpcConfiguration(string npcId);
    NpcClientBase GetNpcClient(string npcId);
}
```

---

### IConversationHistory

Manages conversation history.

**Namespace:** `Tsc.AIBridge.Core`

```csharp
public interface IConversationHistory
{
    List<ChatMessage> GetApiHistoryAsChatMessages();
    void ClearHistory();
    void AddPlayerMessage(string message);
}
```

---

### IAudioCaptureProvider

Interface for audio input implementations.

**Namespace:** `Tsc.AIBridge.Audio.Capture`

```csharp
public interface IAudioCaptureProvider
{
    // Events
    event Action OnCaptureStarted;
    event Action OnCaptureStopped;
    event Action<float[]> OnAudioDataAvailable;
    event Action<string> OnError;

    // Properties
    bool IsCapturing { get; }
    int SampleRate { get; }
    int Channels { get; }
    float CurrentVolume { get; }

    // Methods
    void StartCapture();
    void StopCapture();
    bool SelectDevice(string deviceName);
    string[] GetAvailableDevices();
}
```

---

### IApiKeyProvider

Synchronous API key retrieval.

**Namespace:** `Tsc.AIBridge.Auth`

```csharp
public interface IApiKeyProvider
{
    string GetOrchestratorApiKey();
}
```

### IAsyncApiKeyProvider

Asynchronous API key retrieval via coroutines.

**Namespace:** `Tsc.AIBridge.Auth`

```csharp
public interface IAsyncApiKeyProvider
{
    IEnumerator GetOrchestratorApiKeyAsync(Action<bool, string> callback);
}
```

**Callback Parameters:**
- `bool success`: Whether key retrieval succeeded
- `string result`: API key (if success) or error message (if failed)

---

## Data Types

### ConversationRequest

Complete configuration for starting a conversation.

**Namespace:** `Tsc.AIBridge.Core`

```csharp
public class ConversationRequest
{
    // Required
    public string NpcId { get; set; }

    // Optional - NPC settings (override INpcConfiguration)
    public string SystemPrompt { get; set; }
    public List<ChatMessage> Messages { get; set; }

    // Voice settings
    public string TtsVoice { get; set; }
    public string TtsModel { get; set; }
    public string TtsStreamingMode { get; set; }
    public float TtsSpeed { get; set; }
    public float TtsStability { get; set; }
    public float TtsSimilarityBoost { get; set; }
    public float TtsStyle { get; set; }
    public bool TtsSpeakerBoost { get; set; }

    // LLM settings
    public string LlmProvider { get; set; }
    public string LlmModel { get; set; }
    public float Temperature { get; set; }
    public int MaxTokens { get; set; }

    // STT settings
    public string SttProvider { get; set; }
    public string Language { get; set; }
    public string[] CustomVocabulary { get; set; }

    // Request type
    public bool IsNpcInitiated { get; set; }
    public string InitialPrompt { get; set; }
}
```

---

### ChatMessage

Represents a single message in conversation history.

**Namespace:** `Tsc.AIBridge.Messages`

```csharp
public class ChatMessage
{
    public string role { get; set; }    // "user", "assistant", "system"
    public string content { get; set; }
    public string metadata { get; set; } // Optional
}
```

---

### WebSocket Messages

#### SessionStartMessage

Sent to backend to start a conversation session.

```csharp
public class SessionStartMessage
{
    public string type = "session_start";
    public string sessionId;
    public string requestId;
    public string npcId;

    // Content
    public string systemPrompt;
    public List<ChatMessage> messages;

    // STT settings
    public string sttProvider;
    public string sttLanguage;
    public string[] customVocabulary;

    // LLM settings
    public string llmProvider;
    public string llmModel;
    public float temperature;
    public int maxTokens;

    // TTS settings
    public string ttsVoice;
    public string ttsModel;
    public string ttsStreamingMode;
    public float? VoiceSpeed;
    public float? VoiceStability;
    public float? VoiceSimilarityBoost;
    public float? VoiceStyle;
    public bool? VoiceUseSpeakerBoost;
}
```

#### TranscriptionMessage

Received when STT completes.

```csharp
public class TranscriptionMessage
{
    public string type = "transcription";
    public string requestId;
    public string text;
    public float confidence;
}
```

#### AiResponseMessage

Received with LLM response.

```csharp
public class AiResponseMessage
{
    public string type = "ai_response";
    public string requestId;
    public string content;
    public string model;
    public TokenUsage usage;
}

public class TokenUsage
{
    public int promptTokens;
    public int completionTokens;
    public int totalTokens;
}
```

#### NoTranscriptMessage

Received when STT fails.

```csharp
public class NoTranscriptMessage
{
    public string type = "no_transcript";
    public string requestId;
    public string reason; // "silence", "timeout", "unclear", "error"
    public string details;
}
```

#### ConversationCompleteMessage

Received when session completes.

```csharp
public class ConversationCompleteMessage
{
    public string type = "conversation_complete";
    public string requestId;
    public LatencyMetrics metrics;
}
```

---

## Enums

### ConnectionState

WebSocket connection states.

**Namespace:** `Tsc.AIBridge.WebSocket`

```csharp
public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting
}
```

### RecordingMode

Available recording modes.

**Namespace:** `Tsc.AIBridge.Input`

```csharp
public enum RecordingMode
{
    PushToTalk,      // Manual start/stop with key
    VoiceActivation, // Automatic via VAD
    SmartOffset      // PTT + VAD silence detection
}
```

---

## Provider Values

### LLM Providers

| Value | Description |
|-------|-------------|
| `"openai"` | OpenAI GPT models |
| `"vertexai"` | Google Vertex AI |
| `"azure-openai"` | Azure OpenAI Service |

### LLM Models

| Provider | Models |
|----------|--------|
| OpenAI | `gpt-4o`, `gpt-4o-mini`, `gpt-4-turbo` |
| Vertex AI | `gemini-1.5-pro`, `gemini-1.5-flash` |
| Azure | Same as OpenAI (deployment names) |

### STT Providers

| Value | Description |
|-------|-------------|
| `"google"` | Google Cloud Speech-to-Text |
| `"azure"` | Azure Speech Services |
| `"openai"` | OpenAI Whisper |

### TTS Models

| Value | Description |
|-------|-------------|
| `"eleven_turbo_v2_5"` | ElevenLabs Turbo (low latency) |
| `"eleven_flash_v2_5"` | ElevenLabs Flash (faster) |
| `"eleven_multilingual_v2"` | ElevenLabs Multilingual |

### TTS Streaming Modes

| Value | Description |
|-------|-------------|
| `"sentence"` | Stream after each sentence |
| `"batch"` | Stream entire response at once |

---

## Usage Patterns

### Minimal NPC Setup

```csharp
public class MinimalNpc : NpcClientBase
{
    public override string NpcName => "Assistant";

    // Uses all default values for other properties
}
```

### Custom NPC with All Options

```csharp
public class FullyConfiguredNpc : NpcClientBase
{
    [SerializeField] private NpcConfigSO config;

    public override string NpcName => config.name;
    public override string SystemPrompt => config.systemPrompt;
    public override string VoiceId => config.voiceId;
    public override string TtsModel => "eleven_turbo_v2_5";
    public override float TtsSpeed => config.voiceSpeed;
    public override string LlmProvider => "openai";
    public override string LlmModel => "gpt-4o";
    public override float Temperature => config.temperature;
    public override int MaxTokens => 1000;
    public override string SttProvider => "google";
    public override string Language => "en-US";
    public override bool AllowInterruption => true;

    private List<ChatMessage> _history = new();

    public override List<ChatMessage> GetApiHistoryAsChatMessages() => _history;

    public override void AddPlayerMessage(string message)
    {
        _history.Add(new ChatMessage { role = "user", content = message });
    }

    public override void ClearHistory() => _history.Clear();
}
```

### Custom API Key Provider

```csharp
public class SecureKeyProvider : MonoBehaviour, IAsyncApiKeyProvider
{
    [SerializeField] private string keyVaultUrl;

    public IEnumerator GetOrchestratorApiKeyAsync(Action<bool, string> callback)
    {
        using var request = UnityWebRequest.Get(keyVaultUrl);
        request.SetRequestHeader("Authorization", GetAuthToken());

        yield return request.SendWebRequest();

        if (request.result == UnityWebRequest.Result.Success)
        {
            var response = JsonUtility.FromJson<KeyResponse>(request.downloadHandler.text);
            callback(true, response.apiKey);
        }
        else
        {
            callback(false, request.error);
        }
    }
}
```

---

For more information, see:
- [Getting Started](GettingStarted.md) - Setup guide
- [Best Practices](BestPractices.md) - Implementation patterns
- [Examples](Examples.md) - Real-world examples
