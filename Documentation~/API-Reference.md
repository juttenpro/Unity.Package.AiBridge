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

Abstract base class for NPC implementations. Handles WebSocket message routing, audio playback lifecycle, and conversation state management. Extend this class to create custom NPC implementations.

**Namespace:** `Tsc.AIBridge.Core`

**Inheritance:** `MonoBehaviour`

**Implements:** `IConversationHistory`, `INpcMessageHandler`

#### Abstract Members

| Member | Type | Description |
|--------|------|-------------|
| `NpcName` | `string` (property) | Display name of the NPC |
| `ValidateConfiguration()` | `void` (method) | Called on Start to validate Inspector settings |

#### Properties

| Property | Type | Description |
|----------|------|-------------|
| `NpcId` | `string` | Unique identifier (based on GameObject instance ID) |
| `IsActive` | `bool` | Whether NPC is active for conversation |
| `IsTalking` | `bool` | Whether NPC is currently speaking |
| `IsSpeaking` | `bool` | Alias for IsTalking |
| `IsListening` | `bool` | Whether NPC is listening to user |
| `LastResponseText` | `string` | Last AI response text |
| `AudioPlayer` | `StreamingAudioPlayer` | Auto-discovered audio player (from children) |
| `MetadataHandler` | `ConversationMetadataHandler` | Internal message handler |

> **Note**: NpcClientBase does not define provider settings (LLM, STT, TTS). These are configured by derived classes. `SimpleNpcClient` exposes them as Inspector fields. Custom implementations can source them from ScriptableObjects, databases, or any other source.

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
| `OnSessionStarted` | `Action` | Fired when backend confirms session started |
| `OnAudioStarted` | `Action` | Fired when audio playback begins |
| `OnAudioStopped` | `Action` | Fired when audio playback ends |
| `OnResponseReceived` | `Action<LlmResponseData>` | Fired when AI response received (typed data with Text, Intents) |
| `OnNpcResponse` | `UnityEvent<string>` | Unity event fired with response text (assignable in Inspector) |
| `OnConversationStarted` | `Action` | Fired when conversation begins |
| `OnConversationEnded` | `Action` | Fired when conversation ends |

**Static Events** (for debug UI):

| Event | Type | Description |
|-------|------|-------------|
| `OnTranscriptionReceivedStatic` | `Action<string, string>` | (npcName, transcript) |
| `OnAIResponseReceivedStatic` | `Action<string, string>` | (npcName, response) |

---

### SimpleNpcClient

Self-contained NPC client with all AI provider settings configurable from the Inspector. Implements `INpcConfiguration` so it can be used directly with `RequestOrchestrator.StartAudioRequest()`.

**Namespace:** `Tsc.AIBridge.Core`

**Inheritance:** `NpcClientBase`

**Implements:** `INpcConfiguration`

#### Inspector Properties

**NPC Configuration:**

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `npcName` | `string` | `"NPC"` | NPC display name |
| `systemPrompt` | `string` | `""` | System prompt defining personality (TextArea) |

**LLM Settings:**

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `llmProvider` | `string` | `"openai"` | LLM provider: `openai`, `vertexai`, `azure-openai` |
| `llmModel` | `string` | `"gpt-4o-mini"` | Model ID: `gpt-4o`, `gpt-4o-mini`, `gemini-1.5-flash`, etc. |
| `temperature` | `float` | `0.7` | Response randomness (0.0 - 2.0) |
| `maxTokens` | `int` | `500` | Maximum response tokens |

**STT Settings:**

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `sttProvider` | `string` | `"google"` | STT provider: `google`, `azure`, `openai` |
| `language` | `string` | `"en-US"` | Language code for recognition: `en-US`, `nl-NL`, etc. |

**TTS Settings:**

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `voiceId` | `string` | `"default"` | ElevenLabs voice ID |
| `ttsModel` | `string` | `"eleven_turbo_v2_5"` | TTS model variant |
| `ttsStreamingMode` | `string` | `"batch"` | `batch` or `sentence` |
| `ttsSpeed` | `float` | `1.0` | Voice speed (0.7 - 1.2) |

**Interruption Settings:**

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `allowInterruption` | `bool` | `true` | Whether NPC can be interrupted |
| `persistenceTime` | `float` | `1.5` | Seconds to trigger interruption (0 - 5) |

> **Note**: `AudioSource` and `StreamingAudioPlayer` are auto-discovered from the same GameObject or its children. You do not need to assign them manually.

#### Additional Events (beyond NpcClientBase)

| Event | Type | Description |
|-------|------|-------------|
| `OnStartListening` | `Action` | Fired when NPC starts listening (call `NotifyStartListening()`) |
| `OnStopListening` | `Action` | Fired when NPC stops listening (call `NotifyStopListening()`) |
| `OnStartSpeaking` | `Action` | Fired when audio playback begins (automatic) |
| `OnStopSpeaking` | `Action` | Fired when audio playback ends (automatic) |

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
    public string TtsLanguageCode { get; set; } // Optional: Force TTS language (e.g., "nl", "en")

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
    public string TtsLanguageCode;  // Optional: ISO 639-1 code to force TTS pronunciation (e.g., "nl", "en")
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

### Using SimpleNpcClient (Recommended for Getting Started)

The easiest way to set up an NPC is with `SimpleNpcClient`. Configure everything in the Inspector - no code needed for basic usage.

```csharp
// Start a conversation using SimpleNpcClient as INpcConfiguration
var orchestrator = RequestOrchestrator.Instance;

// SimpleNpcClient implements INpcConfiguration, so pass it directly
orchestrator.StartAudioRequest(mySimpleNpcClient);
```

### Using ConversationRequest (Programmatic Configuration)

For dynamic configuration (e.g., changing providers at runtime):

```csharp
var request = new ConversationRequest
{
    NpcId = myNpc.NpcId,
    LlmProvider = "vertexai",
    LlmModel = "gemini-1.5-flash",
    SttProvider = "google",
    Language = "nl-NL",
    TtsModel = "eleven_turbo_v2_5",
    VoiceId = "your-elevenlabs-voice-id",
    Temperature = 0.7f,
    MaxTokens = 500,
    Messages = new List<ChatMessage>
    {
        new ChatMessage { Role = "system", Content = "You are a helpful assistant." }
    }
};

orchestrator.StartConversationRequest(request);
```

### Custom NPC Implementation

For advanced use cases, extend `NpcClientBase` and implement `INpcConfiguration`:

```csharp
public class CustomNpc : NpcClientBase, INpcConfiguration
{
    [SerializeField] private NpcConfigSO config;

    public override string NpcName => config.name;

    // INpcConfiguration implementation
    string INpcConfiguration.Id => NpcId;
    string INpcConfiguration.Name => config.name;
    string INpcConfiguration.SystemPrompt => config.systemPrompt;
    List<ChatMessage> INpcConfiguration.Messages => null;
    string INpcConfiguration.VoiceId => config.voiceId;
    string INpcConfiguration.TtsModel => "eleven_turbo_v2_5";
    string INpcConfiguration.TtsStreamingMode => "batch";
    float INpcConfiguration.TtsSpeed => 1.0f;
    string INpcConfiguration.LlmProvider => "openai";
    string INpcConfiguration.LlmModel => "gpt-4o";
    float INpcConfiguration.Temperature => config.temperature;
    int INpcConfiguration.MaxTokens => 1000;
    string INpcConfiguration.SttProvider => "google";
    string INpcConfiguration.Language => "en-US";
    bool INpcConfiguration.AllowInterruption => true;
    float INpcConfiguration.InterruptionPersistenceTime => 1.5f;
    bool INpcConfiguration.IsActive => IsActive;
    bool INpcConfiguration.IsTalking => IsTalking;

    public event Action OnStartListening;
    public event Action OnStopListening;
    public event Action OnStartSpeaking;
    public event Action OnStopSpeaking;

    private List<ChatMessage> _history = new();

    public override List<ChatMessage> GetApiHistoryAsChatMessages() => _history;

    public override void AddPlayerMessage(string message)
    {
        _history.Add(new ChatMessage { Role = "user", Content = message });
    }

    public override void ClearHistory() => _history.Clear();

    protected override void ValidateConfiguration()
    {
        if (config == null) Debug.LogError($"[{NpcName}] Config ScriptableObject not assigned!");
    }
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
