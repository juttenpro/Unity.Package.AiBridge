# AI Bridge Core Package

Core AI conversation and audio streaming system for Unity, providing WebSocket-based communication with LLM services.

## Features

- 🎤 **Audio Capture**: Microphone input with AGC and gain control
- 🔊 **Audio Streaming**: Real-time Opus codec support with adaptive buffering
- 🌐 **WebSocket Communication**: Binary and text message handling
- 🎯 **VAD Processing**: Multiple Voice Activity Detection implementations
- 📦 **Minimal Dependencies**: Only requires NativeWebSocket and Unity modules
- 🔌 **Extensible**: Interface-based design for custom implementations

## Dependencies

### Required External Packages

**NativeWebSocket** (v1.1.4 or higher)
- High-performance WebSocket implementation for Unity
- GitHub: https://github.com/endel/NativeWebSocket
- Installation via Package Manager:
  ```
  https://github.com/endel/NativeWebSocket.git#upm
  ```

### Unity Modules
- `com.unity.modules.audio`: Audio system integration
- `com.unity.modules.jsonserialize`: JSON serialization
- `com.unity.modules.unitywebrequest`: HTTP requests

### Optional Dependencies
**Concentus** (Opus codec via NuGet for Unity)
- Required for audio encoding/decoding
- Pre-compiled DLLs included in package

## Installation

### Step 1: Install NativeWebSocket

**REQUIRED**: Install NativeWebSocket before installing AI Bridge.

In Unity Package Manager → Add package from git URL:
```
https://github.com/endel/NativeWebSocket.git#upm
```

### Step 2: Install AI Bridge

#### Via Unity Package Manager

Add to your `manifest.json`:
```json
{
  "dependencies": {
    "com.endel.nativewebsocket": "https://github.com/endel/NativeWebSocket.git#upm",
    "com.simulationcrew.aibridge": "1.0.0"
  }
}
```

#### Via Git URL

In Unity Package Manager, add package from git URL:
```
https://github.com/simulationcrew/aibridge.git
```

**Note**: If NativeWebSocket is not installed, Unity will show a missing dependency error.

## Core Components

### Audio Pipeline

#### Capture
- `IAudioCaptureProvider`: Interface for audio input
- `MicrophoneCapture`: Unity microphone implementation with PTT support

#### Codecs
- `OpusAudioEncoder`: PCM to Opus encoding
- `OpusStreamDecoder`: Opus/OGG stream decoding with queue-based processing
- `OggOpusParser`: OGG packet parsing for stream boundaries

#### Playback
- `StreamingAudioPlayer`: Unity audio playback with adaptive buffering
- `AdaptiveBufferManager`: Dynamic buffer sizing based on network conditions

#### VAD (Voice Activity Detection)
- `VADProcessorBase`: Base class for VAD implementations
- `DynamicRangeVADProcessor`: Adaptive threshold VAD
- `SimpleVADProcessor`: Basic threshold-based VAD

### WebSocket Communication

- `EnhancedWebSocket`: Robust binary/text message separation
- `IWebSocketConnection`: WebSocket interface for implementations
- Message types for conversation protocol

### Interfaces for Extension

The package provides interfaces for integration with external systems:

```csharp
// NPC configuration without implementation dependencies
public interface INpcPersona
{
    bool AllowInterruption { get; }
    float PersistenceTime { get; }
    string SystemPrompt { get; }
    string TtsVoice { get; }
    // ... more properties
}

// Service for finding NPCs in the scene
public interface INpcFinder
{
    INpcPersona FindActiveNpc();
    IEnumerable<INpcPersona> FindAllNpcs();
}

// Conversation history management
public interface IConversationHistoryProvider
{
    ChatMessage[] GetMessages();
    void AddUserMessage(string content, string metadata = null);
    void AddAssistantMessage(string content, string metadata = null);
}
```

## Basic Usage

### Audio Capture
```csharp
using SimulationCrew.AIBridge.Audio.Capture;

public class AudioCaptureExample : MonoBehaviour
{
    private MicrophoneCapture micCapture;

    void Start()
    {
        micCapture = GetComponent<MicrophoneCapture>();
        micCapture.OnAudioDataAvailable += ProcessAudio;
    }

    void ProcessAudio(float[] audioData)
    {
        // Process captured audio
    }

    public void StartRecording()
    {
        micCapture.StartCapture();
    }

    public void StopRecording()
    {
        micCapture.StopCapture();
    }
}
```

### WebSocket Messages
```csharp
using SimulationCrew.AIBridge.Messages;

// Create session start message
var sessionStart = new SessionStartMessage
{
    sessionId = Guid.NewGuid().ToString(),
    systemPrompt = "You are a helpful assistant",
    messages = conversationHistory,
    llmModel = "gpt-4o-mini",
    ttsVoice = "voice_id"
};

// Handle incoming messages
void OnMessageReceived(WebSocketMessage message)
{
    switch (message.type)
    {
        case WebSocketMessageTypes.Transcription:
            var transcription = message as TranscriptionMessage;
            Debug.Log($"User said: {transcription.text}");
            break;
        case WebSocketMessageTypes.AiResponse:
            var response = message as AiResponseMessage;
            Debug.Log($"AI response: {response.content}");
            break;
    }
}
```

### VAD Processing
```csharp
using SimulationCrew.AIBridge.Audio.VAD;

public class VADExample : MonoBehaviour
{
    private DynamicRangeVADProcessor vadProcessor;

    void Start()
    {
        vadProcessor = GetComponent<DynamicRangeVADProcessor>();
        vadProcessor.OnSpeechStart += () => Debug.Log("Speech started");
        vadProcessor.OnSpeechEnd += () => Debug.Log("Speech ended");
    }

    void ProcessAudioFrame(float[] samples)
    {
        vadProcessor.ProcessAudioFrame(samples);
        bool isSpeaking = vadProcessor.IsSpeaking;
    }
}
```

## Architecture

The package follows a clean, interface-based architecture:

```
com.simulationcrew.aibridge/
├── Runtime/
│   ├── Audio/
│   │   ├── Capture/       # Audio input interfaces and implementations
│   │   ├── Codecs/        # Opus/OGG encoding and decoding
│   │   ├── Playback/      # Audio output and buffering
│   │   ├── VAD/           # Voice Activity Detection
│   │   └── Feedback/      # Audio feedback detection
│   ├── WebSocket/         # WebSocket communication layer
│   ├── Messages/          # Protocol message definitions
│   ├── Core/              # Core interfaces and session management
│   │   └── Interfaces/    # Extension point interfaces
│   ├── Auth/              # Authentication interfaces
│   ├── Configuration/     # API endpoints and constants
│   ├── Data/              # Data structures
│   ├── Models/            # Model definitions
│   └── Attributes/        # Custom attributes
└── Samples~/
    └── BasicConversation/ # Example implementation
```

## Performance Considerations

- **Audio Processing**: Optimized for real-time with minimal latency
- **Memory Management**: Queue-based streaming to minimize allocations
- **Network Efficiency**: Opus compression reduces bandwidth by ~85%
- **Thread Safety**: Concurrent collections for cross-thread communication

## Extension Points

The package is designed to be extended through:

1. **Custom Audio Capture**: Implement `IAudioCaptureProvider`
2. **Custom VAD**: Extend `VADProcessorBase`
3. **Custom NPC Systems**: Implement `INpcPersona` and `INpcFinder`
4. **Custom History**: Implement `IConversationHistoryProvider`

## Compatibility

- Unity 2022.3 or higher
- Supports all Unity platforms with microphone access
- VR Ready (Quest, Pico, etc.)

## License

MIT License - See LICENSE.md for details

## Support

- Documentation: https://github.com/simulationcrew/aibridge/wiki
- Issues: https://github.com/simulationcrew/aibridge/issues
- Email: info@simulationcrew.com