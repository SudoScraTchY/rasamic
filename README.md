# Rasa

**Your phone, your microphone — everywhere.** Rasa turns an iPhone or Android
phone into a low-latency wireless microphone for Windows, macOS, and Linux
(x64 and arm64). A free, open-source (MIT) alternative to WO Mic.

- Encrypted Wi-Fi audio by default (Opus, AES-256-GCM, ECDH P-256 pairing), with server-controlled plain mode for trusted LANs
- Noise suppression (RNNoise), auto gain control (SpeexDSP), volume & quality controls
- Virtual microphone on every desktop OS — PipeWire/HAL or an optional signed Windows audio cable
- Smart auto-reconnect (remembered servers, mDNS/beacon/subnet ladder), global hotkeys, auto-start (`rasamicd --autostart enable`)
- iOS **and** Android clients; USB, Bluetooth (Android), Wi-Fi transports

> Status: early development, snapshot 2026-11-27. The full Release suite passes
> **184/184** on Windows. Protocol/crypto, discovery, reconnect, Android
> foreground stereo capture, USB transports, the Avalonia GUI, and Linux PipeWire
> are implemented and tested. Android native arm64/x86_64 and the Android Release
> client build successfully; the client build needs an explicit Android SDK path
> on the current machine.
>
> Windows can use an externally installed signed audio cable (`rasamicd --cable`)
> without test-signing. The project's own Windows driver CI builds unsigned x64 and
> ARM64 packages; installation remains signing/Secure Boot gated. The macOS HAL
> factory/property self-test passes, but hosted `coreaudiod` enumeration remains
> advisory and is not claimed as verified device support. iOS device signing and
> TestFlight need an Apple account/device. Bluetooth phone audio and Wi-Fi Direct
> streaming still need hardware verification. See [HANDOFF.md](HANDOFF.md) for the
> evidence and next actions.

## Documentation

- [User guide](docs/USER_GUIDE.md) — GUI/CLI quick start, pairing, backends, and troubleshooting
- [HANDOFF.md](HANDOFF.md) — current state, evidence, blockers, and next agenda
- [Architecture](docs/ARCHITECTURE.md) — components, data flow, contracts, invariants
- [Development](docs/DEVELOPMENT.md) — prerequisites, native/managed builds, conventions
- [Testing](docs/TESTING.md) — test matrix, commands, mobile and SIMD verification
- [Operations](docs/OPERATIONS.md) — backends, transports, telemetry, troubleshooting
- [Release](docs/RELEASE.md) — gates, packaging, signing, rollout, rollback
- [Performance](docs/perf.md) — benchmarks, latency, Runtime Async/ValueTask/AVX2
- [Design](docs/design.md) — client and Windows GUI design language
- [Multi-client evaluation](docs/multi-client-eval.md) — v1 single-session decision
- [Windows driver feed](docs/windows-driver-feed.md) — driver recon and IOCTL contract

## Layout

- `src/` — .NET 11 libraries, server engine + `rasamicd` CLI, MAUI mobile client
- `native/` — C/C++ audio pipeline (capture → RNNoise → AGC → Opus), third-party submodules
- `drivers/` — Windows virtual-mic driver fork (PortCls WaveRT/KMDF), macOS HAL plugin (CI-built, install script)
- `tests/`, `tools/` — unit + localhost loopback integration tests, loopback client, scripts

## Build

Requires .NET 11 SDK (`global.json`), CMake 3.22+ and Ninja. On Windows: VS 2022/2026
C++ workload + Windows SDK. Submodules: `git submodule update --init --recursive`.

```sh
# 1. rnnoise model weights (official tarball, checksum-verified)
cd native/third_party/rnnoise && sh download_model.sh && cd ../../..

# 2. native library (Windows x64; Android: tools/scripts/build-native-android.bat)
tools/scripts/build-native-win.sh

# 3. managed code + full test suite
dotnet build RasaMic.slnx -c Release
dotnet test RasaMic.slnx -c Release   # 184/184 on the current Windows tree

# MAUI Android client (needs Android SDK/NDK-built .so first)
tools/scripts/build-client-android.bat
```

Android build notes:

- JDK 17+ required (`JAVA_HOME`); the script pins it if the Microsoft JDK is present.
- Set `ANDROID_HOME` and `ANDROID_SDK_ROOT` to the SDK that contains build-tools,
  CMake, NDK, and `platforms;android-37.0`. The default
  `%LOCALAPPDATA%\Android\Sdk` on the current machine is incomplete; the working
  SDK is `M:\Android\Sdks`.
- If `dl.google.com` is unreachable, the platform zip is mirrored at
  `https://mirrors.cloud.tencent.com/AndroidSDK/platform-37.0_r02.zip` —
  extract it into `<sdk>/platforms/android-37.0`.
- Phone smoke test: `tools/scripts/adb-smoke.ps1` (USB debugging is a user action).
- Full setup and verified commands: [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) and
  [docs/TESTING.md](docs/TESTING.md).

## Phone → PC demo (M1)

1. PC: `dotnet run --project src/RasaMic.Server.Cli -- --monitor`
   (advertises via mDNS `_rasamic._udp` + UDP beacon :47301; `--no-discovery` to disable)
   — or the GUI: `dotnet run --project src/RasaMic.Server.App` (pairing payload on screen,
   tray icon, mute/gain controls, live link stats, global mute hotkey
   **Ctrl+Alt+M** — click Change to rebind). Turn **Hear phone on this PC** off to
   keep speaker monitoring silent; encryption and hearing apply on the next start.
2. Phone: install the APK (`src/RasaMic.Client/bin/Debug/net11.0-android/`),
   enter the PC's IP or paste the pairing payload, tap Connect. Leaving the IP
   field empty auto-finds the last remembered server; if the connection drops,
   the client reconnects on its own (watchdog + mDNS/beacon/subnet search).
   Streaming continues while the app is backgrounded (Android foreground service).
3. Talk into the phone — audio plays on the PC speakers. Verify the 6-digit
   pairing code matches on both sides.

Mic direction (Android 10+): turn on **Mic direction capture (AudioRecord)**
in the Audio card and pick front/back/external. Tradeoff: AudioRecord adds a
few ms of latency vs the default AAudio/Oboe LowLatency path, which cannot
select a mic direction — AAudio stays the default.

Stereo (Android): turn on **Stereo capture (AAudio)** in the Audio card.
L/R is negotiated in the Hello handshake and rides the same selected audio path;
the server downmixes for mono-only sinks (Windows driver, macOS HAL) while
monitor playback, external cables, and PipeWire stay stereo. Mutually exclusive with the
AudioRecord backend; iOS stays mono (device mic). Desktop check:
Headless phone connect for adb smoke (grant `RECORD_AUDIO` via `pm grant`
first; unlock the device once — MAUI does not surface the page behind the
keyguard, and sessions connected before screen-off keep streaming locked):
`adb shell am start -n dev.rasamic.client/crc644380c612ea504bd3.MainActivity --es rasa_ip <server-ip>`

`rasamic-loopback --server <ip> --stereo --tone --no-ns` streams L 440 Hz /
R 880 Hz.

## Latency and trusted-LAN plain mode

The phone sender and PC playout are paced to the 10 ms Opus frame. The monitor
queue is capped at 50 ms, large network gaps resynchronize without a burst of PLC
frames, and reconnect discards stale queued phone packets. Live stats show
`dwell`, `wr`, `out`, and `dev` so capture, server, and output-device delay can be
separated.

Encryption stays on by default. On a trusted LAN, the PC can disable audio
encryption for the next session:

```sh
rasamicd --monitor --plain
```

The Windows GUI exposes the same choice as **Encrypt audio**. Plain mode removes
AES-GCM authentication as well as confidentiality: anyone able to reach UDP 47300
can inject audio. Pairing still identifies the server, and control JSON was
already plaintext.

## Driverless demo (no virtual mic yet)

```sh
# PC 1 (or same PC): server plays received audio on speakers
dotnet run --project src/RasaMic.Server.Cli -- --monitor

# PC 2: stream this machine's microphone to the server
dotnet run --project tools/RasaMic.LoopbackClient -- --server <server-ip>
# or with QR pairing payload printed by the server:
dotnet run --project tools/RasaMic.LoopbackClient -- --payload <base64url-text>
```

The client shows a 6-digit pairing code — verify it matches the server side (TOFU).
With a pasted pairing payload the server key is pinned and MITM is rejected outright.

## USB (Android, adb reverse)

```sh
adb reverse tcp:47300 tcp:47300
```

On the phone, enable **USB (adb reverse)** and use server IP `127.0.0.1`.
adb forwards TCP only, so audio rides the control connection as
length-prefixed frames (same AES-GCM packets, demuxed by magic); the choice
is remembered per server and survives reconnects.

## USB (iOS, cable)

usbmuxd runs on the PC, so the PC dials the phone:

```sh
rasamicd --usbmux          # add --monitor / --cable / --driver / --pipewire as usual
```

Requires Apple Devices or iTunes on Windows (Apple Mobile Device Service),
native usbmuxd on macOS, or the distro `usbmuxd` package on Linux. On the
iPhone, enable **USB (cable)** and tap Connect — the app listens and the PC
bridges the tunnel into the server (binary-plist usbmuxd client, no extra
dependencies). Reconnects re-accept automatically. Covered by an end-to-end
integration test (mock usbmuxd → tunnel → encrypted audio → decode); on-device
verification pending an iPhone.

## Bluetooth (Android)

Pair the phone with the PC in Android Bluetooth settings first, then:

```sh
rasamicd --bluetooth       # add --monitor / --cable / --driver / --pipewire as usual
```

In the Android app, turn on **Bluetooth**, pick the paired PC, tap Connect.
Audio rides the same framed tunnel path as USB (RFCOMM, service
"Rasa Mic"). The PC listener uses 32feet (MIT): Windows and Linux (BlueZ);
macOS has no RFCOMM listener support. iOS Bluetooth would require MFi
licensing — not implemented. Reconnects re-dial the socket automatically.
Listener startup verified on Windows; phone-to-PC audio pending a paired
device.

## Wi-Fi Direct (Android)

No router needed: in the Android app tap **Create Wi-Fi Direct group** (the
phone becomes the group owner at `192.168.49.1`), join the group from the PC's
Wi-Fi Direct settings, then connect to the PC's Wi-Fi Direct IP as usual —
audio rides the normal UDP/TCP path with the server-selected encryption mode.
Android 13+ asks for the
nearby-devices permission; older versions need location permission (OS
requirement for Wi-Fi P2P). Group creation is compile-verified; end-to-end
streaming pending two Wi-Fi Direct devices.

## Windows virtual mic with an external audio cable

Install [VB-CABLE](https://vb-audio.com/Cable/) once, then run:

```sh
rasamicd doctor
rasamicd --list-outputs
rasamicd --cable
```

Rasa writes phone audio to the **CABLE Input** playback endpoint. In Discord,
OBS, or another target app, select **CABLE Output** as the microphone. The
Windows GUI exposes the same mode as **VB-CABLE (external)** and can refresh or
persist the selected playback endpoint.

This uses VB-Audio's signed driver; Rasa does not compile, extract, repackage,
install, or remove it. No test-signing or Secure Boot change is required. The
official installer remains a user action and may require UAC or a reboot.

## Linux virtual mic (M5)

No driver needed: `rasamicd --pipewire` creates a **"Rasa Mic"** capture source
(PulseAudio null-sink + remap-source via `pactl`, with a `pw-loopback` fallback)
and feeds received audio into it. Apps (Discord, browsers, `pw-record`) select
"Rasa Mic" as their input.

Requirements: PipeWire with `pipewire-pulse` (the audio feed goes through the
Pulse compat layer) and a session manager (`wireplumber`).

```sh
# Fedora:  sudo dnf install pipewire-utils pipewire-pulse wireplumber
# Debian:  sudo apt install pipewire-audio pipewire-bin pipewire-pulse wireplumber
rasamicd doctor                      # checks pw-loopback, pactl, Pulse socket
dotnet run --project src/RasaMic.Server.Cli -- --pipewire
pw-cli ls Node | grep rasamic-mic    # source appears once a client streams
pw-record --target rasamic-mic check.wav
```

Verified end-to-end on a headless Fedora VM: tone client → server → "Rasa Mic"
source → `pw-record` capture at 98 pkt/s, 0% loss, 0.5 ms jitter; captured
RMS 0.046 (gate 0.001). Tone tests run with `--no-ns` — RNNoise classifies a
pure sine as non-speech and suppresses it. The CI workflow
(`linux-native-and-tests`) gates the same path with a `pw-record` capture and
an RMS check.

## Latency

Pipeline probe (synthetic bursts through the real localhost UDP/TCP path —
encode, AES-GCM, jitter buffer, decode; capture and speaker playback add
platform latency on top):

```sh
dotnet run --project tools/RasaMic.LatencyProbe            # 20 bursts, ~12 s
dotnet run --project tools/RasaMic.LatencyProbe -- 1200    # 10-min soak
```

Measured on Windows x64 localhost with the corrected-cadence probe: three runs at
**median 28.3–31.4 ms, p95 40.6–44.7 ms** (CI gate: p95 < 50 ms). The probe drops
frames missed during scheduling stalls instead of creating an artificial backlog;
earlier catch-up-harness numbers are not comparable.
10-minute soak (1200 bursts): 1200/1200 detected, p95 16.2 ms, no managed-heap
growth. Sender hot path allocates 0 bytes over a 60 s bench
(`RASA_ALLOC_BENCH_SECONDS=60 dotnet test tests/RasaMic.IntegrationTests`).
WAV mode adds sample-accurate cross-correlation of the fed burst against the
backend's PCM: `dotnet run --project tools/RasaMic.LatencyProbe -- 30 50 out.wav`.
Compare it only within the same probe revision because recording adds backend work.
Module benchmarks, per-stage budgets, live p95
telemetry and the CI regression gate: `docs/perf.md`.
Acoustic end-to-end measurement (PC click → phone → stream back) requires the
phone rig and is pending.

## Quality

Measured through the real native pipeline (encode → decode, 440 Hz tone,
`tests/RasaMic.Audio.Tests/QualityGateTests.cs`):

| Bitrate | SNR | THD |
|---|---|---|
| 16 kbps | 26.3 dB | 0.05% |
| 32 kbps | 27.5 dB | 0.02% |
| 64 kbps | 27.8 dB | 0.02% |
| 96 kbps | 27.8 dB | 0.02% |

RNNoise A/B on a white-noise floor: **60.5 dB reduction** (gate ≥10 dB).
AGC, mute, gain and PLC behavior are asserted in the same suite.
One active session per server (a second client gets `Busy`); the design for a
future multi-client mix is evaluated in
[docs/multi-client-eval.md](docs/multi-client-eval.md).

## Release packaging

There are no published releases yet. The full gate, signing, rollout, and rollback
checklist is in [docs/RELEASE.md](docs/RELEASE.md).

```sh
# zip with self-contained rasamicd + native pipeline + licenses (per RID)
tools/scripts/package-release.sh win-x64 native/rasamic-native/build-win-x64/rasamic_native.dll 0.1.0
```

`.github/workflows/release.yml` builds all six server RIDs (win/linux/osx ×
x64/arm64) on `v*` tags and attaches the zips to the GitHub Release.

### Windows GUI installer (Velopack)

```sh
dotnet tool install -g vpk
tools/scripts/package-velopack.sh win-x64 native/rasamic-native/build-win-x64/rasamic_native.dll 0.1.0
```

Produces `RasaMic-win-Setup.exe` (per-user install, no admin needed), full +
delta packages and a `RELEASES` feed in `dist/velopack-out-win-x64/`;
release.yml attaches them for win-x64 and win-arm64. Installed builds check
for updates at startup when `RASA_UPDATE_FEED` points at a feed URL —
downloads run in the background and apply on next exit, never mid-stream.
No MSI: WiX v6+ charges an Open Source Maintenance Fee ($10–60/month by org
size), so the MIT-licensed Velopack path is the installer. Store submission
remains a later M7 step.

## License

MIT — see [LICENSE](LICENSE) and [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).