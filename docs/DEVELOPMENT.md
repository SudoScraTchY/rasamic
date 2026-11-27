# Development guide

This guide is for building and changing Rasa on a clean machine. For the current
project state and next priorities, start with [HANDOFF.md](../HANDOFF.md).

## Prerequisites

| Tool | Version / purpose |
|---|---|
| .NET SDK | 11.0.100, pinned by `global.json` |
| CMake + Ninja | CMake 3.22+; Ninja for native builds |
| Visual Studio / Build Tools | Windows native build: VS 2022 or 2026 C++ workload and Windows SDK |
| JDK | 17+ for Android; the helper script prefers Microsoft JDK 17 |
| Android SDK/NDK | Platform `android-37.0`, build-tools, CMake, NDK; native minimum API 26 |
| Xcode | Required on macOS for iOS and macOS native builds |
| PipeWire | Linux virtual-mic runtime: `pipewire-pulse`, `wireplumber`, `pw-loopback`, `pactl` |

The managed solution targets `net11.0`. The MAUI client is a separate project with
`net11.0-android` and `net11.0-ios` targets and is not part of `RasaMic.slnx`.

## Clone and initialize

```sh
git clone --recurse-submodules https://github.com/SudoScraTchY/rasamic.git
cd rasamic
```

The Windows driver is a real submodule pointing at the project fork:
<https://github.com/SudoScraTchY/rasamic-windows-driver>. Keep its commit pinned;
do not replace it with an embedded repository.

If submodules were skipped during cloning:

```sh
git submodule update --init --recursive
git submodule status
```

## Fetch RNNoise model weights

The model archive is intentionally not committed. The download script verifies
the SHA-256 recorded in `native/third_party/rnnoise/model_version`.

```sh
cd native/third_party/rnnoise
sh download_model.sh
cd ../../..
```

Generated model files and tarballs are expected local dirt in the RNNoise
submodule. Do not add them to the parent repository.

## Build the native pipeline

Build the native library before Audio or Integration tests. The managed P/Invoke
loader expects the platform library next to the test or server output.

### Windows x64

From a Developer PowerShell with the MSVC environment loaded:

```powershell
cmake -S native/rasamic-native -B native/rasamic-native/build-win-x64 -G Ninja -DCMAKE_BUILD_TYPE=Release
cmake --build native/rasamic-native/build-win-x64
```

`tools/scripts/build-native-win.sh` is a convenience wrapper for the current
maintainer machine. It and the Android batch scripts contain machine-specific
paths; prefer the portable CMake commands on a new workstation.

### Linux x64

```sh
sudo apt install libasound2-dev ninja-build
cmake -S native/rasamic-native -B native/rasamic-native/build-linux-x64 -G Ninja -DCMAKE_BUILD_TYPE=Release
cmake --build native/rasamic-native/build-linux-x64
```

Linux ARM64 uses the same commands with an ARM64 runner/toolchain.

### Android

The existing helper targets `M:\Android\Sdks` and NDK 26.1.10909125, falling back
to the first installed NDK:

```bat
tools\scripts\build-native-android.bat arm64-v8a
tools\scripts\build-native-android.bat x86_64
```

For another SDK location, invoke CMake directly with that NDK's
`build/cmake/android.toolchain.cmake`, `ANDROID_ABI`, and
`-DANDROID_PLATFORM=android-26`.

### iOS device arm64

Run on macOS with Xcode:

```sh
tools/scripts/build-native-ios.sh
```

The output is `native/rasamic-native/build-ios-arm64/librasamic_native.dylib`.
The MAUI project includes it only when that file exists.

## Build managed code

```sh
dotnet build RasaMic.slnx -c Release
```

Runtime Async is enabled centrally for desktop `net11.0` targets only:

```xml
<Features Condition="'$(TargetFramework)' == 'net11.0'">runtime-async=on</Features>
```

Do not enable it for Android/iOS until on-device validation is complete.

## Build the mobile clients

### Android

The native arm64-v8a and x86_64 libraries must exist first. Point the build at
the SDK explicitly when the default `%LOCALAPPDATA%\Android\Sdk` is incomplete:

```powershell
$env:ANDROID_HOME = 'M:\Android\Sdks'
$env:ANDROID_SDK_ROOT = 'M:\Android\Sdks'
$env:JAVA_HOME = 'C:\Program Files\Microsoft\jdk-17.0.20.101-hotspot'
dotnet build src\RasaMic.Client -f net11.0-android -c Release
```

The current Release build passes with zero warnings and zero errors when those
variables are set. `tools/scripts/build-client-android.bat` sets the JDK but not
the Android SDK variables.

### iOS

On macOS with Xcode 27.2, after building the native library:

```sh
dotnet workload install maui
dotnet build src/RasaMic.Client -f net11.0-ios -r ios-arm64 -c Release -p:EnableCodeSigning=false -p:NoDSymUtil=true -p:NoSymbolStrip=true
```

This is an unsigned compile. Signing, provisioning, device install, and TestFlight
require an Apple Developer account and a macOS host.

## Run locally

```sh
dotnet run --project src/RasaMic.Server.Cli -c Release -- --monitor
dotnet run --project tools/RasaMic.LoopbackClient -c Release -- --server 127.0.0.1 --tone --no-ns
```

Use `--no-ns` for synthetic tones; RNNoise treats a pure sine as non-speech.
For the GUI, pairing steps, transports, and backend setup, see
[OPERATIONS.md](OPERATIONS.md).

## Code conventions

- Keep nullable reference types enabled; warnings are errors.
- Package versions are centralized in `Directory.Packages.props`.
- Use xUnit v2 and the existing VSTest command shape; see [TESTING.md](TESTING.md).
- Preserve zero managed allocations on the audio hot path.
- Keep native callbacks non-blocking and backend writes single-writer.
- Treat `ValueTask` as a measured API choice, not a blanket replacement for `Task`.
- Preserve the scalar PCM contract for NaN, signed zero, infinity, slices, and
  SIMD tails; keep non-AVX2 fallbacks covered.
- Do not commit build output, model weights, certificates, pairing material, or
  `.tools/` artifacts.

## Useful skills for the next change

- `run-tests` — choose and execute the repository's test command.
- `microbenchmarking` — BenchmarkDotNet methodology and reporting.
- `analyzing-dotnet-performance` — async/allocation/hot-path review.
- `vectorization` — SIMD contract, tail, fallback, and benchmark checks.
- `maui` — mobile lifecycle, permissions, navigation, and platform seams.
- `dotnet-pinvoke` — native interop signatures and lifetime.
- `handoff` — compact session context for the next agent.
