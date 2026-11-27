# Testing guide

Rasa uses SDK-style xUnit v2 projects in VSTest mode. `global.json` does not opt
into native MTP, so use ordinary `dotnet test` syntax without a `--` bridge.

## Current baseline

Verified on 2026-11-27 with .NET SDK 11.0.100 on Windows, current working tree:

```sh
dotnet test RasaMic.slnx -c Release
```

**Passed: 184, Failed: 0, Skipped: 0.**

| Project | Tests | Native dependency |
|---|---:|---|
| `RasaMic.Protocol.Tests` | 24 | No |
| `RasaMic.Audio.Tests` | 51 | Yes |
| `RasaMic.Core.Tests` | 21 | No |
| `RasaMic.IntegrationTests` | 58 | Yes |
| `RasaMic.Server.App.Tests` | 30 | No; headless Avalonia/Skia |

Workflow syntax is checked with:

```sh
go run github.com/rhysd/actionlint/cmd/actionlint@latest -color
```

The Windows native job runs the GUI tests, and `native.yml` has a separate Android
APK job. Remote results below are tied to explicit commits; local green tests are
not a substitute for runner evidence.

An initial full build run had one timing-sensitive `StatsTests` packet-loss
failure; the isolated rerun, a full `--no-build` rerun, and the final full build
rerun all passed.

The local console evidence is `.tools/ci-cd-full-test.log` (ignored). A green
run is evidence only for the commit/working tree and environment that produced
it; record both when promoting a release.

## Before testing

Initialize submodules and build the native library as described in
[DEVELOPMENT.md](DEVELOPMENT.md). Audio and Integration tests load
`rasamic_native` through P/Invoke and will fail if the platform library is absent.

```sh
git submodule update --init --recursive
cd native/third_party/rnnoise && sh download_model.sh && cd ../../..
# Build the platform native library, then:
dotnet test RasaMic.slnx -c Release
```

## Focused commands

```sh
dotnet test tests/RasaMic.Protocol.Tests -c Release
dotnet test tests/RasaMic.Core.Tests -c Release
dotnet test tests/RasaMic.Audio.Tests -c Release
dotnet test tests/RasaMic.IntegrationTests -c Release
dotnet test tests/RasaMic.Server.App.Tests -c Release

# Latency, plain transport, reconnect flush, and GUI monitoring
dotnet test tests/RasaMic.IntegrationTests -c Release --filter "FullyQualifiedName~LoopbackTests|FullyQualifiedName~SenderPacingTests"
dotnet test tests/RasaMic.Core.Tests -c Release --filter "FullyQualifiedName~JitterBufferTests"
dotnet test tests/RasaMic.Audio.Tests -c Release --filter "FullyQualifiedName~PipelineTests|FullyQualifiedName~MonitorOutputTests"
dotnet test tests/RasaMic.Server.App.Tests -c Release

# Allocation gate used by CI
dotnet test tests/RasaMic.Audio.Tests -c Release --filter "FullyQualifiedName~HotPathAlloc"

# SIMD contract only
dotnet test tests/RasaMic.Audio.Tests -c Release --filter "FullyQualifiedName~PcmPostGainTests"
```

Use `--no-build` only when the intended assemblies are already built. A solution
test does not rebuild unrelated tool projects such as `RasaMic.Benchmarks`.

## Windows external-cable checks

```sh
rasamicd --list-outputs
rasamicd doctor
rasamicd --cable
```

Without VB-CABLE, `--cable` must fail before listening with the official install
link. With a user-installed cable, verify playback to **CABLE Input**, capture
from **CABLE Output**, mono/stereo sessions, stop/start, and endpoint selection
persistence. Driver installation and desktop UI interaction remain user actions;
use CLI/headless checks for automated validation.

## SIMD fallback matrix

Run these in separate processes; do not change process-wide JIT switches inside
a test. The production path is AVX2, with TensorPrimitives/scalar-compatible
fallbacks.

```sh
dotnet test tests/RasaMic.Audio.Tests -c Release --no-build --filter "FullyQualifiedName~PcmPostGainTests"
DOTNET_EnableAVX2=0 dotnet test tests/RasaMic.Audio.Tests -c Release --no-build --filter "FullyQualifiedName~PcmPostGainTests"
DOTNET_EnableHWIntrinsic=0 dotnet test tests/RasaMic.Audio.Tests -c Release --no-build --filter "FullyQualifiedName~PcmPostGainTests"
```

Recorded on 2026-11-26: **27/27** in each configuration, including NaN, signed
zero, infinities, non-zero-offset slices, and vector-tail boundaries.

## Performance gates

```sh
dotnet run --project tools/RasaMic.LatencyProbe -c Release -- 30 50
dotnet run --project tools/RasaMic.Benchmarks -c Release -- --job short --filter '*' --exporters json --artifacts perf-results
powershell -NoProfile -ExecutionPolicy Bypass -File tools/perf-gate.ps1 -ResultsDir perf-results
```

The CI latency gate is p95 < 50 ms. Hosted scheduling noise is handled by up to three
attempts with the same threshold; the run passes only when one attempt meets it.
Benchmark methodology, final Runtime Async /
ValueTask / AVX2 numbers, and reproduction commands are in
[perf.md](perf.md). A benchmark fails only when its mean minus two standard errors is
more than 10% above the committed runner baseline. Unknown performance runners produce an advisory candidate
baseline; a human must promote it under `docs/perf-baselines/`.

## Mobile build verification

Android Release, verified 2026-11-27 with the SDK explicitly selected:

```powershell
$env:ANDROID_HOME = 'M:\Android\Sdks'
$env:ANDROID_SDK_ROOT = 'M:\Android\Sdks'
$env:JAVA_HOME = 'C:\Program Files\Microsoft\jdk-17.0.20.101-hotspot'
dotnet build src/RasaMic.Client -f net11.0-android -c Release
```

Result: **Build succeeded, 0 warnings, 0 errors.** This proves compilation and
packaging, not on-device behavior. Run the ADB smoke flow after UI or lifecycle
changes. iOS requires a macOS/Xcode host; an unsigned Release compile is not a
device or TestFlight test.

## On-device checks

- Unlock the phone before headless intent tests; MAUI cannot resolve the page
  behind the keyguard.
- Keep a capture-active foreground session for representative performance runs.
  Doze-state managed-thread numbers are not production numbers.
- Use `--no-ns` for tone transport checks.
- For USB, remove stale `adb reverse` tunnels after testing; they can make mDNS
  and subnet discovery hairpin back to the PC.
- Record APK SHA, device model, Android version, server commit, transport, loss,
  jitter, dwell, write gap, monitor queue, output-device buffer, and capture-active state.
- For plain mode, restart the server with `--plain`, confirm the client reconnects,
  then repeat the same measurements. Default encrypted mode must remain compatible.

See [OPERATIONS.md](OPERATIONS.md) for smoke commands and known traps.

## CI snapshot

Remote state for code/workflow revision `abf3dea`, checked 2026-11-27:

| Workflow | State | Scope |
|---|---|---|
| `ci` | Passing | [Run 45946396112](https://github.com/SudoScraTchY/rasamic/actions/runs/45946396112): all five jobs, including PipeWire capture and the required HAL factory self-test |
| `native` | Passing | [Run 45946396054](https://github.com/SudoScraTchY/rasamic/actions/runs/45946396054): Linux x64/arm64, macOS arm64, iOS arm64, Android arm64/x86_64, and `android-app` |
| `ios` | Passing | [Run 45946396085](https://github.com/SudoScraTchY/rasamic/actions/runs/45946396085): unsigned device-arm64 Release build with Xcode 27.2 |
| `perf` | Passing | [Run 45946396059](https://github.com/SudoScraTchY/rasamic/actions/runs/45946396059): zero-allocation gate, latency p95 34.3 ms, benchmarks, and summary; the EPYC 9V74 runner produced an advisory candidate baseline |
| `driver` | Passing | [Run 45946452255](https://github.com/SudoScraTchY/rasamic/actions/runs/45946452255): unsigned x64/ARM64 packages; architectures inspected |
| `release` | Passing | [Run 45946455576](https://github.com/SudoScraTchY/rasamic/actions/runs/45946455576): all six server RID packages and both Windows installers |

Hosted HAL enumeration and ring-readable steps are advisory. A green `ci` run proves
the build and factory/property self-test, not that `coreaudiod` exposes a working
macOS microphone. Driver and release workflows are intentionally not global required
checks because they are path-filtered or manually/tag dispatched.

## Main branch protection

Verified through the GitHub API on 2026-11-27. `main` requires strict status checks
for these 14 contexts:

- `managed-tests`
- `macos-managed-tests`
- `windows-native-and-audio-tests`
- `linux-native-and-tests`
- `macos-hal-spike`
- `build (linux-x64)`
- `build (linux-arm64)`
- `build (osx-arm64)`
- `build (ios-arm64)`
- `build (android-arm64)`
- `build (android-x86_64)`
- `android-app`
- `perf-gates`
- `build` (iOS workflow)

The policy also requires one approving review, dismisses stale approvals, requires
conversation resolution, and blocks force-pushes and branch deletion. Administrator
enforcement is disabled so the solo maintainer can bypass the rules for emergency
maintenance; normal pushes and PRs remain protected.
