# Changelog
All notable changes to the SimulationCrew AI Bridge Core package will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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