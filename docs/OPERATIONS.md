# Operations guide

This guide covers running Rasa, selecting a backend, transports, telemetry, and
first-line diagnosis. Build setup is in [DEVELOPMENT.md](DEVELOPMENT.md).

## Ports and discovery

| Port | Protocol | Purpose |
|---|---|---|
| TCP/UDP 47300 | TCP control + encrypted or plain audio | Default server endpoint |
| UDP 47301 | Beacon | LAN discovery fallback |
| mDNS `_rasamic._udp` | multicast DNS | Primary discovery path |

Allow the server through the host firewall for the active transport. Discovery
only proposes a server; pairing pins the server key. A pasted pairing payload pins
the key directly, while manual pairing uses a six-digit TOFU code. The phone app does
not scan the QR shown by the Windows GUI; copy the payload instead.

## Start the server

Headless monitor output:

```sh
dotnet run --project src/RasaMic.Server.Cli -c Release -- --monitor
```

Windows GUI:

```sh
dotnet run --project src/RasaMic.Server.App -c Release
```

The GUI shows the LAN IPv4, pairing payload, output backend, mute/gain, live stats,
and tray behavior. **Hear phone on this PC** controls speaker monitoring
independently of a virtual microphone. **Encrypt audio** controls the next
session's wire protection. Both apply on the next server start. The default
global mute hotkey is Ctrl+Alt+M.

Useful CLI checks and modes:

```sh
rasamicd doctor
rasamicd --monitor
rasamicd --monitor --plain
rasamicd --cable
rasamicd --cable --cable-name "CABLE Input"
rasamicd --list-outputs
rasamicd --driver
rasamicd --pipewire
rasamicd --hal
rasamicd --usbmux
rasamicd --bluetooth
rasamicd --autostart enable
```

Backend flags can be combined with transport listeners, for example
`rasamicd --usbmux --monitor`. `--plain` is a server-start setting: HelloAck
tells the client not to seal audio. Encryption remains the default. Plain mode
removes authentication and confidentiality, so use it only on a trusted LAN.

### Self-monitoring

Monitor mode plays phone audio through the PC speakers. In the GUI, turn off
**Hear phone on this PC** to silence speaker playback. With VB-CABLE or the
Windows driver selected, the same toggle independently adds or removes PC
speaker playback while leaving the virtual microphone active.

## Desktop backends

| Backend | Platform | Runtime requirements | Status |
|---|---|---|---|
| Monitor | Windows/macOS/Linux | Working output device | End-to-end verified |
| External cable | Windows | User-installed VB-CABLE or compatible playback/capture pair | Endpoint enumeration verified; audio roundtrip pending cable install |
| PipeWire | Linux | `pipewire-pulse`, `wireplumber`, `pw-loopback`, `pactl` | End-to-end verified |
| Windows driver | Windows x64/ARM64 | Built/signed driver package; test-signing or production signature | Feed implemented; install user-gated |
| HAL plugin | macOS | Plugin installed in `/Library/Audio/Plug-Ins/HAL/` | Source implemented; CI compile blocked |

Backends are single-writer. Do not move mixing into a backend; a future
multi-client mixer must run before the backend write.

### Linux PipeWire

```sh
sudo dnf install pipewire-utils pipewire-pulse wireplumber   # Fedora
# Debian/Ubuntu: sudo apt install pipewire-audio pipewire-bin pipewire-pulse wireplumber
rasamicd doctor
rasamicd --pipewire
pw-cli ls Node | grep rasamic-mic
pw-record --target rasamic-mic check.wav
```

Nodes appear when the first session starts. Tone checks must use `--no-ns`.

### Windows external cable

Install the official [VB-CABLE package](https://vb-audio.com/Cable/) before
starting Rasa. The driver install is a user action; Rasa never installs or
removes it.

```sh
rasamicd doctor
rasamicd --list-outputs
rasamicd --cable
```

Rasa opens **CABLE Input (VB-Audio Virtual Cable)** for playback. Select
**CABLE Output (VB-Audio Virtual Cable)** as the microphone in Discord/OBS.
Use `--cable-name` for a different playback endpoint. The GUI lists playback
endpoints, stores the selected WASAPI ID, and provides Refresh after install.

No test-signing or Secure Boot change is needed. If the endpoint is missing,
Rasa stops before listening and points to the official download. Do not select
CABLE Output as Rasa's playback endpoint; the data path is CABLE Input to
CABLE Output.

### Windows driver

The driver fork and feed contract are documented in
[windows-driver-feed.md](windows-driver-feed.md). The install helper trusts a
self-signed test certificate and uses `pnputil`/`devcon`.

**User action only:** enabling test-signing requires an elevated
`bcdedit /set testsigning on` and a reboot. Secure Boot policy may reject the
change. Never automate this or attempt to bypass Secure Boot. A manually trusted
root does not replace kernel Code Integrity or production WHQL/WHCP signing.

### macOS HAL

The CI failure is a compile-time SDK/API mismatch in
`drivers/macos/RasaMicHalPlugin/RasaMicHalPlugin.cpp`, not a runtime denial. Fix
that build before claiming HAL verification. Installation restarts
`coreaudiod`, so treat plugin install/removal as a user-visible maintenance action.

## Client transports

- **Wi-Fi:** UDP audio plus TCP control; AES-GCM is on unless the server selects plain mode. Preferred for low latency.
- **Android USB:** `adb reverse tcp:47300 tcp:47300`, then server `127.0.0.1`.
  Audio is framed over the control TCP connection because adb does not forward UDP.
- **iOS USB:** PC runs `rasamicd --usbmux` and dials the phone through usbmuxd.
  Requires Apple Devices/iTunes on Windows, native usbmuxd on macOS, or the distro
  package on Linux.
- **Android Bluetooth:** pair in Android settings, run `rasamicd --bluetooth`, then
  select the PC. RFCOMM service name is `Rasa Mic`. No iOS Bluetooth support.
- **Android Wi-Fi Direct:** create the phone group, join from the PC, then use the
  PC's Wi-Fi Direct IP. End-to-end streaming still needs two-device verification.

After USB testing:

```sh
adb reverse --remove tcp:47300
```

A stale reverse tunnel can make discovery find the phone's own adb listener and
reconnect back into the PC.

## Telemetry

At 1 Hz the server reports RTT, loss, jitter, decoder p95, jitter dwell, backend
write gap, monitor queue depth, output drops, and the native output-device buffer.
The CLI ticker prints `dec … dwell … wr … out … dev … drops …`. `out` is capped
at 50 ms for miniaudio monitor/cable output; `dev` is the WASAPI/backend buffer.
Capture these in soak logs together with packet rate and sequence gaps.

Soak gates: loss <0.1%, jitter p99 <30 ms, phone PSS/server WS growth <5%, no
steady-state Gen2 GC, and zero sequence gaps.

## Troubleshooting

| Symptom | Check |
|---|---|
| Phone cannot find server | Same LAN/subnet; firewall TCP/UDP 47300 and UDP 47301; mDNS/beacon enabled; GUI IPv4 is a real LAN adapter |
| Discovery finds a loopback/adb address | Remove stale `adb reverse` tunnels; discovery filters loopback A records and the client's own unicast addresses |
| USB connects but no audio | Confirm `adb reverse tcp:47300 tcp:47300`; use `127.0.0.1`; audio is TCP-framed in this mode |
| Pairing fails | Compare six-digit code; paste the payload to pin the key; check server is not already `Busy` |
| High latency on Android | Unlock device; keep capture-active foreground service; compare `dwell`, `wr`, `out`, and `dev`. High `dwell` indicates sender/server pacing, high `out` indicates app playback backlog, high `dev` indicates the output device |
| Hearing delayed self-monitoring | Turn off **Hear phone on this PC**, or use VB-CABLE/driver output without speaker monitoring |
| Plain mode rejected or ignored | Restart the PC server after changing **Encrypt audio** or `--plain`; the mode is negotiated at connect time |
| Tone is silent | Add `--no-ns`; RNNoise suppresses pure tones |
| PipeWire source missing | Run `rasamicd doctor`; start a session; check `pipewire-pulse` and `wireplumber` |
| VB-CABLE backend reports missing endpoint | Install official VB-CABLE, run `rasamicd --list-outputs`, then choose CABLE Input for Rasa and CABLE Output in the target app |
| Target app has no cable microphone | Confirm CABLE Output appears under Windows recording devices and is selected in the app; Rasa must write to CABLE Input |
| Windows driver will not install | Check test-signing/Secure Boot policy and certificate trust; production release needs WHQL/WHCP |
| Android build cannot find `aapt2.exe` | Set `ANDROID_HOME` and `ANDROID_SDK_ROOT` to the SDK containing build-tools; set JDK 17 |
| Native configure cannot find RNNoise data | Run `download_model.sh` in the RNNoise submodule and verify its checksum |
| GUI hotkey tests race | GUI tests disable xUnit parallelization because the hook is process-wide |
| macOS HAL CI fails | Update the plugin against the current AudioServerPlugIn interface and remove duplicate property cases |

## Safety boundaries

- Do not drive the desktop UI for QA; use CLI, logs, file/registry edits, and
  headless tests. Phone ADB automation is allowed.
- Test-signing changes and reboot are user actions.
- Do not kill shared audio services or install/remove drivers without explicit
  authorization.
- Do not commit `.pfx`, `.snk`, `secrets.json`, model archives, or `.tools/` output.
