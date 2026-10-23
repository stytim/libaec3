# AEC3
AEC3 Extracted From WebRTC

This is a build adaptation for macOS Apple Silicon platform of the original project: [ewan-xu/AEC3](https://github.com/ewan-xu/AEC3)

## Build

### Windows
- Visual Studio 2015

### macOS (Apple Silicon/ARM)
#### Prerequisites
1. Install Homebrew (if not already installed):
   ```bash
   /bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"
   ```

2. Install required packages:
   ```bash
   brew install cmake
   ```

#### Build Steps
1. Clone the repository:
   ```bash
   git clone https://github.com/EitanWong/AEC3.git
   cd AEC3
   ```

2. Build using the provided script:
   ```bash
   cd demo
   chmod +x build.sh
   ./build.sh
   ```

   Or manually:
   ```bash
   cd demo
   mkdir -p build
   cd build
   cmake ..
   make -j$(sysctl -n hw.ncpu)
   ```

   arm64 library only (macOS 11+, exports only the `aec3_*` C API):
   ```bash
   ./build_macos_arm64.sh   # -> install/macos-arm64/lib/aec3.dylib
   ```

## Streaming API (real time)
For live audio, feed the far end and the microphone separately instead of `aec3_process_frame`:

```c
aec3_handle_t* h = aec3_create_stream(16000, 24000); // capture rate, render rate
aec3_analyze_render(h, speaker_frame, 240);          // each 10 ms of audio sent to the speakers
aec3_process_capture(h, mic_frame, 160);             // each 10 ms of microphone audio, in place
aec3_destroy(h);
```

- Mono 10 ms frames of floats in [-1, 1]: `rate / 100` samples per call; anything else returns -1.
- Capture runs at 16, 32 or 48 kHz; render may be any multiple of 100 Hz in [8000, 192000] and is resampled internally.
- No delay parameter: AEC3 finds the echo delay itself, up to about 0.5 s.
- Render and capture may be called from different threads (one thread per side). See `api/aec3_api.h`.

## Usage
The demo application processes audio files for acoustic echo cancellation:

```bash
./demo ref.wav rec.wav out.wav
```

Where:
- `ref.wav`: Reference/far-end signal (e.g., audio played from speakers)
- `rec.wav`: Recorded/near-end signal (e.g., microphone recording with echo)
- `out.wav`: Output file where the processed audio (echo-cancelled) will be saved

## Test
See demo.cc for example usage.

## Note
The AEC3 (Acoustic Echo Cancellation) implementation is extracted from the WebRTC project and adapted to run as a standalone library. This repository contains build scripts specifically for macOS Apple Silicon platform.
