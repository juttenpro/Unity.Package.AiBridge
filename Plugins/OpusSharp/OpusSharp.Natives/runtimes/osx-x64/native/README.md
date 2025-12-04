# macOS x64 (Intel) Opus Native Library

Place `libopus.dylib` for Intel Macs (x86_64) in this directory.

## How to obtain libopus.dylib

### Option 1: Install via Homebrew (Recommended)

```bash
# Install opus
brew install opus

# Find the library
ls -la $(brew --prefix opus)/lib/

# Copy the dylib (adjust version if needed)
cp $(brew --prefix opus)/lib/libopus.dylib ./libopus.dylib

# Verify architecture (should show x86_64)
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
./configure --host=x86_64-apple-darwin
make
cp .libs/libopus.0.dylib ../libopus.dylib
```

## Verification

After placing the file, verify it's the correct architecture:

```bash
file libopus.dylib
# Should output: libopus.dylib: Mach-O 64-bit dynamically linked shared library x86_64
```
