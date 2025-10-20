# OpusSharp Setup Guide

## Overview
OpusSharp is required for real-time audio encoding in the Unity VR Training application. It encodes microphone audio to Opus format for efficient streaming to the backend API.

## Installation

OpusSharp is automatically included as part of the TSC AI Bridge package - no additional setup required!

### Package Location
OpusSharp is located within the AI Bridge package:
- `Packages/com.simulationcrew.aibridge/Plugins/OpusSharp/OpusSharp.Core/` - Core C# library
- `Packages/com.simulationcrew.aibridge/Plugins/OpusSharp/OpusSharp.Natives/` - Native Opus libraries for different platforms

When you install the AI Bridge package, OpusSharp is automatically available and ready to use.

## Platform-Specific Files

### Windows (x64)
- DLL: `OpusSharp.Natives/runtimes/win-x64/native/opus.dll`

### Windows (x86)
- DLL: `OpusSharp.Natives/runtimes/win-x86/native/opus.dll`

### Android (ARM64)
- SO: `OpusSharp.Natives/runtimes/android-arm64/native/libopus.so`
- Required for Quest 2/3 deployment

### Linux (x64)
- SO: `OpusSharp.Natives/runtimes/linux-x64/native/opus.so`

## Troubleshooting

### No audio being sent to backend
- Check if OpusAudioEncoder is throwing warnings
- Verify microphone permissions are granted
- Check RecorderBase is capturing audio

### DllNotFoundException on specific platform
- Ensure the correct native library is present for your platform
- Check platform settings in Unity for the native libraries

## Package Migration

OpusSharp is now part of the TSC AI Bridge package and will be automatically included when you install the package. No manual setup required!

## Version Info
- OpusSharp.Core: 1.5.2.1
- OpusSharp.Natives: 1.5.2.1
- Opus codec version: 1.3.1