# Rasa project handoff

**Snapshot:** 2026-11-27

**Repository:** <https://github.com/SudoScraTchY/rasamic>

**Driver fork:** <https://github.com/SudoScraTchY/rasamic-windows-driver>

**Baseline HEAD:** `27d03b0f38abb20a9405cd337c4889d34db01422`

**License:** MIT, Rasa Contributors

This is the maintainer entry point. It records what is implemented, what is
actually verified, what blocks a release, and what the next person should do.
It does not replace the architecture, development, testing, operations, or
release guides linked below.

## Executive summary

Rasa is an open-source WO Mic alternative: an Android/iOS phone captures audio,
runs native noise suppression/AGC, encodes Opus, and streams encrypted audio by
default to a Windows/macOS/Linux server. The PC can explicitly select plain audio
for a trusted LAN. The server exposes monitor output or a virtual
microphone backend.

The strongest verified path today is **Android -> Windows/Linux server -> monitor
or PipeWire**. The managed Release suite is green, Android Release builds cleanly,
and the performance workflow passes. The project is **not release-ready** because
the macOS HAL compile patch still needs a CI rerun, the Windows driver workflow has
never run, `main` has no branch protection, and there are no tags or releases.

The working tree also contains a completed Runtime Async / ValueTask / AVX2
performance pass, the latency/plain-mode pass, this documentation pass, and the
external-cable backend. They are **uncommitted**; remote `main` remains at `27d03b0`.

## Platform and feature status

| Area | Implemented | Verified | Gap / blocker |
|---|---|---|---|
| Windows server + GUI | Yes | 184/184 Release tests on Windows; GUI headless tests; server-controlled plain mode and independent PC-speaker monitoring; local Velopack install/uninstall round-trip | External-cable audio roundtrip still needs a user-installed cable |
| Windows virtual mic | External-cable backend plus IOCTL feed, kernel SPSC ring, user-mode driver backend | WASAPI endpoint enumeration and missing-cable failure verified; pinned fork matches remote `main` | `driver` workflow has zero runs; no Driver Verifier/install evidence |
| Linux server | Yes | CI native x64/arm64 and managed tests pass; PipeWire source capture passes | Keep distro/package smoke coverage current |
| macOS server | Managed front end + HAL writer/plugin | macOS managed CI job passes; HAL source patched for current SDK | macOS HAL rerun pending; `macos-14` native job cancelled |
| Android client | Wi-Fi, adb-reverse USB, foreground service, stereo Oboe, optional AudioRecord, discovery/reconnect | Native CI arm64/x86_64; Release build 0 warnings/errors; prior live Wi-Fi soak and capture-active bench; APK build job added | New Android app workflow awaits remote run; Bluetooth and Wi-Fi Direct need hardware verification |
| iOS client | AVAudioEngine mono capture, USB tunnel code, pairing/UI | Unsigned Release compile has passed; on-device work not verified here | `macos-14` CI cancelled; Apple account/macOS/device required |
| Bluetooth | Android client + PC RFCOMM listener | Listener startup on Windows | Phone-to-PC audio pending paired hardware |
| Multi-client | Single-session `Busy` behavior and forward-compatible `sessionId` | Integration tests | Mixing intentionally deferred; see [multi-client-eval.md](docs/multi-client-eval.md) |

## Evidence snapshot

### Local, current working tree — 2026-11-27

| Check | Result | Evidence |
|---|---|---|
| `dotnet test RasaMic.slnx -c Release` | **184 passed, 0 failed, 0 skipped** | `.tools/ci-cd-full-test.log`; all five test projects, .NET 11.0.100 Windows |
| `rasamicd --list-outputs` | Real WASAPI playback/capture IDs enumerated | Local Windows CLI run; VB-CABLE not installed |
| Android -> Windows monitor Wi-Fi smoke | Plain and encrypted modes both connected; final paced run ~100 pkt/s, 0% loss, ~10 ms RTT, 10 ms write interval, 20–70 ms dwell, zero drops, 22 ms device buffer; reconnect clean | `.tools/rasamic-v2-final.log`, `.tools/rasamic-v2-final.png` (ignored) |
| Android Release with `M:\Android\Sdks` + JDK 17 | **Build succeeded, 0 warnings, 0 errors** | `.tools/ci-cd-android-build.log` (ignored) |
| BenchmarkDotNet DefaultJob | **22 cases, 0 B allocated; no >10% regressions** | `.tools/perf-refresh/`; refreshed local and hosted-runner baselines under `docs/perf-baselines/` |
| Workflow syntax | **actionlint 1.7.12 passed** | `go run github.com/rhysd/actionlint/cmd/actionlint@latest -color` |
| SDK | .NET **11.0.100** | `global.json`, `dotnet --version` |
| Workloads | android, ios, maccatalyst, maui-android, maui-windows installed | `dotnet workload list` |
| Submodules | All six present; Windows driver pinned at `9b6e18b` | `git submodule status` |

The Android build needs `ANDROID_HOME` and `ANDROID_SDK_ROOT` set explicitly on
this machine. The default `%LOCALAPPDATA%\Android\Sdk` has only licenses and
platform-tools, while `M:\Android\Sdks` contains build-tools, platforms, CMake, and
NDK.

### Remote, commit `27d03b0` — checked 2026-11-27

| Workflow | State | Detail |
|---|---|---|
| `ci` | **Failing** | [Run 45775478409](https://github.com/SudoScraTchY/rasamic/actions/runs/45775478409): four jobs pass; `macos-hal-spike` fails at `Build HAL plugin` |
| `native` | **Cancelled** | [Run 45775478486](https://github.com/SudoScraTchY/rasamic/actions/runs/45775478486): Linux/Android native jobs pass; macOS jobs cancelled |
| `ios` | **Cancelled** | [Run 45775478430](https://github.com/SudoScraTchY/rasamic/actions/runs/45775478430): no steps executed |
| `perf` | **Passing** | [Run 45775478420](https://github.com/SudoScraTchY/rasamic/actions/runs/45775478420); hosted candidate baseline promoted in this tree |
| `driver` | **No runs** | Trigger now includes the gitlink, `.gitmodules`, and workflow edits |
| `release` | **No runs** | Workflow now creates a missing tag release before uploading artifacts |

`main` has no branch protection. There are no GitHub Releases. The local workflow,
HAL, benchmark, and documentation fixes are uncommitted, so none of these remote
results include them yet.

### Earlier device/package evidence

- 2026-11-18 Wi-Fi soak, fixed APK, 11 samples over 10 minutes: 11/11 zero-loss,
  ~100 pkt/s, jitter 0.8–2.2 ms, no phone/server memory growth.
- 2026-11-19 capture-active Redmi Note 11 bench: mono 4.4 ms / stereo 8.2 ms per
  10 ms frame. Earlier 21/42 ms numbers were doze-state artifacts.
- Linux PipeWire tone capture verified end-to-end; CI repeats the smoke.
- Velopack Windows GUI installer passed a local silent install/uninstall round-trip.
- iOS unsigned Release compile passed previously; the current remote iOS workflow
  is cancelled and no device signing/TestFlight evidence exists.

Details: [TESTING.md](docs/TESTING.md), [perf.md](docs/perf.md),
[design.md](docs/design.md).

## Architecture in one page

```text
Phone capture
  -> native RNNoise / Speex AGC / gain / limiter / Opus
  -> paced Opus packet (AES-GCM by default; plain when selected by the PC)
  -> UDP or TCP-framed USB/Bluetooth tunnel
  -> server Hello + single-session admission + encryption-mode decision
  -> session/replay/header validation + optional AES-GCM open
  -> adaptive jitter -> Opus decode or PLC
  -> gain / local gain / mute / downmix
  -> one backend writer: monitor, external cable, PipeWire, Windows driver, macOS HAL
```

Discovery uses mDNS `_rasamic._udp` and UDP beacon 47301. Reconnect order is
remembered IPs -> matching mDNS ID -> matching beacon ID -> `/24` probe, with
0.5–8 second backoff. Pairing pins a persistent P-256 server identity; QR payloads
reject a changed key outright.

Important boundaries:

- Audio is encrypted/authenticated by default. `--plain` or the GUI toggle removes both protections for a trusted LAN; post-handshake control JSON is plaintext in both modes. `SessionReady` authenticates the negotiated mode before sending starts.
- Sender enables Opus in-band FEC, but the receiver currently uses PLC for gaps;
  FEC recovery is not implemented.
- `CaptureTsUs` is transmitted but not used for server scheduling.
- One active session per server. A future mixer belongs before the backend.
- Native callbacks must stay non-blocking; backend writes are single-writer.
- Runtime Async is desktop `net11.0` only; mobile targets are excluded.

Full source map and invariants: [ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Completed performance pass — uncommitted

The current working tree includes:

- Runtime Async enabled centrally for desktop `net11.0`.
- `ValueTask` outer framing APIs with a `Task` inner exact-read helper. Full
  `ValueTask` conversion benchmarked slower and was rejected.
- Fused AVX2 post-gain + limiter with TensorPrimitives fallback and scalar-contract
  tests for NaN, signed zero, infinities, slices, and tails.
- Permanent BenchmarkDotNet comparison cases and updated performance notes.

Final measurements:

| Case | Before | After |
|---|---:|---:|
| Post-gain 480 / 960 floats | 118.5 / 224.8 ns | **73.2 / 145.0 ns**, 0 B |
| Runtime Async synchronous framing | 72.23 ns | **58.83 ns** (~19% faster) |
| Hybrid sync framing | Task: 51.86 ns / 448 B | **51.07 ns / 376 B** |
| Hybrid suspended framing | Task: 1,263.88 ns / 1,047 B | 1,287.98 ns / 1,079 B |

The suspended hybrid delta is small relative to network I/O; the synchronous fast
path saves a Task allocation. Post-gain “before” is the original ShortRun baseline
and “after” is the final DefaultJob run; the side-by-side candidate comparison is
in [perf.md](docs/perf.md). Reproduce from that file.

## Latency and server-controlled plain mode — uncommitted

The 1–1.5 s delay had three application-side causes: a 96,000-sample monitor FIFO
(up to 2 s mono), burst decode/write with no playout clock, and stale phone packets
sent during reconnect. AES-GCM was not the bottleneck.

- Monitor/cable playback is capped at 2,400 samples: 50 ms mono at 48 kHz.
- The server drains UDP without blocking, then decodes/writes one frame per
  real-time deadline. TCP audio ingress is bounded to eight queued frames.
- A >32-packet sequence jump after >100 ms of silence resynchronizes without a
  large PLC burst. The adaptive target is capped at 40 ms and shrinks after one
  stable second.
- The phone sender paces to the pipeline frame duration and flushes stale complete
  packets on reconnect.
- `--plain` / **Encrypt audio** is negotiated by the PC in `HelloAck`. Encryption
  remains the default; plain mode has no audio authentication or confidentiality.
- **Hear phone on this PC** independently controls speaker monitoring, including
  alongside VB-CABLE or the Windows driver.

Live final evidence: ~100 pkt/s, 0% loss, ~10 ms RTT, 10 ms write interval,
20–70 ms jitter dwell, zero output drops, 22 ms WASAPI device buffer. Acoustic
round-trip latency still needs a physical tap/loopback rig.

## External-cable backend — uncommitted

Windows can now use an externally installed signed audio cable instead of the
project driver:

- `ExternalCableBackend` writes to a selected playback endpoint and validates it
  before listening.
- `rasamicd --cable`, `--cable-name`, and `--list-outputs` expose CLI control.
- The Avalonia GUI lists playback endpoints, persists the WASAPI ID, and refreshes
  after installation.
- Native miniaudio enumeration exposes playback/capture names and stable Windows
  endpoint IDs through a synchronous UTF-8 callback ABI.
- `doctor` checks for both CABLE Input and CABLE Output.

Verified locally: native/managed Release builds, 184/184 tests, real WASAPI
enumeration, and the missing-cable error path. Not verified: an actual
VB-CABLE audio roundtrip, because the vendor driver is not installed on this
machine. Rasa does not bundle or install third-party drivers.

## Blockers and risks

### P0 — release blockers

1. **macOS CI still needs a verified rerun.**
   - `native.yml` and `ios.yml` use `macos-14`; those jobs were cancelled.
   - The 19 Xcode 26.6 HAL compile errors were traced to `REFIID` byte comparison,
     removed property names, duplicate latency selectors, and the current
     `GetPropertyData` signature. The source is patched, but Windows cannot compile
     the macOS target locally.
   - Acceptance: supported runner labels, plugin compiles, enumeration spike
     passes, ring-readable spike is evaluated rather than ignored.

2. **The project's branded Windows driver has no CI or install evidence.**
   - The external-cable backend avoids this blocker for users who accept a
     separate vendor installation.
   - `driver.yml` exists but has zero runs.
   - Local test-signing is blocked by Secure Boot policy on the maintainer PC.
   - A manually trusted root does not replace kernel Code Integrity or production
     WHQL/WHCP signing.
   - Acceptance: x64/ARM64 workflow passes; test-signed package installs on a
     test-signing VM; Driver Verifier runs clean; production signing path is
     documented separately.

3. **No release has been cut.**
   - No tags, releases, or successful `release` workflow.
   - Acceptance: every gate in [RELEASE.md](docs/RELEASE.md) passes on the release
     commit, artifacts are inspected, and platform claims match evidence.

4. **`main` has no branch protection.**
   - Failed checks do not block a merge or direct push.
   - Acceptance: require PR review and the CI, native, perf, Android, driver, and
     release checks that apply to the changed paths. This is a remote repository
     setting and is not changed by this local pass.

### P1 — product/verification gaps

- iOS device signing, TestFlight, and real iPhone capture need an Apple account,
  macOS, and hardware.
- Bluetooth phone-to-PC audio and Wi-Fi Direct streaming need physical two-device
  tests. Compile/listener success is not end-to-end evidence.
- Acoustic end-to-end latency (PC click -> phone -> stream back) still needs the
  phone rig; localhost pipeline latency is not acoustic latency.
- Control traffic is plaintext after pairing; decide whether protocol v2 should
  authenticate it before claiming full control-channel confidentiality.
- FEC is enabled on the encoder but not decoded on the receiver.
- Multi-client mixing is intentionally deferred; see
  [multi-client-eval.md](docs/multi-client-eval.md).
- Windows helper scripts contain machine-specific `M:\...` paths. Prefer portable
  CMake commands on new machines or make the scripts path-independent in a
  separate focused change.
- `IVirtualMicBackend` documentation still says “ACX”; the actual Windows driver
  is PortCls WaveRT/KMDF. Correct the comment when touching that interface.

## Ordered next agenda

1. **Repair macOS CI.** Update runner labels, port the HAL plugin to the current
   Xcode 26 interface, remove duplicate property cases, and get both spikes to run.
2. **Run the driver workflow.** Resolve WDK build issues, then validate x64/ARM64
   packages. Keep test-signing/reboot as a user action; do not bypass Secure Boot.
3. **Review and land the uncommitted performance + latency + documentation changes.**
   Run the full suite and benchmarks on the exact commit, then commit/push only
   when the user asks.
4. **Re-run Android device regression.** Use the new Release APK, verify Wi-Fi,
   USB, reconnect, background service, stereo, and memory on the Redmi Note 11.
5. **Close hardware-only gaps.** Bluetooth, Wi-Fi Direct, iPhone capture, and
   acoustic latency each need a recorded device test.
6. **Cut v0.1.0 only after release gates pass.** Follow [RELEASE.md](docs/RELEASE.md);
   do not tag merely because local tests are green.
7. **Post-1.0:** evaluate multi-client mixing and control-channel authentication.

## First session checklist for the next maintainer

```sh
git status
git log -5 --oneline
git submodule status
dotnet --version
dotnet workload list
```

Then:

1. Read [ARCHITECTURE.md](docs/ARCHITECTURE.md) before changing protocol, native,
   jitter, or backend code.
2. Build submodules/native prerequisites from
   [DEVELOPMENT.md](docs/DEVELOPMENT.md).
3. Run `dotnet test RasaMic.slnx -c Release`; expect 184/184 on this snapshot.
4. Check remote workflow state; do not infer it from local green tests.
5. Keep a list of user-only actions: test-signing, reboot, driver install, Apple
   signing, and destructive audio-service changes.
6. Do not commit or push unless explicitly asked. Stage specific files and scan
   for secrets. Never update git config or force-push `main`.
7. Do not drive the desktop UI. Use CLI, logs, registry/file edits, and headless
   tests; phone ADB automation is allowed.

## Working-tree notes at handoff time

Modified implementation/performance files:

- `Directory.Build.props`
- `src/RasaMic.Audio/PcmPostGain.cs`
- `src/RasaMic.Audio/AudioPipeline.cs`
- `src/RasaMic.Audio/AudioDevice.cs`
- `src/RasaMic.Audio/AudioDeviceEnumerator.cs`
- `src/RasaMic.Audio/MonitorOutput.cs`
- `src/RasaMic.Audio/NativeMethods.cs`
- `src/RasaMic.Client.Core/ClientSession.cs`
- `src/RasaMic.Protocol/ControlFraming.cs`
- `src/RasaMic.Protocol/ControlMessages.cs`
- `src/RasaMic.Core/Jitter/JitterBuffer.cs`
- `src/RasaMic.Client/MainPage.xaml.cs`
- `src/RasaMic.Server/RasaMicServer.cs`
- `src/RasaMic.Server/IVirtualMicBackend.cs`
- `src/RasaMic.Server/Backends/MonitorBackend.cs`
- `src/RasaMic.Server/Backends/SelfMonitoringBackend.cs`
- `src/RasaMic.Server/Backends/ExternalCableBackend.cs`
- `src/RasaMic.Server.Cli/Program.cs`
- `src/RasaMic.Server.Cli/Doctor.cs`
- `src/RasaMic.Server.App/ViewModels/MainWindowViewModel.cs`
- `src/RasaMic.Server.App/MainWindow.axaml`
- `src/RasaMic.Server.App/Services/SettingsStore.cs`
- `native/rasamic-native/include/rasamic_native.h`
- `native/rasamic-native/src/monitor.cpp`
- `native/rasamic-native/src/monitor_stub.cpp`
- `native/rasamic-native/src/pipeline.cpp`
- `native/rasamic-native/CMakeLists.txt`
- `tests/RasaMic.Audio.Tests/PcmPostGainTests.cs`
- `tests/RasaMic.Audio.Tests/MonitorOutputTests.cs`
- `tests/RasaMic.Audio.Tests/PipelineTests.cs`
- `tests/RasaMic.Core.Tests/JitterBufferTests.cs`
- `tests/RasaMic.IntegrationTests/ExternalCableBackendTests.cs`
- `tests/RasaMic.IntegrationTests/LoopbackTests.cs`
- `tests/RasaMic.IntegrationTests/SenderPacingTests.cs`
- `tests/RasaMic.Protocol.Tests/ControlFramingTests.cs`
- `tests/RasaMic.Server.App.Tests/HotkeyTests.cs`
- `tests/RasaMic.Server.App.Tests/MainWindowTests.cs`
- `tests/RasaMic.Server.App.Tests/SelfMonitoringBackendTests.cs`
- `tools/RasaMic.Benchmarks/Program.cs`

Modified/added documentation:

- `README.md`
- `docs/perf.md`
- `docs/windows-driver-feed.md`
- `docs/ARCHITECTURE.md`
- `docs/DEVELOPMENT.md`
- `docs/TESTING.md`
- `docs/OPERATIONS.md`
- `docs/RELEASE.md`
- `HANDOFF.md`

Expected unrelated submodule dirt: `native/third_party/rnnoise` contains downloaded
model archives, `models/`, and generated `src/rnnoise_data*.c/h`. These are ignored
local build inputs, not parent-repository changes.

## Documentation map

| Document | Use it for |
|---|---|
| [README.md](README.md) | Product overview, quick demo, user-facing build/run entry |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | Components, wire contracts, data flow, invariants |
| [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) | Clean-machine setup, native/managed/mobile builds, conventions |
| [docs/TESTING.md](docs/TESTING.md) | Test commands/counts, SIMD matrix, mobile and CI evidence |
| [docs/OPERATIONS.md](docs/OPERATIONS.md) | Backends, transports, ports, telemetry, troubleshooting |
| [docs/RELEASE.md](docs/RELEASE.md) | Gates, artifacts, signing, rollout, rollback |
| [docs/perf.md](docs/perf.md) | Benchmarks, latency, Runtime Async/ValueTask/AVX2 results |
| [docs/design.md](docs/design.md) | MAUI/Avalonia visual system and QA history |
| [docs/multi-client-eval.md](docs/multi-client-eval.md) | Single-session decision and future mixer design |
| [docs/windows-driver-feed.md](docs/windows-driver-feed.md) | Driver recon, IOCTL/ring contract, install constraints |
| [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) | Dependency and license inventory |

## Suggested skills for the next agent

- `handoff` — compact the current session for another agent.
- `run-tests` — select the correct repository test command and report real counts.
- `microbenchmarking` — BenchmarkDotNet design, execution, and interpretation.
- `analyzing-dotnet-performance` — async, allocation, and hot-path review.
- `vectorization` — SIMD equivalence, tails, fallbacks, and benchmarks.
- `maui` — mobile lifecycle, permissions, navigation, and platform seams.
- `dotnet-pinvoke` — native ABI/marshalling review.
- `process-api-net11` — .NET 11 process orchestration if release tooling changes.

## Handoff completeness checklist

- [x] Current commit, remote state, and uncommitted scope recorded.
- [x] Implemented vs verified platform status separated.
- [x] Architecture, protocol, security, and backend invariants documented.
- [x] Build/test/benchmark commands and current counts recorded.
- [x] macOS, driver, iOS, and release blockers stated with acceptance criteria.
- [x] README and contributor/operator/release guides completed.
- [x] Next agenda ordered and tied to concrete evidence.
- [x] Safety boundaries and user-only actions preserved.
- [ ] macOS CI green.
- [ ] Driver workflow and install/Driver Verifier evidence complete.
- [ ] Hardware-only mobile/transport checks complete.
- [ ] Release gates passed and v0.1.0 published.

The unchecked items are project work, not missing handoff documentation.
