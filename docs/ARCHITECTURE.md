# Architecture

Rasa is a phone-to-desktop microphone system. The phone captures PCM, runs native
noise suppression/AGC, encodes Opus, and sends authenticated packets to a desktop
server. The server validates the session, buffers and decodes packets, applies
gain/mute/downmix, and writes one float-PCM stream to a virtual-microphone backend.

For setup and commands, see [DEVELOPMENT.md](DEVELOPMENT.md). For runtime behavior,
see [OPERATIONS.md](OPERATIONS.md).

## System flow

```text
Phone microphone
  -> platform capture (Oboe / AVAudioEngine / AudioRecord)
  -> native pipeline: RNNoise -> Speex AGC -> gain/mute/limiter -> Opus
  -> paced Opus audio packet (AES-256-GCM by default, plain when the server selects it)
  -> UDP or TCP-framed tunnel
  -> server Hello/session admission
  -> header/session/replay validation -> AES-GCM open
  -> adaptive jitter buffer -> Opus decode/PLC
  -> gain -> local gain -> mute -> optional downmix
  -> single-writer backend (monitor / PipeWire / Windows driver / macOS HAL)
```

Discovery is a separate control plane: mDNS and UDP beacons advertise the server;
the client reconnect ladder finds candidates, then the pinned-key handshake proves
identity.

## Component map

| Area | Primary code | Responsibility |
|---|---|---|
| Native C ABI | `native/rasamic-native/include/rasamic_native.h` | Stable capture/pipeline/monitor boundary |
| Native pipeline | `native/rasamic-native/src/pipeline.cpp` | PCM accumulation, DSP, Opus encode, packet ring |
| Managed interop | `src/RasaMic.Audio/NativeMethods.cs`, `AudioPipeline.cs` | Library loading, callbacks, encode/decode wrappers |
| Protocol | `src/RasaMic.Protocol/` | Constants, messages, framing, pairing, crypto, replay window |
| Server | `src/RasaMic.Server/RasaMicServer.cs` | Listener, Hello admission, audio loop, telemetry |
| Jitter | `src/RasaMic.Core/Jitter/JitterBuffer.cs` | Reorder, adaptive depth, gap/PLC signaling |
| Discovery/reconnect | `src/RasaMic.Core/Discovery/`, `src/RasaMic.Client.Core/ServerFinder.cs` | Advertise, browse, remembered endpoints, subnet probe |
| Client session | `src/RasaMic.Client.Core/ClientSession.cs` | Handshake, audio sealing, send path |
| Mobile UI/lifecycle | `src/RasaMic.Client/MainPage.xaml.cs` | Connect/Live states, permissions, capture/sender lifetime |
| Desktop backends | `src/RasaMic.Server/Backends/` | Monitor, PipeWire, Windows IOCTL, macOS shared ring |
| CLI/GUI | `src/RasaMic.Server.Cli/`, `src/RasaMic.Server.App/` | Headless and Avalonia front ends over the same engine |

## Native capture and encode

The C ABI is defined in
[rasamic_native.h](../native/rasamic-native/include/rasamic_native.h#L19-L84).
Platform capture is selected in
[CMakeLists.txt](../native/rasamic-native/CMakeLists.txt#L82-L105): Oboe on Android,
AVAudioEngine on iOS, miniaudio elsewhere.

The pipeline in
[pipeline.cpp](../native/rasamic-native/src/pipeline.cpp#L70-L219) has these
contracts:

- Fixed 48 kHz PCM16 input; mono or interleaved stereo.
- Encoder frames are 10 or 20 ms; arbitrary feed sizes are accumulated safely.
- `rm_pipeline_feed` counts int16 values, not frames.
- Processing order: RNNoise -> Speex AGC -> gain/mute -> soft limiter -> Opus.
- A 64-slot × 512-byte SPSC ring decouples capture from the sender; a full ring
  drops a new encoded frame and increments a counter.
- Sequence and capture timestamp are assigned only after successful encoding.
- The sender configures Opus in-band FEC, but the current receive path uses PLC
  for gaps and does not decode FEC. Treat FEC recovery as open work, not a
  verified feature.

Native callbacks run on real-time audio threads. Do not allocate, block, take
managed locks, or perform expensive work there. The managed callback bridge is
[CaptureDevice.cs](../src/RasaMic.Audio/CaptureDevice.cs#L18-L55).

## Protocol and security boundary

Protocol v2 constants are in
[ProtocolConstants.cs](../src/RasaMic.Protocol/ProtocolConstants.cs#L3-L18):

- TCP and UDP audio/control: 47300; beacon: 47301; mDNS: `_rasamic._udp`.
- Control frames: JSON behind a big-endian uint16 length, maximum 16 KiB.
- Audio header: 24 bytes, little-endian; encrypted mode appends a 16-byte GCM tag, plain mode sends Opus directly after the header.
- Nonce: `sessionId || sequence || direction`; direction values are part of the
  wire contract.

Pairing uses a persistent P-256 server identity plus ephemeral ECDH, HKDF-derived
control/audio keys, and a six-digit confirmation code. The client exchange is in
[ClientSession.cs](../src/RasaMic.Client.Core/ClientSession.cs#L39-L85); the server
side is in [RasaMicServer.cs](../src/RasaMic.Server/RasaMicServer.cs#L90-L172).
A pasted pairing payload pins the static key and rejects a changed `HelloAck.StaticPub`.
After `PairConfirm`, the server sends `SessionReady` with an HMAC-SHA256 proof
bound to the control key, session ID, and encryption mode. The client verifies it
before creating the sender, so a modified HelloAck cannot downgrade audio to plaintext.

**Current boundary:** audio is encrypted and authenticated by default. The PC
server can select plain audio with `--plain` or the GUI **Encrypt audio** toggle;
`HelloAck.AudioEncrypted` carries that decision to the client. Plain mode removes
both confidentiality and authentication and is intended only for trusted LANs.
Post-handshake control messages remain plaintext JSON in both modes. `CaptureTsUs`
is transmitted but not used for server scheduling. `Resume` and some
remote-setting messages exist in the schema without complete session flows. Do
not claim end-to-end encryption of all control traffic.

## Session admission and audio loop

The server listens in
[RasaMicServer.cs](../src/RasaMic.Server/RasaMicServer.cs#L63-L88). A client claims
the single active slot only after a valid Hello, so bare TCP probes cannot evict a
real session. A second valid client receives `Busy`.

The audio loop in
[RasaMicServer.cs](../src/RasaMic.Server/RasaMicServer.cs#L174-L384) performs:

1. Start the selected backend at 48 kHz.
2. Drain available UDP datagrams or bounded TCP-framed audio without blocking.
3. Validate magic, version, type, session ID, replay window, and the GCM tag when encrypted.
4. Push accepted packets into the jitter buffer.
5. Decode at most one packet/PLC frame per real-time playout deadline.
6. Apply client gain, local gain, mute, and optional stereo-to-mono downmix.
7. Write synchronously to the backend and publish 1 Hz telemetry.

The sender and server both pace to the pipeline frame duration. Bursts are
buffered, never decoded/written as a fast burst. Preserve non-blocking telemetry:
a pending control write must not stall audio.

## Jitter and loss behavior

[JitterBuffer.cs](../src/RasaMic.Core/Jitter/JitterBuffer.cs#L15-L134) starts at
the first authenticated sequence, reorders arrivals, increases depth on late
packets, rebuffers after underrun, and shrinks after one stable second. The default
adaptive target is capped at four packets (40 ms at 10 ms frames). Missing
sequences produce a gap result; the server calls Opus PLC rather than waiting for
late data indefinitely. A sequence jump larger than 32 packets after more than
100 ms without input resynchronizes to the new stream instead of generating a
large PLC burst.

## Discovery and reconnect

Advertisers are
[Beacon.cs](../src/RasaMic.Core/Discovery/Beacon.cs#L28-L58) and
[Mdns.cs](../src/RasaMic.Core/Discovery/Mdns.cs#L7-L29). The reconnect ladder in
[ServerFinder.cs](../src/RasaMic.Client.Core/ServerFinder.cs#L15-L103) tries:

1. Remembered IPs.
2. mDNS entries matching the pinned server ID.
3. Beacon entries matching the pinned server ID.
4. IPv4 `/24` probe, excluding the device's own addresses.
5. Jittered 0.5 -> 8 second backoff.

Discovery is only a candidate finder. The pinned handshake establishes identity.
Android browsing holds a multicast lock. Stale `adb reverse` tunnels can make the
phone's own listener appear as a server; keep the own-address exclusion and remove
tunnels after testing.

The supervisor sends Ping every two seconds and disposes after five seconds
without Pong. Its control read intentionally has no timeout so a watchdog cannot
cancel a partial frame and desynchronize a TCP tunnel.

## Mobile lifetime

### Android

- `MainPage` owns capture, pipeline, sender, and supervisor state.
- `MicForegroundService` is a notification/wake-lock lifetime guard, not the
  capture owner. It starts on `connected` and stops on disconnect.
- Backgrounding stops discovery; capture/send continue.
- Oboe requests low-latency exclusive PCM16 capture without rate/channel fallback.
- `AudioRecordCapture` is an optional mono 48 kHz alternative for mic-direction
  selection; it uses a blocking reader thread.
- Stereo exists only on the Oboe path. The sender paces queued packets to the
  pipeline frame duration and flushes stale complete packets on reconnect.
  Disconnect order is service/session/sender, then capture, pipeline, and supervisor.

Key files: [MainPage.xaml.cs](../src/RasaMic.Client/MainPage.xaml.cs#L213-L364),
[MicForegroundService.cs](../src/RasaMic.Client/Platforms/Android/MicForegroundService.cs#L9-L69),
[capture_android.cpp](../native/rasamic-native/src/capture_android.cpp#L42-L85).

### iOS

- `AVAudioEngine` tap is converted to mono float32 at 48 kHz.
- Measurement mode disables Apple's AGC/noise processing.
- Preserve interruption recovery, route-change tap reinstallation, and optional
  polar-pattern handling.
- USB uses a usbmuxd tunnel; the PC dials the phone.

Key files: [capture_ios.mm](../native/rasamic-native/src/capture_ios.mm#L23-L157)
and [MainPage.xaml.cs](../src/RasaMic.Client/MainPage.xaml.cs#L249-L266).

## Desktop backend contract

[IVirtualMicBackend.cs](../src/RasaMic.Server/IVirtualMicBackend.cs#L8-L15)
defines `Start`, `Write`, and `Stop`. `Write` is called from the decode thread.
Backends are single-writer and expose whether they support stereo.

| Backend | Channels | Mechanism | Important invariant |
|---|---:|---|---|
| Monitor | 1 or 2 | Native miniaudio SPSC float ring | 2,400-sample cap (50 ms mono at 48 kHz); zero-fill underrun; drop newest when full |
| External cable | 1 or 2 | Miniaudio WASAPI playback into an installed cable | Prefer stable endpoint IDs; target app selects the matching capture endpoint |
| PipeWire | 1 or 2 | Pulse null-sink/remap-source, then `pw-loopback` | Clean up module IDs or child process |
| Windows driver | 1 | User-mode IOCTL -> kernel SPSC ring | Feed is 48 kHz stereo int32; duplicate mono; flush on close/pause/stop |
| macOS HAL | 1 | Shared-memory ring read by plugin | Identical layout, monotonic positions, release/acquire, silence on underrun |

The external-cable backend uses the same bounded native monitor ring. The GUI
can wrap a virtual-microphone backend in `SelfMonitoringBackend` to add independent
PC speaker playback. Windows device
enumeration exposes WASAPI endpoint IDs as UTF-8 through a synchronous callback;
exact-ID opening avoids ambiguous name matching. Rasa never owns the cable
driver lifecycle or changes system defaults.

Windows details: [WindowsDriverBackend.cs](../src/RasaMic.Server/Backends/WindowsDriverBackend.cs#L12-L107),
[rasamic_feed.h](../drivers/windows/Source/Inc/rasamic_feed.h#L10-L53), and
[rasamic_feed.cpp](../drivers/windows/Source/Main/rasamic_feed.cpp#L133-L249).
The kernel ring is 256 KiB, power-of-two, non-paged, and cache-line separated.
The driver is PortCls WaveRT/KMDF, not ACX.

macOS details: [MacHalBackend.cs](../src/RasaMic.Server/Backends/MacHalBackend.cs#L8-L47),
[RasaRing.cs](../src/RasaMic.Server/Backends/RasaRing.cs#L9-L103), and
[rasa_ring.h](../drivers/macos/RasaMicHalPlugin/rasa_ring.h#L1-L40).
The HAL exposes fixed 48 kHz mono float32 and discards oldest data after lapping.

The shared [DriftSlipPolicy.cs](../src/RasaMic.Server/Backends/DriftSlipPolicy.cs#L3-L56)
bounds clock correction. Multi-client mixing must happen before the backend; never
turn a backend into a mixer.

## Startup and persistent identity

The CLI argument/backend selection is in
[Program.cs](../src/RasaMic.Server.Cli/Program.cs#L7) and
[backend selection](../src/RasaMic.Server.Cli/Program.cs#L143). It defaults to monitor;
when multiple flags are supplied, precedence is cable -> driver -> PipeWire ->
HAL -> monitor. Cable mode validates the selected playback endpoint before the
listener starts. Discovery starts after the listener is ready and advertises the
actual bound port.

The Avalonia GUI uses the same engine and persisted identity. Closing the window
hides to tray; explicit shutdown stops the server, discovery, and hotkey resources.

[ServerIdentityStore.cs](../src/RasaMic.Server/ServerIdentityStore.cs#L5-L20)
persists the PKCS#8 identity. CLI and GUI must share it; regenerating it invalidates
existing client pins.

## Cross-cutting invariants

- One active audio session per server.
- No managed allocations on the steady-state audio hot path.
- Native callbacks remain non-blocking.
- Backend writes remain single-writer.
- Encrypted audio packets are authenticated before jitter/decode; plain mode is an explicit server choice with no authentication.
- Runtime Async is desktop `net11.0` only until mobile validation.
- The framing API uses the measured `ValueTask` outer / `Task` inner hybrid.
- AVX2 post-gain preserves scalar NaN/signed-zero/infinity semantics and keeps a
  non-AVX2 fallback; see [perf.md](perf.md).
- Never automate test-signing, reboot, driver install, or desktop UI QA.
