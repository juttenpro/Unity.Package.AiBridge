# macOS ARM64 (Apple Silicon) Opus Native Library

Place `libopus.dylib` for Apple Silicon Macs (M1/M2/M3/M4) in this directory.

## How to obtain libopus.dylib

### Option 1: Install via Homebrew (Recommended)

On Apple Silicon Macs, Homebrew is installed in `/opt/homebrew/`:

```bash
# Install opus
brew install opus

# Find the library
ls -la /opt/homebrew/lib/libopus*

# Copy the dylib
cp /opt/homebrew/lib/libopus.dylib ./libopus.dylib

# Verify architecture (should show arm64)
file libopus.dylib
```

### Option 2: Build from source

```bash
# Install dependencies
brew install autoconf automake libtool

# Clone and build
git clone https://github.com/xiph/opus.git
cd opus
./autogen.sh
./configure --host=aarch64-apple-darwin
make
cp .libs/libopus.0.dylib ../libopus.dylib
```

## Verification

After placing the file, verify it's the correct architecture:

```bash
file libopus.dylib
# Should output: libopus.dylib: Mach-O 64-bit dynamically linked shared library arm64
```

## Note for Universal Binary

If you have a universal binary (contains both x86_64 and arm64), you can use the same file for both architectures:

```bash
file libopus.dylib
# Universal binary output: libopus.dylib: Mach-O universal binary with 2 architectures: [x86_64:...] [arm64:...]
```
