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
    "com.simulationcrew.aibridge": "https://github.com/juttenpro/Unity.Package.AiBridge.git"
  }
}
```

#### Via Git URL

In Unity Package Manager, add package from git URL:
```
https://github.com/juttenpro/Unity.Package.AiBridge.git
```

**Note**: If NativeWebSocket is not installed, Unity will show a missing dependency error.

### Step 3: Platform-Specific Setup

#### macOS Setup (Required for Mac Users)

The Opus audio codec requires a native library (`libopus.dylib`) that is not included in the package due to licensing and build requirements. Mac users must install this library locally.

**Quick Setup (Recommended)**

In Unity on your Mac, use the built-in menu:

1. Open Unity
2. Go to **Tools → OpusSharp → Setup macOS Libraries (Homebrew)**
3. If Homebrew opus is not installed, Unity will prompt to install it automatically
4. The library will be copied to the correct location

**Manual Setup**

If the menu option doesn't work, install manually via Terminal:

```bash
# 1. Install opus via Homebrew (if not already installed)
brew install opus

# 2. Find and copy the library
# For Apple Silicon (M1/M2/M3/M4):
cp /opt/homebrew/lib/libopus.dylib ~/Library/PackageCache/com.simulationcrew.aibridge@*/Plugins/OpusSharp/OpusSharp.Natives/runtimes/osx-arm64/native/

# For Intel Mac:
cp /usr/local/lib/libopus.dylib ~/Library/PackageCache/com.simulationcrew.aibridge@*/Plugins/OpusSharp/OpusSharp.Natives/runtimes/osx-x64/native/

# 3. Restart Unity or reimport the package
```

**Troubleshooting macOS**

If you see this error on startup:
```
[OpusAudioEncoder] Failed to initialize: opus assembly:<unknown assembly> type:<unknown type> member:(null)
```

This means the native library is missing. Follow the setup steps above.

**Contributing macOS Libraries**

If you'd like to contribute pre-built macOS libraries to the package:

```bash
# On your Mac, after installing via Homebrew:
# 1. Clone the AIBridge repository
git clone https://github.com/juttenpro/Unity.Package.AiBridge.git
cd Unity.Package.AiBridge

# 2. Copy your architecture's library
# Apple Silicon:
cp /opt/homebrew/lib/libopus.dylib Plugins/OpusSharp/OpusSharp.Natives/runtimes/osx-arm64/native/

# Intel:
cp /usr/local/lib/libopus.dylib Plugins/OpusSharp/OpusSharp.Natives/runtimes/osx-x64/native/

# 3. Verify the architecture
file Plugins/OpusSharp/OpusSharp.Natives/runtimes/osx-arm64/native/libopus.dylib
# Should show: Mach-O 64-bit dynamically linked shared library arm64

# 4. Commit and create a PR
git add .
git commit -m "feat: add macOS ARM64 native opus library"
git push
```

#### Windows and Linux

No additional setup required. Native libraries are included in the package.

## Audio Configuration

### ⚠️ CRITICAL: Audio Sample Rate Setup

**AI Bridge requires Unity's audio system to run at 48000 Hz** to match the TTS audio stream format (Opus 48kHz).

If your Unity project uses a different sample rate (e.g., auto-selected 24kHz or 44.1kHz), **TTS playback will be at incorrect speed**.

#### Symptoms of Incorrect Sample Rate
- 🐌 **Slow, robotic voice** (if system < 48kHz)
- 🏃 **Fast, chipmunk voice** (if system > 48kHz)
- ⚡ **Playback speed = (48000 / systemSampleRate)x**

Example: 24kHz system → 48000/24000 = 2x slower playback

#### Solution 1: Project Settings (Recommended)

Set the sample rate in Unity's audio settings:

1. Open **Edit → Project Settings → Audio**
2. Set **System Sample Rate** to **48000**
3. Rebuild your project

Or edit `ProjectSettings/AudioManager.asset` directly:
```yaml
AudioManager:
  m_SampleRate: 48000  # Set to 48000 (not 0 for auto)
```

#### Solution 2: Runtime Configuration

Configure sample rate at application startup:

```csharp
using UnityEngine;

public class AudioInitializer : MonoBehaviour
{
    private void Awake()
    {
        const int REQUIRED_SAMPLE_RATE = 48000;

        if (AudioSettings.outputSampleRate != REQUIRED_SAMPLE_RATE)
        {
            var config = AudioSettings.GetConfiguration();
            config.sampleRate = REQUIRED_SAMPLE_RATE;
            bool success = AudioSettings.Reset(config);

            if (success)
            {
                Debug.Log($"Audio sample rate set to {AudioSettings.outputSampleRate}Hz");
            }
            else
            {
                Debug.LogError("Failed to set audio sample rate!");
            }
        }
    }
}
```

#### Automatic Detection

AI Bridge automatically detects sample rate mismatches and logs a **warning** with the playback speed ratio:

```
[AIBridge] AudioSettings.outputSampleRate is 24000Hz but TTS audio requires 48000Hz.
This will cause incorrect playback speed (audio will play 2.00x too fast/slow).
SOLUTION: Set Project Settings > Audio > System Sample Rate to 48000Hz...
```

**Always check Unity Console** for this warning during development.

#### Impact on Other Audio

Setting Unity to 48kHz affects **all audio in your project**:
- ✅ **48kHz is industry standard** (video, broadcasting, pro audio)
- ✅ **Unity automatically resamples** other audio files (44.1kHz music, etc.)
- ✅ **Minimal quality loss** from resampling (generally inaudible)
- ✅ **All modern hardware supports 48kHz** (desktop, mobile, VR)

**Recommendation**: Use 48kHz for your entire project for consistency.

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
using Tsc.AIBridge.Audio.Capture;

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
using Tsc.AIBridge.Messages;

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
using Tsc.AIBridge.Audio.VAD;

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

### Pause Handling

AI Bridge automatically handles Unity's `OnApplicationPause()`, but you can also integrate with custom pause systems (e.g., in-game pause menus, training systems):

```csharp
using Tsc.AIBridge.Core;
using UnityEngine;

public class CustomPauseManager : MonoBehaviour
{
    private RequestOrchestrator orchestrator;

    void Start()
    {
        orchestrator = RequestOrchestrator.Instance;
    }

    public void PauseGame()
    {
        // Forward pause state to AIBridge
        orchestrator.HandlePauseStateChange(isPaused: true, source: "GamePause");

        // Pausing will:
        // - Stop any active audio recording
        // - Reset request state to prevent orphaned sessions
        // - Clear audio buffers to prevent stale audio

        Time.timeScale = 0f;
    }

    public void ResumeGame()
    {
        // Forward resume state to AIBridge
        orchestrator.HandlePauseStateChange(isPaused: false, source: "GameResume");

        // After resume:
        // - User needs to press PTT again to start new recording
        // - This prevents resuming half-recorded audio (safe approach)

        Time.timeScale = 1f;
    }
}
```

**Why this matters:**
- Prevents recording state desync during pause
- Cleans up orphaned sessions when pause occurs mid-recording
- Ensures safe state when resuming from pause

**Note:** The `source` parameter is optional and used for logging to help debug pause-related issues.

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

- Unity 6 (6000.x) or Unity 2022.3 LTS or higher
- Supports all Unity platforms with microphone access
- VR Ready (Meta Quest, Pico, etc.)
- Desktop (Windows, macOS, Linux)
- Mobile (Android, iOS)

## License

MIT License - See LICENSE.md for details

## Support

- Repository: https://github.com/juttenpro/Unity.Package.AiBridge
- Issues: https://github.com/juttenpro/Unity.Package.AiBridge/issues