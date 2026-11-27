# Rasa user guide

Rasa turns an Android or iPhone microphone into a low-latency microphone for a
Windows, macOS, or Linux PC. The PC server owns the output backend and decides
whether audio encryption is enabled.

There is no packaged public installer yet. Build the server and phone app from
source using the [development guide](DEVELOPMENT.md), then follow the quick start
below.

## Quick start: phone to PC speakers

1. Build the native library and managed solution:
   ```sh
   tools/scripts/build-native-win.sh
   dotnet build RasaMic.slnx -c Release
   ```
2. Start the Windows GUI:
   ```sh
   dotnet run --project src/RasaMic.Server.App -c Release
   ```
3. Keep **Monitor (PC speakers)** selected. Click **Start server**.
4. Install the Android APK from
   `src/RasaMic.Client/bin/Release/net11.0-android/` on the phone.
5. Put the phone and PC on the same network. In the phone app, enter the PC IP
   shown by the GUI, or paste the pairing payload, then tap **Connect**.
6. Verify that the six-digit pairing code matches on the phone and PC.
7. Talk into the phone. Audio plays through the PC speakers while the session is
   active.

The phone app does not scan the QR displayed by the Windows GUI. Copy the pairing
payload shown below the QR instead. Pasting the payload pins the server identity;
manual IP pairing uses a six-digit TOFU code.

Allow Rasa through the PC firewall for TCP/UDP 47300. Discovery also uses UDP
47301 and mDNS `_rasamic._udp`.

## Windows GUI controls

- **Encrypt audio** controls the next session. Encryption is on by default.
- **Hear phone on this PC** controls speaker monitoring independently of the
  virtual microphone.
- **Mute** and **Gain** affect the active session.
- **Ctrl+Alt+M** is the default global mute hotkey; use **Change** to rebind it.
- The LAN address, pairing payload, backend, and live statistics are shown while
  the server is running.

Encryption and hearing controls apply on the next server start. Stop the server,
change them, and start it again.

## CLI quick start

Start monitor output:

```sh
rasamicd --monitor
```

List playback endpoints:

```sh
rasamicd --list-outputs
```

Use an installed VB-CABLE-compatible playback endpoint:

```sh
rasamicd --cable
rasamicd --cable --cable-name "CABLE Input"
```

Run environment checks:

```sh
rasamicd doctor
```

Show all options:

```sh
rasamicd --help
```

For a source checkout without an installed `rasamicd` command:

```sh
dotnet run --project src/RasaMic.Server.Cli -c Release -- --monitor
```

## Output backends

| Backend | Use |
|---|---|
| Monitor | Hear phone audio through PC speakers; no virtual microphone |
| External cable | Send audio to an installed VB-CABLE-compatible playback endpoint |
| Windows driver | Feed the project's virtual microphone after a signed driver is installed |
| PipeWire | Expose a virtual microphone source on Linux |
| macOS HAL | Expose a virtual microphone on macOS; HAL build/install remains platform-gated |

With a virtual-microphone backend selected, **Hear phone on this PC** can be turned
off without disabling the virtual microphone.

## Trusted-LAN plain mode

Encryption is the safe default. On a trusted LAN, the PC can disable audio
encryption:

```sh
rasamicd --monitor --plain
```

In the GUI, turn off **Encrypt audio** and start the server. The phone follows the
PC's authenticated mode decision; there is no separate phone setting.

Plain mode removes audio confidentiality and authentication. Anyone able to reach
UDP 47300 can inject audio. Use it only on a trusted network.

## Phone transports

- **Wi-Fi:** enter the PC IP or use discovery. UDP carries audio; TCP carries
  control messages.
- **USB/adb reverse:** enable USB debugging, run `adb reverse tcp:47300 tcp:47300`,
  enable **USB (adb reverse)** in the phone app, and use `127.0.0.1`.
- **Bluetooth and Wi-Fi Direct:** implemented in the client, but still need
  hardware verification.

The Android foreground service keeps streaming while the app is backgrounded.

## Troubleshooting

**The phone cannot find the PC**

- Confirm both devices are on the same LAN and that PC firewall rules allow
  TCP/UDP 47300 plus UDP 47301.
- Enter the PC IP manually. The GUI displays the active LAN address.
- Avoid VPNs and virtual adapters that isolate the phone from the PC.

**Pairing fails**

- Paste the full pairing payload, not only the six-digit code.
- If the PC identity changed, replace the old saved payload on the phone.
- Manual IP pairing requires the code shown on both sides to match.

**No sound on the PC**

- Select **Monitor (PC speakers)** and enable **Hear phone on this PC**.
- Check the Windows output endpoint and system volume.
- Watch the GUI/CLI packet rate, loss, jitter, and output-drop counters.

**No sound in Discord/OBS**

- Select an installed cable backend, or install/sign the project's Windows driver.
- For VB-CABLE, Rasa plays to **CABLE Input**; Discord/OBS must select **CABLE
  Output** as its microphone.
- Monitor mode never creates a virtual microphone.

**Audio is delayed**

- Use the default low-latency Android capture path rather than AudioRecord.
- Keep Wi-Fi signal strong and avoid congested channels.
- Plain mode removes encryption overhead, but encryption is not the main source
  of network latency.
- Check `dwell`, `wr`, `out`, and `dev` in live statistics; large output-buffer or
  dwell values indicate buffering rather than crypto cost.

## Current limits

- One phone can stream at a time. A second client receives `Busy`; multi-client
  mixing is not implemented.
- The phone has a latching **Mute** control, not hold-to-talk.
- Actual VB-CABLE audio, Bluetooth phone audio, Wi-Fi Direct streaming, and
  acoustic mouth-to-speaker latency still require hardware verification.
