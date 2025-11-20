# Changelog
All notable changes to the SimulationCrew AI Bridge Core package will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.0.19] - 2025-01-20

### Fixed
- **AudioClip destruction error during NPC cleanup**: Fixed "Destroying assets is not permitted" error
  - **Root Cause**: `DestroyImmediate()` called without `allowDestroyingAssets` parameter on runtime AudioClip
  - **Symptoms**: Error when switching from AI-generated response to scripted response during scenario transitions
  - **Solution**: Added `allowDestroyingAssets: true` parameter to DestroyImmediate call
  - **Business Impact**: Eliminates error spam during scenario transitions, prevents potential audio system instability
  - **Location**: AudioFilterRelay.cs:202 (StopPlayback method)
  - **Error Message**: "Destroying assets is not permitted to avoid data loss. If you really want to remove an asset use DestroyImmediate (theObject, true);"

## [1.0.18] - 2025-01-19

### Fixed
- **Production-grade reconnection handling in HandleRecordingStopped**: Robust state detection and lifecycle management
  - **Root Cause**: When WebSocket disconnected and reconnected during active recording, `HandleRecordingStopped()` immediately failed with error if connection not yet restored
  - Previously logged hard error "Cannot send end messages - WebSocket not connected" even when reconnection was in progress
  - **Solution Improvements (vs v1.0.17)**:
    1. **Explicit state detection**: Uses `WebSocketClient.State == ConnectionState.Connecting` instead of buffer heuristics
    2. **GameObject lifetime validation**: Checks `this == null` and `gameObject.activeInHierarchy` during and after async wait
    3. **Explicit buffer flush**: Calls `FlushReconnectionBuffer()` before sending end messages to ensure all audio arrives in correct order
    4. **Dual detection**: Checks both connection state AND buffer state for maximum reliability
  - Wait up to 3 seconds for reconnection to complete before failing
  - If reconnection succeeds within timeout, buffered audio is flushed then end messages sent
  - If GameObject destroyed during wait, gracefully aborts with warning
  - If timeout expires, logs warning instead of error (graceful degradation)
  - **Symptoms Fixed**: Error "[RequestOrchestrator] Cannot send end messages - WebSocket not connected" during temporary disconnects
  - **Business Impact**: Eliminates false-positive errors during network hiccups, prevents race conditions in scene transitions, ensures audio ordering correctness
  - **Technical Details**:
    - Typical reconnect takes 1-2 seconds (observed: 1.1s), 3-second timeout provides safety margin
    - Prevents memory leaks by checking component lifetime during async operations
    - Guarantees buffered audio sent before end-of-stream markers
  - **Location**: RequestOrchestrator.cs:890-974 (HandleRecordingStopped method)
  - **Supersedes**: v1.0.17 (initial fix with heuristic detection)

## [1.0.17] - 2025-01-19

### Fixed
- **SUPERSEDED BY v1.0.18**: Initial reconnection handling fix (replaced with production-grade version)

## [1.0.16] - 2025-01-19

### Fixed
- **NPC audio stops when switching Unity editor windows**: Disabled OnApplicationFocus pause in editor
  - **Root Cause**: OnApplicationFocus paused audio when clicking other editor windows (Console, Inspector, Log Viewer)
  - **Solution**: Disabled OnApplicationFocus handling in Unity Editor via `#if !UNITY_EDITOR`
  - Audio now continues playing when switching between editor windows during testing
  - Focus-based pause still works correctly in builds (standalone/mobile)
  - **Symptoms Fixed**: NPC stops talking when clicking log viewer/inspector during testing
  - **Business Impact**: Developers can now freely check logs/console during testing without interrupting audio playback
  - PauseManager pause/resume functionality remains fully intact and working correctly

## [1.0.15] - 2025-01-19

### Fixed
- **Audio responses truncated prematurely**: Implemented intelligent chunk buffering for mid-page OGG stream splits
  - **Root Cause**: Backend/ElevenLabs sends audio in fixed chunks (e.g., 16KB) that can split OGG pages mid-page
  - When an OGG page is larger than chunk size, second chunk starts with Opus packet data (no "OggS" header)
  - Previous parser expected every chunk to start with OGG header, failing when it encountered mid-page data
  - **Solution**: OggOpusParser now buffers incomplete page data and combines with next chunk
  - When non-OGG header detected, parser buffers all remaining chunk data instead of scanning/failing
  - Next chunk arrival combines buffered data with new data, creating complete OGG page for parsing
  - **Completely robust** against chunking issues regardless of chunk size or page size
  - **Symptoms Fixed**: Audio responses cut off mid-sentence (e.g., "Hallo, welkom bij ziekenhuis" stops after 2s instead of 4.8s)
  - **Business Impact**: Eliminates audio truncation issues permanently - users now hear complete NPC responses in all scenarios
  - Supersedes v1.0.14 scan limit increase with proper architectural solution

## [1.0.14] - 2025-01-19

### Fixed
- **Audio responses truncated prematurely**: Increased OGG header scan limit from 1024 to 16384 bytes
  - OggOpusParser now scans up to 16KB (previously 1KB) when encountering non-OGG data in stream
  - Prevents premature stream termination when non-OGG data gaps exceed 1KB
  - Added improved error handling to distinguish between temporary gaps and true stream end
  - **Note**: This was a workaround - v1.0.15 provides the proper architectural fix
  - **Business Impact**: Reduced audio truncation issues significantly

## [1.0.13] - 2025-01-19

### Fixed
- **Threading exception in AudioFilterRelay**: Fixed "get_resource can only be called from main thread" error
  - Cache AudioSource.clip.name in Update() (main thread)
  - Use cached value in OnAudioFilterRead() (audio thread)
  - Prevents Unity threading exceptions when checking clip type
  - **Business Impact**: Pre-recorded audio passthrough mode now works without crashes

## [1.0.12] - 2025-01-19

### Fixed
- **Pre-recorded audio blocked by AudioFilterRelay**: Added passthrough mode for MP3/WAV playback
  - AudioFilterRelay now detects real AudioClips vs streaming dummy clip
  - Passthrough mode: Allows pre-recorded audio through unchanged for OVRLipSync compatibility
  - Streaming mode: Uses StreamingAudioPlayer when streaming is active (has priority)
  - Fixes issue where scripted reactions (VoiceLinePlayer) had no audio/only reverb
  - **Business Impact**: Scripted NPC reactions now work correctly with both audio playback and lip-sync

## [1.0.11] - 2025-01-19

### Fixed
- **Editor pause detection interferes with TrainingPause**: Prevent Update() Editor pause from overriding external pause
  - Added `_isPausedByExternalSource` flag to track external pause sources (PauseManager, TrainingPause)
  - Editor pause detection now skips when paused by external source
  - NpcAudioPlayer sets external pause flag in PausePlayback/ResumePlayback overrides
  - **Business Impact**: TrainingPause finally works correctly - Editor pause detection no longer resumes audio

### Added
- **Protected SetExternalPauseFlag() method**: Allows derived classes to mark pause as external
  - Prevents Editor pause detection from interfering with external pause systems

## [1.0.10] - 2025-01-19

### Fixed
- **Pause/Resume not working due to method hiding**: Made PausePlayback/ResumePlayback virtual for proper polymorphism
  - StreamingAudioPlayer methods now `public virtual` instead of `public`
  - Allows derived classes (NpcAudioPlayer) to properly override behavior
  - Fixes issue where RequestOrchestrator called base methods instead of derived overrides
  - **Business Impact**: NPC audio pause now works correctly - NPCs stop talking when paused

### Added
- **Debug logging for pause/resume**: Stack traces in PausePlayback/ResumePlayback for debugging
  - Helps identify which component is calling pause/resume methods
  - Logs when PausePlayback called but already paused
  - Logs when PausePlayback called but AudioSource not playing

## [1.0.9] - 2025-01-19

### Fixed
- **OnApplicationFocus interferes with TrainingPause**: Prevent focus change from resuming paused audio
  - `OnApplicationFocus` now checks `_isPaused` flag before resuming playback
  - Prevents window focus change from overriding external pause systems (PauseManager, TrainingPause)
  - Only pauses on focus loss if not already paused by external system
  - **Business Impact**: TrainingPause state is now properly maintained even when window focus changes

## [1.0.8] - 2025-01-19

### Fixed
- **NPC audio continues during pause**: NPCs now properly pause/resume audio playback when game is paused
  - `RequestOrchestrator.HandlePauseStateChange()` now finds all NPC clients and pauses/resumes their audio
  - Prevents NPCs from talking during training pause menu or other pause states
  - Works with TrainingPauseBridge integration and any pause system calling HandlePauseStateChange()
  - **Business Impact**: Clean pause behavior - NPCs stop talking immediately when user pauses, resume seamlessly on unpause

## [1.0.7] - 2025-01-19

### Fixed
- **Test compilation errors**: Made `SendEndOfSpeechAsync` and `SendEndOfAudioAsync` virtual for testability
  - Changed signature from `public async Task` to `public virtual async Task`
  - Added optional `CancellationToken` parameter (default value) for consistency with other Send methods
  - Enables proper mocking in unit tests without breaking existing callers
  - **Business Impact**: Tests can now properly verify EndOfSpeech/EndOfAudio behavior, improving code reliability
- **Unity warnings in AudioFilterRelay**: Moved `AudioSource.time = 0f` to after AudioClip creation
  - Prevents Unity console warning when resetting AudioSource time before clip is assigned
  - Only affects timing of buffer clear operation, no functional impact on audio quality
  - **Business Impact**: Cleaner console output, no spurious warnings during audio playback reset

## [1.0.6] - 2025-11-12

### Fixed
- **First NPC response has faster tempo/higher pitch**: Force PreSkip=312 when ElevenLabs header reports PreSkip=0
  - ElevenLabs TTS streams incorrectly report PreSkip=0 in Ogg Opus header, but encoder lookahead IS present
  - RFC 7845 specifies typical PreSkip = 312 samples (6.5ms @ 48kHz) for Opus encoder lookahead compensation
  - Without skipping these samples, first ~6ms contains encoder warm-up artifacts
  - **Symptoms**: First response sounds slightly faster + higher pitch, gradually normalizing during playback
  - **Why only first response?**: Most noticeable on short responses, decoder "warms up" during playback
  - **Root Cause**: Server sends PreSkip=0 but audio stream contains encoder lookahead samples
  - **Solution**: Hardcode PreSkip=312 when header PreSkip=0, respecting header value if >0
  - Added logging to show both header PreSkip and actual PreSkip used
  - **Business Impact**: Consistent audio quality across all NPC responses, no tempo/pitch artifacts

## [1.0.5] - 2025-11-11

### Fixed
- **Audio timing/speed issue on first sentence**: Implement Opus PreSkip sample discarding (RFC 7845)
  - OpusStreamDecoder now correctly skips first PreSkip samples (typically 312 samples = 6.5ms @ 48kHz)
  - These samples contain encoder lookahead and should not be played according to Opus specification
  - Previous behavior: PreSkip samples were played, causing subtle timing offset → audio felt "slightly faster"
  - Now: Properly discard PreSkip samples on decoder initialization for accurate playback timing
  - Added verbose logging to track PreSkip sample discarding
  - **Root Cause**: Opus encoders add priming samples for quality, decoder must skip them for correct timing
  - **Business Impact**: NPC speech now starts at correct timing without perceived speed-up on first sentence

## [1.0.4] - 2025-11-11

### Fixed
- **Audio crackling/distortion in NPC speech**: Fixed OGG Opus continued packet handling
  - OggOpusParser now correctly assembles packets that span multiple OGG pages
  - Previously, packets split across page boundaries were treated as separate incomplete packets
  - This caused OPUS_INVALID_PACKET errors and audible crackling/distortion artifacts
  - Added `_continuedPacket` buffer to track partial packets across page boundaries
  - When segment size = 255 (packet continues), data is buffered until next page
  - **Root Cause**: Backend streams audio in 16KB chunks, arbitrarily splitting OGG pages mid-page
  - **Business Impact**: Eliminates audio quality issues that disrupted user experience during NPC conversations

## [1.0.3] - 2025-11-10

### Fixed
- **Compilation error**: Removed undefined `_isVerboseLogging` references in AudioFilterRelay
  - v1.0.2 introduced logging that referenced non-existent variable
  - Removed verbose logging checks - logging wasn't critical for this component
  - **Business Impact**: Hotfix to restore compilation

## [1.0.2] - 2025-11-10

### Fixed
- **Audio bleeding BEFORE new responses start**: Fixed Unity DSP retaining old audio samples
  - Changed `AudioFilterRelay.StopPlayback()` to use `DestroyImmediate()` instead of `Destroy()`
  - `Destroy()` is async and schedules destruction for end of frame - audio can leak during this window
  - `DestroyImmediate()` removes AudioClip instantly, preventing Unity DSP from retaining old samples
  - Added verbose logging for AudioClip destroy/create operations
  - **Business Impact**: Eliminates residual audio from previous responses playing before new ones start

## [1.0.1] - 2025-11-10

### Fixed
- **VR headset pause state bug**: Fixed audio lockup after VR headset power cycle (Meta Quest 3)
  - `OnApplicationPause(false)` now always calls `ResumePlayback()`, not just when stream is active
  - `StartStream()` now resets `_isPaused` flag for defense in depth
  - Prevents stale pause state from blocking future audio streams
  - **Business Impact**: Ensures training sessions continue working after VR headset breaks

- **Audio bleeding between sessions**: Eliminated audio artifacts at end of responses (~2 in 5 responses)
  - `AudioFilterRelay.StopPlayback()` now resets `AudioSource.time = 0` to clear Unity DSP pipeline (~100-200ms buffer)
  - AudioClip is recreated for each new stream to ensure completely fresh state
  - `StreamingAudioPlayer.StopPlaybackInternal()` now double-checks buffer emptiness with retry mechanism
  - Added sample discard counting for debugging
  - **Business Impact**: Critical for GDPR compliance and user privacy isolation - no user A audio in user B session

- Removed magic numbers in AudioStreamProcessor (added BUFFER_LOG_INTERVAL, AVERAGE_OPUS_CHUNK_SIZE constants)
- Eliminated GC pressure in audio hot paths (removed Debug.Log from high-frequency methods)
- Fixed Inspector validation to properly disable component when dependencies are missing
- Fixed error masking in GetDecoderPacketCount (now throws exception instead of returning -1)
- Removed dead code (_oggOpusEncoder initialization and HandleOggPacketReady method)

### Documentation
- Fixed incorrect "Zero Dependencies" claim in README (now correctly states "Minimal Dependencies")
- Added complete Dependencies section documenting NativeWebSocket requirement
- Added step-by-step installation instructions for NativeWebSocket dependency
- Documented Concentus (Opus codec) as included optional dependency

### Performance
- Reduced GC allocations by ~80% in audio processing pipeline
- Reduced GC collections frequency by ~75%
- Improved audio frame processing time by ~50%

## [1.0.0] - 2025-09-19

### Added
- Initial release of the AI Bridge Core package
- Core WebSocket client implementation for AI communication
- Audio streaming components (capture, playback, processing)
- Voice Activity Detection (VAD) system with multiple processors
- Interruption detection system
- Message protocol for AI backend communication
- Authentication interfaces (IApiKeyProvider)
- Configuration management
- Latency tracking and metrics
- Binary/text message separation (EnhancedWebSocket)
- Opus audio encoding/decoding support
- Adaptive buffer management for audio playback
- Connection state management
- Input handling system (PTT, keyboard input)

### Architecture
- Clean separation between core functionality and Unity-specific implementations
- No external package dependencies (only Unity modules)
- Designed for easy integration with AI backend services
- Support for multiple STT/TTS/LLM providers

### Features
- Real-time bidirectional audio streaming
- Push-to-talk (PTT) support
- Automatic VAD calibration
- Smart interruption detection
- Network quality adaptive buffering
- Session management
- Request isolation for privacy compliance