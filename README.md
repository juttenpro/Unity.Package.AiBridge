# AI Bridge Core Package

Real-time AI conversation system for Unity with WebSocket-based communication, audio streaming, and multi-provider support.

## What is AI Bridge?

AI Bridge enables natural voice conversations between users and AI-powered NPCs in Unity. It handles the complete audio pipeline:

```
User speaks → Microphone → Opus encoding → WebSocket → Backend
                                                          ↓
                                                    STT → LLM → TTS
                                                          ↓
User hears ← AudioSource ← Opus decoding ← WebSocket ← Backend
```

**Key Capabilities:**
- Real-time voice-to-voice conversations (sub-second latency)
- Multiple AI providers: OpenAI, Vertex AI, Azure OpenAI
- Multiple TTS providers: ElevenLabs (Turbo, Flash, Multilingual)
- Multiple STT providers: Google Cloud Speech, Azure, OpenAI
- VR-ready with spatial audio support
- Cross-platform: Windows, macOS, Linux, Android, iOS, VR headsets

## Quick Start

### 1. Install Dependencies

Add to your `Packages/manifest.json`:
```json
{
  "dependencies": {
    "com.endel.nativewebsocket": "https://github.com/endel/NativeWebSocket.git#upm",
    "com.simulationcrew.aibridge": "https://github.com/juttenpro/Unity.Package.AiBridge.git"
  }
}
```

### 2. Configure Audio (Critical!)

Set Unity's audio sample rate to 48kHz:
1. **Edit → Project Settings → Audio**
2. Set **System Sample Rate** to **48000**

### 3. Set Up Scene

Add these components to your scene:

```
Scene Hierarchy:
├── AIBridgeManager (Empty GameObject)
│   ├── WebSocketClient
│   ├── RequestOrchestrator
│   ├── SpeechInputHandler
│   └── EnvironmentApiKeyProvider
│
└── NPC (Your character)
    ├── AudioSource
    ├── StreamingAudioPlayer
    └── SimpleNpcClient (configure LLM/STT/TTS in Inspector)
```

### 4. Configure API Key

The API key authenticates your client with the backend service. You receive this key from the backend provider.

Set the environment variable before running Unity:
```bash
# Windows PowerShell
$env:ORCHESTRATOR_API_KEY = "your-api-key-here"

# Linux/Mac
export ORCHESTRATOR_API_KEY="your-api-key-here"
```

### 5. Start a Conversation

```csharp
using Tsc.AIBridge.Core;

public class ConversationStarter : MonoBehaviour
{
    [SerializeField] private SimpleNpcClient npc;

    public void StartConversation()
    {
        var orchestrator = RequestOrchestrator.Instance;
        orchestrator.StartAudioRequest(npc.NpcId);
    }

    public void StopRecording()
    {
        RequestOrchestrator.Instance.EndAudioRequest();
    }
}
```

## Documentation

| Document | Description |
|----------|-------------|
| [Getting Started](Documentation~/GettingStarted.md) | Complete setup guide with step-by-step instructions |
| [Best Practices](Documentation~/BestPractices.md) | Patterns, anti-patterns, and production tips |
| [Examples](Documentation~/Examples.md) | Real-world implementation examples |
| [API Reference](Documentation~/API-Reference.md) | Complete API documentation |
| [API Key Providers](Runtime/Auth/README_API_KEY_PROVIDERS.md) | Authentication configuration |
| [Recording Architecture](Runtime/Input/RECORDING_ARCHITECTURE.md) | Recording system design |

## Core Concepts

### The Conversation Flow

1. **User presses PTT** (Push-to-Talk) or VAD detects speech
2. **SpeechInputHandler** captures microphone audio
3. **RequestOrchestrator** creates a session and buffers audio
4. Audio is **Opus-encoded** and sent via WebSocket
5. Backend processes: **STT → LLM → TTS**
6. **Audio streams back** as Opus/OGG chunks
7. **NpcClient** decodes and plays through AudioSource
8. Session completes, ready for next turn

### Key Components

| Component | Responsibility |
|-----------|----------------|
| `WebSocketClient` | Manages WebSocket connection, message routing |
| `RequestOrchestrator` | Coordinates conversation flow, session lifecycle |
| `SpeechInputHandler` | Microphone capture, PTT/VAD control |
| `NpcClientBase` | NPC-side message handling, audio playback |
| `StreamingAudioPlayer` | Real-time audio playback with buffering |

### Extension Points

Customize behavior by implementing these interfaces:

```csharp
// Custom NPC configuration
public interface INpcConfiguration
{
    string Id { get; }
    string SystemPrompt { get; }
    string VoiceId { get; }
    // ... see API Reference for full interface
}

// Custom API key retrieval
public interface IApiKeyProvider
{
    string GetOrchestratorApiKey();
}

// Custom audio capture
public interface IAudioCaptureProvider
{
    void StartCapture();
    void StopCapture();
    event Action<float[]> OnAudioDataAvailable;
}
```

## Features

### Audio Pipeline
- Real-time microphone capture (16kHz mono)
- Opus encoding (~85% bandwidth reduction)
- Adaptive buffering for network conditions
- Streaming playback (48kHz stereo)
- Spatial audio support for VR

### Voice Activity Detection (VAD)
- `DynamicRangeVADProcessor`: Adaptive threshold, auto-calibrating
- `SimpleVADProcessor`: Fixed threshold for consistent environments
- Smart offset: VAD-based silence detection after PTT release

### Session Management
- Request queuing prevents concurrent conflicts
- Audio buffering during WebSocket reconnection
- Pause/resume handling (VR headset, game pause)
- Interruption detection (user speaks while NPC talking)

### Recording Modes
- **Push-to-Talk (PTT)**: Manual control via button
- **Voice Activation**: Automatic start/stop via VAD
- **Smart Offset**: PTT + VAD silence detection
- **Fixed Delay**: PTT + configurable timeout

## Requirements

### Unity Version
- Unity 6 (6000.x) or Unity 2022.3 LTS

### Dependencies
- **NativeWebSocket** (required): High-performance WebSocket
- **Opus codec** (included): Audio compression

### Platform Support
- Windows (x64, x86)
- macOS (Intel, Apple Silicon)
- Linux (x64)
- Android (ARM64)
- iOS
- VR: Meta Quest, Pico, SteamVR

### Backend
AI Bridge requires a compatible backend service. The backend handles:
- Speech-to-Text (STT)
- Language Model (LLM) inference
- Text-to-Speech (TTS)
- WebSocket message routing

## Troubleshooting

### Audio plays at wrong speed
**Cause**: Unity sample rate not set to 48kHz
**Fix**: Edit → Project Settings → Audio → System Sample Rate = 48000

### No audio output
**Cause**: AudioSource not configured correctly
**Fix**: Ensure AudioSource has Spatial Blend set appropriately for your use case

### WebSocket connection fails
**Cause**: API key not configured
**Fix**: Set ORCHESTRATOR_API_KEY environment variable

### macOS: "DllNotFoundException: opus"
**Cause**: Native library not installed
**Fix**: Run `Tools → OpusSharp → Setup macOS Library (Homebrew)`

See [Troubleshooting Guide](Documentation~/GettingStarted.md#troubleshooting) for more solutions.

## Version History

See [CHANGELOG.md](CHANGELOG.md) for detailed release notes.

**Current Version**: 1.5.0

## License

MIT License - See LICENSE.md for details

## Support

- Repository: https://github.com/juttenpro/Unity.Package.AiBridge
- Issues: https://github.com/juttenpro/Unity.Package.AiBridge/issues
