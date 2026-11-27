# Rasa project handoff

**Snapshot:** 2026-11-27

**Repository:** <https://github.com/SudoScraTchY/rasamic>

**Driver fork:** <https://github.com/SudoScraTchY/rasamic-windows-driver>

**Code revision:** `abf3deaeb37f551746c7799955cd9e1248177d95`

**Driver revision:** `156ebaf90fe35558b652e9918fcbc77207f6bfb6`

**License:** MIT, Rasa Contributors

This is the maintainer entry point. It records what is implemented, what is
actually verified, what blocks a release, and what the next person should do.
It does not replace the architecture, development, testing, operations, or
release guides linked below.

## Executive summary

Rasa is an open-source WO Mic alternative: an Android/iOS phone captures audio,
runs native noise suppression/AGC, encodes Opus, and streams encrypted audio by
default to a Windows/macOS/Linux server. The PC can explicitly select plain audio
for a trusted LAN. The server exposes monitor output or a virtual microphone
backend.

The strongest verified path is **Android -> Windows/Linux server -> monitor or
PipeWire**. The managed Release suite is green, Android and unsigned iOS device
builds pass, native matrices pass, and all six server packages build. The Windows
driver CI produces real x64 and ARM64 kernel binaries.

The project is **not release-ready**. macOS factory/property calls pass, but hosted
`coreaudiod` discovery remains advisory. Driver installation, Driver Verifier,
production signing, iOS signing/device capture, Bluetooth, Wi-Fi Direct, and
acoustic latency still need hardware or account-gated evidence. No tag or GitHub
Release has been published.

## Platform and feature status

| Area | Implemented | Verified | Gap / blocker |
|---|---|---|---|
| Windows server + GUI | Yes | 184/184 Release tests; headless GUI tests; plain mode; independent PC-speaker monitoring; Velopack install/uninstall round-trip | External-cable audio round-trip needs a user-installed cable |
| Windows virtual mic | External-cable backend plus IOCTL feed, kernel SPSC ring, user-mode backend | Unsigned x64/ARM64 driver packages build; PE architectures inspected; WASAPI enumeration and missing-cable failure verified | Test-signing/install, Driver Verifier, and production WHQL/WHCP signing remain |
| Linux server | Yes | Native x64/arm64 and managed CI pass; PipeWire source capture passes | Keep distro/package smoke coverage current |
| macOS server | Managed front end + HAL writer/plugin | Managed CI passes; HAL compiles; ad-hoc-signed factory/property self-test passes | Hosted enumeration/ring spikes are advisory; real Mac validation remains |
| Android client | Wi-Fi, adb-reverse USB, foreground service, stereo Oboe, optional AudioRecord, discovery/reconnect | Native arm64/x86_64 and Release APK job pass; prior live Wi-Fi soak and capture-active bench | Bluetooth and Wi-Fi Direct need hardware verification |
| iOS client | AVAudioEngine mono capture, USB tunnel code, pairing/UI | Unsigned device-arm64 Release compile passes with Xcode 27.2 | Apple signing, provisioning, device install, and real capture remain |
| Bluetooth | Android client + PC RFCOMM listener | Listener startup on Windows | Phone-to-PC audio needs paired hardware |
| Multi-client | Single-session `Busy` behavior and forward-compatible `sessionId` | Integration tests | Mixing intentionally deferred; see [multi-client-eval.md](docs/multi-client-eval.md) |

## Evidence snapshot

### Local — 2026-11-27

| Check | Result | Evidence |
|---|---|---|
| `dotnet test RasaMic.slnx -c Release` | **184 passed, 0 failed, 0 skipped** | `.tools/ci-final-test.log`; .NET 11.0.100 Windows |
| Corrected latency probe, three consecutive runs | Median **28.3–31.4 ms**, p95 **40.6–44.7 ms**, max **46.6 ms** | `.tools/perf-probe-after.log` |
| Previous catch-up probe, same command | Median **66.9 ms**, p95 **107.3 ms** | `.tools/perf-probe-before.log`; demonstrated artificial backlog |
| Android Release with explicit SDK + JDK 17 | **Build succeeded, 0 warnings, 0 errors** | `.tools/ci-cd-android-build.log` (ignored) |
| BenchmarkDotNet DefaultJob | **22 cases, 0 B allocated; no >10% regressions** | `.tools/perf-refresh/`; baselines under `docs/perf-baselines/` |
| Perf comparator synthetic checks | Noisy 1.12x point mean passes; clear 1.30x regression fails | `.tools/perf-gate-test/` (ignored) |
| Workflow syntax | **actionlint 1.7.12 passed** | `.github/actionlint.yaml` plus workflow files |
| Driver package architectures | x64 `8664`; ARM64 `AA64` | Final CI `.sys` artifacts inspected with `dumpbin` |

The Android build needs `ANDROID_HOME` and `ANDROID_SDK_ROOT` set explicitly on
this machine. The default `%LOCALAPPDATA%\Android\Sdk` has only licenses and
platform-tools, while `M:\Android\Sdks` contains build-tools, platforms, CMake, and
NDK.

### Remote

Final verification on code/workflow revision `abf3dea`:

| Workflow | Result | Run |
|---|---|---|
| `ci` | Passed all five jobs | [45946396112](https://github.com/SudoScraTchY/rasamic/actions/runs/45946396112) |
| `native` | Passed six ABI/platform jobs and `android-app` | [45946396054](https://github.com/SudoScraTchY/rasamic/actions/runs/45946396054) |
| `ios` | Passed unsigned device-arm64 build | [45946396085](https://github.com/SudoScraTchY/rasamic/actions/runs/45946396085) |
| `perf` | Passed; latency p95 34.3 ms | [45946396059](https://github.com/SudoScraTchY/rasamic/actions/runs/45946396059) |
| `driver` | Passed x64 and ARM64 unsigned packages | [45946452255](https://github.com/SudoScraTchY/rasamic/actions/runs/45946452255) |
| `release` | Passed all six RID packages | [45946455576](https://github.com/SudoScraTchY/rasamic/actions/runs/45946455576) |

`main` branch protection is active with 14 required checks, one approving review,
stale-review dismissal, conversation resolution, no force-push/deletion, and an
administrator emergency bypass. Full policy: [TESTING.md](docs/TESTING.md).
Documentation-only follow-up commits do not change the tested binaries.

Driver artifact SHA-256 from run `45946452255`:

- x64 `.sys`: `3676d88fe8ece6eb99b9969c5e0afc24eb19ccbbf2c9f48616854d9d5d41e173`
- ARM64 `.sys`: `f1475b000d48d20aa23e63f045822afdb18510e7de90d737b17922d406b7a4f4`

The release workflow was exercised with `workflow_dispatch`; it uploaded packages
but did not create a tag or GitHub Release.

### Earlier device/package evidence

- 2026-11-18 Wi-Fi soak, fixed APK, 11 samples over 10 minutes: 11/11 zero-loss,
  ~100 pkt/s, jitter 0.8–2.2 ms, no phone/server memory growth.
- 2026-11-19 capture-active Redmi Note 11 bench: mono 4.4 ms / stereo 8.2 ms per
  10 ms frame. Earlier 21/42 ms numbers were doze-state artifacts.
- Linux PipeWire tone capture verified end-to-end; CI repeats the smoke.
- Velopack Windows GUI installer passed a local silent install/uninstall round-trip.
- iOS unsigned compilation is not device signing, provisioning, or TestFlight evidence.

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
- Sender enables Opus in-band FEC, but the receiver currently uses PLC for gaps; FEC recovery is not implemented.
- `CaptureTsUs` is transmitted but not used for server scheduling.
- One active session per server. A future mixer belongs before the backend.
- Native callbacks must stay non-blocking; backend writes are single-writer.
- Runtime Async is desktop `net11.0` only; mobile targets are excluded.

Full source map and invariants: [ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Completed performance pass

- Runtime Async enabled centrally for desktop `net11.0`.
- `ValueTask` outer framing APIs with a `Task` inner exact-read helper. Full
  `ValueTask` conversion benchmarked slower and was rejected.
- Fused AVX2 post-gain + limiter with TensorPrimitives fallback and scalar-contract
  tests for NaN, signed zero, infinities, slices, and tails.
- Permanent BenchmarkDotNet comparison cases and per-runner baselines.
- Uncertainty-aware CI regression gate: current mean minus two standard errors
  must exceed the baseline by more than 10%.

| Case | Before | After |
|---|---:|---:|
| Post-gain 480 / 960 floats | 118.5 / 224.8 ns | **73.2 / 145.0 ns**, 0 B |
| Runtime Async synchronous framing | 72.23 ns | **58.83 ns** (~19% faster) |
| Hybrid sync framing | Task: 51.86 ns / 448 B | **51.07 ns / 376 B** |
| Hybrid suspended framing | Task: 1,263.88 ns / 1,047 B | 1,287.98 ns / 1,079 B |

The suspended hybrid delta is small relative to network I/O; the synchronous fast
path saves a Task allocation. Reproduce and compare from [perf.md](docs/perf.md).

## Latency and server-controlled plain mode

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
- The latency probe drops stale cadence after scheduling stalls instead of
  replaying overdue frames and creating an artificial backlog.

Live final evidence: ~100 pkt/s, 0% loss, ~10 ms RTT, 10 ms write interval,
20–70 ms jitter dwell, zero output drops, 22 ms WASAPI device buffer. Acoustic
round-trip latency still needs a physical tap/loopback rig.

## External-cable backend

Windows can use an externally installed signed audio cable instead of the project
driver:

- `ExternalCableBackend` writes to a selected playback endpoint and validates it
  before listening.
- `rasamicd --cable`, `--cable-name`, and `--list-outputs` expose CLI control.
- The Avalonia GUI lists playback endpoints, persists the WASAPI ID, and refreshes
  after installation.
- Native miniaudio enumeration exposes playback/capture names and stable Windows
  endpoint IDs through a synchronous UTF-8 callback ABI.
- `doctor` checks for both CABLE Input and CABLE Output.

Verified locally: native/managed Release builds, 184/184 tests, real WASAPI
enumeration, and the missing-cable error path. Not verified: an actual VB-CABLE
audio roundtrip, because the vendor driver is not installed on this machine.
Rasa does not bundle or install third-party drivers.

## Blockers and risks

### P0 — release blockers

1. **macOS virtual-mic support is not verified.**
   - The HAL compiles, is ad-hoc signed, and passes direct factory/property calls.
   - Hosted `coreaudiod` does not enumerate it; enumeration and ring spikes are
     advisory and must not be presented as supported-device evidence.
   - Acceptance: enumeration and non-silent ring capture pass on a supported Mac.

2. **The branded Windows driver has no install/Driver Verifier evidence.**
   - CI builds unsigned x64/ARM64 packages and skips package verification because
     the hosted WDK lacks a usable x86 `InfVerif.dll`.
   - Local test-signing is blocked by Secure Boot policy on the maintainer PC.
   - A manually trusted root does not replace kernel Code Integrity or production
     WHQL/WHCP signing.
   - Acceptance: signed package installs on a test-signing VM; Driver Verifier runs
     clean; production signing path is documented separately.

3. **No release has been cut.**
   - No tags or GitHub Releases exist. Successful dispatch packaging is not a
     published release.
   - Acceptance: every applicable gate in [RELEASE.md](docs/RELEASE.md) passes,
     artifacts are inspected, and platform claims match evidence.

### P1 — product/verification gaps

- iOS device signing, TestFlight, and real iPhone capture need an Apple account,
  macOS, and hardware.
- Bluetooth phone-to-PC audio and Wi-Fi Direct streaming need physical two-device
  tests. Compile/listener success is not end-to-end evidence.
- Acoustic end-to-end latency still needs the phone rig; localhost pipeline latency
  is not acoustic latency.
- Control traffic is plaintext after pairing; decide whether protocol v2 should
  authenticate it before claiming full control-channel confidentiality.
- FEC is enabled on the encoder but not decoded on the receiver.
- Multi-client mixing is intentionally deferred; see
  [multi-client-eval.md](docs/multi-client-eval.md).
- Windows helper scripts contain machine-specific `M:\...` paths. Prefer portable
  CMake commands on new machines or make the scripts path-independent separately.
- `IVirtualMicBackend` documentation still says “ACX”; the actual Windows driver
  is PortCls WaveRT/KMDF. Correct the comment when touching that interface.

## Ordered next agenda

1. **Validate macOS on real hardware.** Diagnose hosted `coreaudiod` discovery,
   then prove enumeration and ring capture before claiming a macOS virtual mic.
2. **Validate the Windows driver package.** Keep test-signing/reboot as a user
   action; do not bypass Secure Boot. Install on a VM and run Driver Verifier.
3. **Re-run Android device regression.** Verify Wi-Fi, USB, reconnect, background
   service, stereo, and memory on the Redmi Note 11.
4. **Close hardware-only gaps.** Bluetooth, Wi-Fi Direct, iPhone capture, and
   acoustic latency each need a recorded device test.
5. **Cut v0.1.0 only after release gates pass.** Follow [RELEASE.md](docs/RELEASE.md);
   do not tag merely because CI is green.
6. **Post-1.0:** evaluate multi-client mixing and control-channel authentication.

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

Expected submodule dirt: `native/third_party/rnnoise` may contain downloaded model
archives, `models/`, and generated `src/rnnoise_data*.c/h`. These are ignored local
build inputs, not parent-repository changes.

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

- [x] Current code/driver revisions and remote state recorded.
- [x] Implemented vs verified platform status separated.
- [x] Architecture, protocol, security, and backend invariants documented.
- [x] Build/test/benchmark commands and current counts recorded.
- [x] macOS, driver, iOS, and release blockers stated with acceptance criteria.
- [x] README and contributor/operator/release guides completed.
- [x] Next agenda ordered and tied to concrete evidence.
- [x] Safety boundaries and user-only actions preserved.
- [ ] macOS enumeration and ring capture verified.
- [ ] Driver install and Driver Verifier evidence complete.
- [ ] Hardware-only mobile/transport checks complete.
- [ ] Release gates passed and v0.1.0 published.

The unchecked items are project work, not missing handoff documentation.
