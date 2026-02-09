# Getting Started with AI Bridge

This guide walks you through setting up AI Bridge in your Unity project, from installation to your first AI conversation.

## Table of Contents

1. [Prerequisites](#prerequisites)
2. [Installation](#installation)
3. [Project Configuration](#project-configuration)
4. [Scene Setup](#scene-setup)
5. [Basic Implementation](#basic-implementation)
6. [Testing Your Setup](#testing-your-setup)
7. [Next Steps](#next-steps)
8. [Troubleshooting](#troubleshooting)

## Prerequisites

Before you begin, ensure you have:

- **Unity 2022.3 LTS** or **Unity 6 (6000.x)** or later
- A compatible **backend service** (handles STT, LLM, TTS)
- An **API key** for the orchestrator service (see [API Key Setup](#configure-api-key))
- **Microphone access** on your target platform

### Backend Requirements

AI Bridge is a client-side package that connects to a backend orchestrator service over WebSocket. The backend handles the full AI pipeline:
- **STT** (Speech-to-Text): Transcribes user speech (Google, Azure, or OpenAI)
- **LLM** (Large Language Model): Generates NPC responses (OpenAI, Vertex AI, or Azure OpenAI)
- **TTS** (Text-to-Speech): Synthesizes voice audio (ElevenLabs)

The default backend is the **API Orchestrator** hosted on Google Cloud Run. Contact the backend provider to obtain your API key.

## Installation

### Step 1: Install NativeWebSocket (Required)

AI Bridge requires NativeWebSocket for WebSocket communication.

**Option A: Via Package Manager UI**
1. Open **Window → Package Manager**
2. Click **+** → **Add package from git URL**
3. Enter: `https://github.com/endel/NativeWebSocket.git#upm`
4. Click **Add**

**Option B: Via manifest.json**

Edit `Packages/manifest.json`:
```json
{
  "dependencies": {
    "com.endel.nativewebsocket": "https://github.com/endel/NativeWebSocket.git#upm"
  }
}
```

### Step 2: Install AI Bridge

**Option A: Via Package Manager UI**
1. Open **Window → Package Manager**
2. Click **+** → **Add package from git URL**
3. Enter: `https://github.com/juttenpro/Unity.Package.AiBridge.git`
4. Click **Add**

**Option B: Via manifest.json**

Add to `Packages/manifest.json`:
```json
{
  "dependencies": {
    "com.endel.nativewebsocket": "https://github.com/endel/NativeWebSocket.git#upm",
    "com.simulationcrew.aibridge": "https://github.com/juttenpro/Unity.Package.AiBridge.git"
  }
}
```

### Step 3: Verify Native Libraries

AI Bridge automatically installs Opus audio codec libraries. Verify installation:

1. Check that `Assets/Plugins/OpusSharp/` folder exists
2. It should contain platform-specific libraries:
   - `Windows/x86_64/opus.dll`
   - `Windows/x86/opus.dll`
   - `Linux/x86_64/opus.so`
   - `Android/ARM64/libopus.so`

If missing, use: **Tools → OpusSharp → Install Native Libraries to Project**

#### macOS Users

macOS requires manual library setup due to code signing:

1. Install Opus via Homebrew: `brew install opus`
2. Use: **Tools → OpusSharp → Setup macOS Library (Homebrew)**

Or manually:
```bash
# Apple Silicon (M1/M2/M3/M4)
mkdir -p Assets/Plugins/OpusSharp/macOS
cp /opt/homebrew/lib/libopus.dylib Assets/Plugins/OpusSharp/macOS/

# Intel Mac
mkdir -p Assets/Plugins/OpusSharp/macOS
cp /usr/local/lib/libopus.dylib Assets/Plugins/OpusSharp/macOS/
```

## Project Configuration

### Audio Sample Rate (Critical!)

AI Bridge requires Unity's audio system to run at **48000 Hz**.

1. Open **Edit → Project Settings → Audio**
2. Set **System Sample Rate** to **48000**
3. Save and restart Unity

**Why 48kHz?**
- TTS audio streams at 48kHz
- Mismatched rates cause playback speed issues:
  - 24kHz system = audio plays 2x slower
  - 44.1kHz system = audio plays ~9% slower

### Microphone Permissions

#### Android
Add to `Assets/Plugins/Android/AndroidManifest.xml`:
```xml
<uses-permission android:name="android.permission.RECORD_AUDIO"/>
```

#### iOS
Add to Player Settings → Other Settings:
- **Microphone Usage Description**: "Used for voice conversations with NPCs"

#### WebGL
Microphone access requires HTTPS and user gesture to grant permission.

## Scene Setup

### Minimal Scene Structure

Create the following GameObjects:

```
Scene
├── AIBridgeManager (Empty GameObject)
│   Components:
│   ├── WebSocketClient
│   ├── RequestOrchestrator
│   ├── SpeechInputHandler
│   ├── MicrophoneCapture
│   └── EnvironmentApiKeyProvider
│
└── NPC (Your character GameObject)
    Components:
    ├── SimpleNpcClient (or your custom NpcClientBase)
    ├── StreamingAudioPlayer
    └── AudioSource
```

### Step-by-Step Setup

#### 1. Create AIBridgeManager

1. Create empty GameObject: **GameObject → Create Empty**
2. Rename to "AIBridgeManager"
3. Add components (in order):
   - `EnvironmentApiKeyProvider`
   - `WebSocketClient`
   - `MicrophoneCapture`
   - `SpeechInputHandler`
   - `RequestOrchestrator`

#### 2. Configure WebSocketClient

In the Inspector:

| Field | Value |
|-------|-------|
| Api Base Url | Your backend URL (default: `https://api-orchestrator-service-104588943109.europe-west4.run.app`) |
| WebSocket Endpoint | `/api/websocket` (this is the correct endpoint for the API Orchestrator) |
| Establish Connection On Start | ✓ (checked) |
| Send Wake Up Call | ✓ (recommended - prevents Cloud Run cold start delays) |
| Api Key Provider Component | Drag the `EnvironmentApiKeyProvider` component |
| Enable Verbose Logging | ✓ (for development) |

#### 3. Configure SpeechInputHandler

| Field | Value |
|-------|-------|
| Audio Capture | Drag the `MicrophoneCapture` component |
| Recording Mode | Choose: PTT, VoiceActivation, or SmartOffset |
| PTT Key | Space (or your preferred key) |

#### 4. Configure RequestOrchestrator

| Field | Value |
|-------|-------|
| Speech Input Handler | Drag the `SpeechInputHandler` component |
| Enable Verbose Logging | ✓ (for development) |

#### 5. Create NPC GameObject

1. Create or select your NPC character
2. Add `AudioSource` component if not present
3. Configure AudioSource:
   - **Spatial Blend**: 1.0 for 3D audio (VR) or 0.0 for 2D
   - **Play On Awake**: ✗ (unchecked)
4. Add `StreamingAudioPlayer` component
5. Add `SimpleNpcClient` component

#### 6. Configure SimpleNpcClient

SimpleNpcClient exposes all AI provider settings directly in the Inspector, so you can configure the full conversation pipeline without writing code.

**NPC Configuration:**

| Field | Value | Description |
|-------|-------|-------------|
| Npc Name | "Assistant" | Display name for this NPC |
| System Prompt | Your NPC's personality | Multi-line text defining behavior (e.g., "You are a helpful receptionist...") |

**LLM Settings:**

| Field | Value | Description |
|-------|-------|-------------|
| Llm Provider | `openai` | Options: `openai`, `vertexai`, `azure-openai` |
| Llm Model | `gpt-4o-mini` | Model ID (e.g., `gpt-4o`, `gpt-4o-mini`, `gemini-1.5-flash`) |
| Temperature | 0.7 | Response randomness: 0 = deterministic, 2 = creative |
| Max Tokens | 500 | Maximum length of LLM response |

**STT Settings:**

| Field | Value | Description |
|-------|-------|-------------|
| Stt Provider | `google` | Options: `google`, `azure`, `openai` |
| Language | `en-US` | Language code for speech recognition (e.g., `nl-NL`, `en-US`) |

**TTS Settings:**

| Field | Value | Description |
|-------|-------|-------------|
| Voice Id | ElevenLabs voice ID | Get voice IDs from your ElevenLabs account |
| Tts Model | `eleven_turbo_v2_5` | Options: `eleven_turbo_v2_5` (fast), `eleven_flash_v2_5`, `eleven_multilingual_v2` |
| Tts Streaming Mode | `batch` | `batch` = wait for full response, `sentence` = stream per sentence |
| Tts Speed | 1.0 | Voice speed: 0.7 (slow) to 1.2 (fast) |

**Interruption Settings:**

| Field | Value | Description |
|-------|-------|-------------|
| Allow Interruption | ✓ | Whether the user can interrupt the NPC |
| Persistence Time | 1.5 | Seconds user must speak to trigger interruption |

> **Note**: The `AudioSource` and `StreamingAudioPlayer` components are auto-discovered on the same GameObject or its children. You don't need to drag them into SimpleNpcClient.

> **Tip**: SimpleNpcClient implements `INpcConfiguration`, so you can pass it directly to `RequestOrchestrator.StartAudioRequest()` without building a separate configuration object.

### Configure API Key

The `ORCHESTRATOR_API_KEY` is a shared secret that authenticates your client with the backend service. **You receive this key from the backend provider** - it is not self-generated.

The authentication flow is:
1. Client sends API key to `POST /api/auth/token` (via `X-API-Key` header)
2. Backend validates the key and returns a short-lived JWT token (1 hour)
3. Client uses the JWT token to authenticate WebSocket connections

AI Bridge handles steps 1-3 automatically. You only need to provide the API key.

Set the environment variable before running Unity:

**Windows PowerShell:**
```powershell
$env:ORCHESTRATOR_API_KEY = "your-api-key-here"
# Then start Unity from the same terminal
```

**Windows CMD:**
```cmd
set ORCHESTRATOR_API_KEY=your-api-key-here
```

**macOS/Linux:**
```bash
export ORCHESTRATOR_API_KEY="your-api-key-here"
```

**Unity Editor (persistent):**
Add `ORCHESTRATOR_API_KEY` to your system environment variables (Windows: System Properties → Environment Variables, macOS/Linux: `~/.bashrc` or `~/.zshrc`).

> **Custom key providers**: If you need to load the API key from a vault, database, or web service instead of an environment variable, implement `IApiKeyProvider` (sync) or `IAsyncApiKeyProvider` (async) and assign it to the WebSocketClient's `Api Key Provider Component` field. See [API Key Providers](../Runtime/Auth/README_API_KEY_PROVIDERS.md) for details.

## Basic Implementation

### Push-to-Talk Controller

```csharp
using Tsc.AIBridge.Core;
using Tsc.AIBridge.Input;
using UnityEngine;

public class PTTController : MonoBehaviour
{
    [SerializeField] private SimpleNpcClient targetNpc;
    [SerializeField] private KeyCode pttKey = KeyCode.Space;

    private RequestOrchestrator _orchestrator;
    private bool _isRecording;

    private void Start()
    {
        _orchestrator = RequestOrchestrator.Instance;

        // Subscribe to events
        _orchestrator.OnTranscriptionReceived += HandleTranscription;
        targetNpc.OnAiResponseReceived += HandleResponse;
    }

    private void Update()
    {
        // PTT key pressed
        if (Input.GetKeyDown(pttKey) && !_isRecording)
        {
            StartRecording();
        }

        // PTT key released
        if (Input.GetKeyUp(pttKey) && _isRecording)
        {
            StopRecording();
        }
    }

    private void StartRecording()
    {
        _isRecording = true;
        _orchestrator.StartAudioRequest(targetNpc.NpcId);
        Debug.Log("Recording started...");
    }

    private void StopRecording()
    {
        _isRecording = false;
        _orchestrator.EndAudioRequest();
        Debug.Log("Recording stopped, processing...");
    }

    private void HandleTranscription(string text)
    {
        Debug.Log($"You said: {text}");
    }

    private void HandleResponse(AiResponseMessage response)
    {
        Debug.Log($"NPC says: {response.content}");
    }

    private void OnDestroy()
    {
        if (_orchestrator != null)
        {
            _orchestrator.OnTranscriptionReceived -= HandleTranscription;
        }
    }
}
```

### UI Button Integration

```csharp
using Tsc.AIBridge.Core;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

public class TalkButton : MonoBehaviour, IPointerDownHandler, IPointerUpHandler
{
    [SerializeField] private SimpleNpcClient targetNpc;
    [SerializeField] private Image buttonImage;
    [SerializeField] private Color normalColor = Color.white;
    [SerializeField] private Color recordingColor = Color.red;

    private RequestOrchestrator _orchestrator;

    private void Start()
    {
        _orchestrator = RequestOrchestrator.Instance;
        buttonImage.color = normalColor;
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        _orchestrator.StartAudioRequest(targetNpc.NpcId);
        buttonImage.color = recordingColor;
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        _orchestrator.EndAudioRequest();
        buttonImage.color = normalColor;
    }
}
```

### VR Controller Input (Meta Quest)

```csharp
using Tsc.AIBridge.Core;
using UnityEngine;
using UnityEngine.XR;

public class VRTalkController : MonoBehaviour
{
    [SerializeField] private SimpleNpcClient targetNpc;
    [SerializeField] private XRNode controllerNode = XRNode.RightHand;

    private RequestOrchestrator _orchestrator;
    private InputDevice _controller;
    private bool _wasPressed;

    private void Start()
    {
        _orchestrator = RequestOrchestrator.Instance;
    }

    private void Update()
    {
        // Get controller
        if (!_controller.isValid)
        {
            _controller = InputDevices.GetDeviceAtXRNode(controllerNode);
            return;
        }

        // Check trigger button
        _controller.TryGetFeatureValue(CommonUsages.triggerButton, out bool isPressed);

        // Trigger pressed
        if (isPressed && !_wasPressed)
        {
            _orchestrator.StartAudioRequest(targetNpc.NpcId);
        }

        // Trigger released
        if (!isPressed && _wasPressed)
        {
            _orchestrator.EndAudioRequest();
        }

        _wasPressed = isPressed;
    }
}
```

## Testing Your Setup

### Verification Checklist

1. **Console Output**: Look for these log messages:
   ```
   [WebSocketClient] Connected to backend
   [RequestOrchestrator] Session started: <session-id>
   [MicrophoneCapture] Capture started
   ```

2. **Audio Capture**: Speak while recording, verify:
   ```
   [SpeechInputHandler] Audio data received: <sample-count> samples
   ```

3. **Transcription**: After releasing PTT:
   ```
   [RequestOrchestrator] Transcription received: "your spoken text"
   ```

4. **AI Response**: Shortly after:
   ```
   [NpcClient] AI response: "response text"
   [StreamingAudioPlayer] Playback started
   ```

### Common Test Scenarios

1. **Basic Conversation**
   - Press PTT, say "Hello, how are you?"
   - Release PTT
   - Wait for NPC response
   - Verify audio plays correctly

2. **Quick Responses**
   - Ask a simple question
   - Measure time from PTT release to first audio
   - Should be under 2 seconds for most setups

3. **Long Responses**
   - Ask for a detailed explanation
   - Verify audio plays continuously without gaps
   - Check for any audio artifacts

4. **Interruption**
   - Start speaking while NPC is responding
   - Verify NPC stops speaking (if interruption enabled)
   - Verify new recording starts cleanly

## Next Steps

Once basic setup is working:

1. **Read [Best Practices](BestPractices.md)** for production-ready patterns
2. **Explore [Examples](Examples.md)** for real-world implementations
3. **Review [API Reference](API-Reference.md)** for customization options
4. **Implement custom NPC** by extending `NpcClientBase`
5. **Add conversation history** for context-aware responses
6. **Configure voice settings** for personality tuning

## Troubleshooting

### Installation Issues

#### "Missing assembly reference: NativeWebSocket"
**Cause**: NativeWebSocket not installed
**Fix**: Install NativeWebSocket first (see Installation step 1)

#### "Multiple plugins with the same name 'opus'"
**Cause**: Duplicate Opus libraries
**Fix**: Delete `Assets/Plugins/OpusSharp/` and reinstall via **Tools → OpusSharp → Install Native Libraries**

### Connection Issues

#### "WebSocket connection failed"
**Causes**:
- Backend not running
- Incorrect URL
- API key not set
- Network/firewall issues

**Debug steps**:
1. Verify backend is accessible via browser/curl
2. Check WebSocketClient URL matches backend
3. Verify API key environment variable is set
4. Check Unity Console for specific error messages

#### "API key not configured"
**Cause**: Environment variable not set
**Fix**: Set ORCHESTRATOR_API_KEY before starting Unity

### Audio Issues

#### Audio plays at wrong speed
**Cause**: Unity sample rate not 48kHz
**Fix**: Edit → Project Settings → Audio → System Sample Rate = 48000

#### No audio output
**Causes**:
- AudioSource not configured
- StreamingAudioPlayer not connected
- AudioSource muted

**Fix**:
1. Verify AudioSource is enabled and not muted
2. Check StreamingAudioPlayer has AudioSource reference
3. Verify AudioListener exists in scene

#### Audio sounds distorted
**Causes**:
- Sample rate mismatch
- Spatial audio misconfiguration
- AudioSource settings

**Fix**:
1. Ensure 48kHz sample rate
2. For VR: Set AudioSource Spatial Blend to 1.0
3. For 2D: Set Spatial Blend to 0.0

### Recording Issues

#### "Microphone not found"
**Cause**: No microphone available or permission denied
**Fix**:
1. Check microphone is connected
2. Grant microphone permission to Unity/app
3. Select correct device in MicrophoneCapture

#### Recording doesn't start
**Causes**:
- SpeechInputHandler not configured
- PTT key conflict
- WebSocket not connected

**Fix**:
1. Verify SpeechInputHandler has AudioCapture reference
2. Check PTT key isn't used by other systems
3. Wait for WebSocket connection before recording

### VR-Specific Issues

#### Audio doesn't follow NPC position
**Cause**: AudioSource not configured for spatial audio
**Fix**: Set AudioSource Spatial Blend to 1.0

#### Audio continues during headset off
**Cause**: Pause handling not working
**Fix**: Verify RequestOrchestrator receives pause events

### Platform-Specific Issues

#### Android: No microphone access
**Fix**: Add RECORD_AUDIO permission to AndroidManifest.xml

#### iOS: App crashes on microphone access
**Fix**: Add Microphone Usage Description in Player Settings

#### macOS: "DllNotFoundException: opus"
**Fix**: Install Opus via Homebrew and run macOS setup tool

## Getting Help

If you're still stuck:

1. **Check Console**: Look for error messages with `[AIBridge]` prefix
2. **Enable Verbose Logging**: Set `enableVerboseLogging = true` on components
3. **Check CHANGELOG**: Recent fixes might address your issue
4. **Open Issue**: https://github.com/juttenpro/Unity.Package.AiBridge/issues
