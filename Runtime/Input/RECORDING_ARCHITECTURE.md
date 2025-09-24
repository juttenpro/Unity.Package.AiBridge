# Recording Architecture

## Overview

The recording system provides a clean separation between public interface and internal complexity.

## Core Components

### 1. SpeechInputHandler (Public Interface)
**Purpose**: Simple interface for external components

**Events**:
- `OnUserStartedSpeaking` - User begins speaking (PTT or VAD)
- `OnUserStoppedSpeaking` - User stops speaking (after all delays)
- `OnAudioDataReceived` - Raw audio data for processing

**Key Point**: External components don't need to know HOW recording starts/stops (PTT vs VAD).

### 2. RecordingController (Internal Logic)
**Purpose**: Encapsulates all recording control logic

**Responsibilities**:
- PTT state management
- Voice activation detection
- Smart offset calculation
- Fixed delay management
- Silence detection

**Decision Matrix**:

| Mode | PTT State | Action | Who Controls Stop |
|------|-----------|--------|-------------------|
| Voice Activation | - | User speaks | VAD (silence timeout) |
| PTT | Pressed | Button press | Never (until release) |
| PTT | Released + Smart Offset | Button release | VAD (silence detection) |
| PTT | Released + Fixed Delay | Button release | Timer (fixed delay) |

### 3. VADManager
**Purpose**: Voice Activity Detection

**Used for**:
- Voice activation mode (detecting speech start/stop)
- Smart offset (detecting silence after PTT release)
- Interruption detection (checking if user is speaking)

## Key Principles

### Separation of Concerns
- **SpeechInputHandler**: WHAT (user is speaking or not)
- **RecordingController**: HOW (PTT, VAD, delays, offsets)
- **VADManager**: DETECTION (is there speech in audio)

### Critical Rules
1. **Never stop recording while PTT is pressed**
2. **VAD only controls recording when**:
   - Voice activation mode is enabled, OR
   - PTT is released AND smart offset is enabled
3. **External components only care about**: User speaking or not

## Benefits

1. **Simplicity**: External API is just "user speaking" events
2. **Flexibility**: Can change internal logic without affecting consumers
3. **Testability**: RecordingController can be unit tested
4. **Maintainability**: Clear separation of responsibilities

## Usage Example

```csharp
// External component just listens to simple events
speechInputHandler.OnUserStartedSpeaking += () => {
    // Start processing audio
    // Don't care if it's PTT or VAD
};

speechInputHandler.OnUserStoppedSpeaking += () => {
    // Stop processing audio
    // Don't care about delays or offsets
};
```

## Implementation Status

- ✅ RecordingController class created
- ✅ Critical safeguards implemented
- ✅ Clear separation of concerns
- ⚠️ Full refactoring would require significant changes
- ✅ Current implementation has safeguards to prevent issues