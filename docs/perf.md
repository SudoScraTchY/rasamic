# Rasa performance notes

Method: measure first, optimize second, re-measure and record before/after.
Microbenchmarks: `tools/RasaMic.Benchmarks` (BenchmarkDotNet 0.14, MemoryDiagnoser).
Pipeline latency: `tools/RasaMic.LatencyProbe` (paced 10 ms PCM, tone bursts, feed→backend-write).
Acoustic end-to-end still needs a physical phone/speaker loopback rig; server-side stage telemetry is not a substitute.
CI gates: `.github/workflows/perf.yml` — zero-allocation test, probe p95 < 50 ms, bench suite.
Hosted runners have variable scheduling latency even with zero packet loss. The latency
step therefore makes up to three attempts and passes only when an attempt meets the same
50 ms threshold; it never relaxes the gate.

## LAN default profile

48 kHz stereo, Opus VBR 256 kbps, 10 ms frames, FEC on, adaptive jitter target capped at 40 ms.
Robust profile only under loss. Impulse targets: ≤30 ms Wi-Fi LAN, ≤20 ms wired/USB.
Pipeline-only probe (localhost, 30 bursts), three consecutive corrected-cadence runs:
median 28.3–31.4 ms, p95 40.6–44.7 ms, max 46.6 ms — all PASS at the 50 ms gate.
The probe now drops stale cadence after a scheduling stall instead of replaying every
overdue frame; production capture callbacks do not replay missed frames. Earlier
catch-up-harness numbers are not comparable. Capture and playback add platform latency
on top.
WAV mode (`rasamic-latencyprobe 30 50 out.wav`) cross-correlates the fed burst against
the backend's PCM for sample-accurate numbers: first-burst feed→write 20.46 ms (the
initial 2-packet rebuffer), sub-ms precision; steady-state bursts ride the ~16 ms median.

## Current per-stage baseline — 2026-11-27

Host: i3-12100F (AVX2), Windows 10 19045, .NET 11.0.0, BenchmarkDotNet 0.14.0 DefaultJob.
The 22-case suite is recorded in `docs/perf-baselines/12th-gen-intel-r-core-tm-i3-12100f.json`.
`perf.yml` fails >10% mean regression via `tools/perf-compare.ps1`; per-runner baselines
are selected by `tools/perf-gate.ps1`. The GitHub-hosted AMD EPYC baseline is also committed,
so that runner now enforces comparisons instead of emitting only an advisory candidate.
Benchmark reports are attached as artifacts and published in the Actions step summary.

| Stage | Benchmark | Mean | p99 budget |
|---|---|---:|---:|
| capture→encode, mono 32k, NS+AGC | Feed_10ms_Mono_32k | 0.648 ms | 3 ms |
| capture→encode, stereo 256k, NS+AGC | Feed_10ms_Stereo_256k_NsAgc | 1.265 ms | 3 ms |
| Opus decode 10 ms | Decode_10ms | 9.968 µs | 1 ms |
| Opus PLC | Plc_10ms | 0.274 µs | — |
| AES-256-GCM seal / open, 320 B | Seal_320B / Open_320B | 0.273 / 0.294 µs | 0.3 ms |
| SIMD post-gain+limiter, 480 / 960 floats | Gain_480 / Gain_960 | 0.068 / 0.143 µs | 0.2 ms |
| jitter push+next (steady / 10% loss) | PushNext_* | 35–70 ns | — |

Every row reports 0 B allocated. `HotPathAllocTests` additionally asserts zero managed
allocations across 60 s of Feed/ReadPacket on the client hot path.

## Runtime Async, ValueTask and AVX2

Host: the same i3-12100F / Windows 10 19045 / .NET 11.0.0 AVX2 reference machine.
BenchmarkDotNet 0.14.0; comparative runs used ShortRun, final production runs used DefaultJob.
Local artifacts are under `.tools/perf-*` (ignored); the permanent cases are in
`tools/RasaMic.Benchmarks/Program.cs`.

**Runtime Async** is enabled centrally for desktop `net11.0` projects only:

```xml
<Features Condition="'$(TargetFramework)' == 'net11.0'">runtime-async=on</Features>
```

Android/iOS targets are deliberately excluded until their Runtime Async support is
validated on-device. Same-job framing comparison (Task, synchronous MemoryStream):
72.23 ns / 448 B before, 58.83 ns / 448 B after (~19% faster). Suspended reads moved
from 1,473.89 ns to 1,425.27 ns (~3% faster); allocations were unchanged.

**ValueTask uses a measured hybrid, not a blanket conversion.** `ReadRawAsync` / `ReadAsync`
return `ValueTask`, while the inner exact-read helper stays `Task`. Converting both layers
to ValueTask was slower under Runtime Async and was rejected. Final DefaultJob comparison
against the previous all-Task framing path:

| Path | Sync mean | Sync allocated | Suspended mean | Suspended allocated |
|---|---:|---:|---:|---:|
| Task outer + Task inner | 51.54 ns | 448 B | 1,296.18 ns | 1,047 B |
| ValueTask outer + Task inner | 52.27 ns | 376 B | 1,340.40 ns | 1,079 B |

The hybrid is 72 B lighter on synchronous completion and about 1.4% slower there;
suspended reads are about 3.4% slower (+44 ns, +32 B). Those deltas are negligible next
to network I/O, while the synchronous fast path avoids a Task allocation, so the hybrid
is retained for allocation behavior rather than a speed claim.

**Post-gain + limiter** now fuses multiply and clamp in one AVX2 pass for spans of at
least 8 floats. Comparisons + conditional selects preserve NaN, signed zero and infinity
semantics; the existing TensorPrimitives path remains for short spans and non-AVX2 CPUs.
Side-by-side ShortRun, 480 / 960 floats:

| Implementation | 480 mean | 960 mean | Allocated |
|---|---:|---:|---:|
| Scalar oracle | 406.94 ns | 738.76 ns | 0 B |
| TensorPrimitives, two passes | 123.56 ns | 240.90 ns | 0 B |
| Fused Vector128 | 154.19 ns | 316.70 ns | 0 B |
| Fused Vector256 | 88.78 ns | 169.06 ns | 0 B |

Final production DefaultJob: **67.89 ns / 143.12 ns**, 0 B allocated. Against the prior
committed baseline this is 0.57x / 0.59x (about 41–43% lower mean time). Vector128 was not
shipped because it lost to TensorPrimitives; AVX2 is guarded by
`Vector256.IsHardwareAccelerated`.

Validation: the current full solution passes **184/184** (24 Protocol, 51 Audio, 21 Core,
58 Integration, 30 Server.App). PCM contract tests pass 27/27 on AVX2, 27/27 with
`DOTNET_EnableAVX2=0`, and 27/27 with `DOTNET_EnableHWIntrinsic=0`, including NaN,
signed zero, infinities, non-zero-offset slices and vector-tail boundaries. iOS Release
build passes; Android Release build passes on 2026-11-27 with 0 warnings / 0 errors
when `ANDROID_HOME` and `ANDROID_SDK_ROOT` point to `M:\Android\Sdks` and JDK 17
is selected. The default `%LOCALAPPDATA%\Android\Sdk` lacks build-tools/aapt2, so
the SDK variables are required on this machine.

Reproduce the final cases:

```bash
dotnet build tools/RasaMic.Benchmarks/RasaMic.Benchmarks.csproj -c Release
dotnet run --project tools/RasaMic.Benchmarks/RasaMic.Benchmarks.csproj -c Release --no-build -- \
  --filter "*" --job default --noOverwrite --exporters json github
```

## Findings and fixes

- **Windows tone feed paced at ~64 pkt/s instead of 100.** `Thread.Sleep(10)` per 10 ms
  chunk rides the default 15.6 ms Windows timer grid. LoopbackClient now paces to a
  Stopwatch deadline (sleep-then-spin). Verified: 100–101 pkt/s, 0% loss. Phone capture
  was never affected (native RT callback).
- **mDNS loopback answers hairpinned reconnects through adb-reverse** (control TCP back
  into the PC, UDP audio blackholed). Browser now filters loopback A records; regression
  assert in MdnsDiscoveryTests. Reconnect ladder otherwise unchanged.
- **Subnet probe hit the phone's own adb-reverse listener.** With `adb reverse` left
  active, the /24 probe found the phone's own Wi-Fi IP (adbd binds the tunnel listener
  on-device), hairpinned into the PC, and the supervisor remembered the self-address.
  Probe now skips the client's own unicast addresses; regression test
  `Find_SubnetProbe_SkipsOwnAddresses`. Leftover test tunnels removed with
  `adb reverse --remove`.
- **USB control framing flap** came from cancelling pending NetworkStream reads on a
  watchdog timeout; the reader is now continuous with a separate ping/pong timer.

## Soak gates

10 min on-device + 30 min CI: loss <0.1%, jitter p99 <30 ms, phone PSS / server WS
growth <5%, no steady-state Gen2 GC, zero seq gaps.

2026-11-18 Wi-Fi soak (fixed APK, 11 samples over 10 min): 11/11 zero-loss, ~100 pkt/s,
jitter 0.8–2.2 ms; phone PSS 425.9→379.6 MB (no growth), server WS 56.9→56.4 MB (flat). PASS.
Server-restart recovery on the same APK: unattended Wi-Fi reconnect within seconds of
the listener coming up, ~100 pkt/s 0% loss (both hairpin traps fixed).

## Live per-stage telemetry

Stats (1 Hz) carries p95 decode, jitter dwell, backend write-gap, monitor queue,
output drops, and native output-device buffer. The CLI ticker prints
`dec … dwell … wr … out … dev … drops …` and soak logs capture them.

2026-11-27 paced Android -> Windows Wi-Fi run (plain mode, Redmi Note 11):
~100 pkt/s, 0% loss, ~10 ms RTT, 10 ms write interval, 20–70 ms jitter dwell,
zero output drops, and a 22 ms WASAPI output-device buffer. The adaptive jitter
target is capped at 40 ms and the miniaudio queue at 2,400 samples (50 ms mono at
48 kHz). Evidence: `.tools/rasamic-v2-final.log` and
`.tools/rasamic-v2-final.png` (ignored).
This is server-side stage evidence, not an acoustic round-trip measurement.

## Quality gates

Mono bitrate sweep + RNNoise A/B: `QualityGateTests` (table in README).
LAN default stereo 256k: L 31.6 dB / R 34.0 dB SNR, THD ≤0.05% (per-channel
best-SNR delay alignment). Opus stereo coupling gives tonal content a
per-channel group-delay offset; plumbing verified sample-identical with
identical channels (skew 0, L−R rms 0). Voice content is the design target.

## On-device (arm64) first numbers

Redmi Note 11 (SD680), intent-triggered bench (`--ei rasa_bench 1`, logcat
tag RasaBench): unboosted managed thread mono 21.2 ms / stereo 41.8 ms per
10 ms frame. NOT representative of production: capture runs on the Oboe
LowLatency/Exclusive RT thread whose AAudio stream holds the HAL perf lock,
and live sessions sustain 100 pkt/s 0% loss. Urgent-audio thread priority
alone changes nothing (mono 20.8 ms); an ADPF PerformanceHintManager session
is unavailable on this device (hint=False). 2026-11-19: idle re-run
20.6/41.0 ms with `capture-active=False` (keyguard blocked the session);
awake foreground re-run 4.0 ms mono / 7.8 ms stereo — SoC power state dominates
the unboosted managed-thread number (5x vs doze). The bench intent is also
delivered via `OnNewIntent` mid-session. **Capture-active (2026-11-19,
mic foreground service live, `capture-active=True`): mono 4.4 ms / stereo
8.2 ms per 10 ms frame** — inside the frame budget even on an unboosted
managed thread; the production Oboe RT thread with the HAL perf lock is
faster still, and live sessions sustain 100 pkt/s 0% loss. The 21/42 ms
first numbers were a doze-state artifact, not a pipeline bottleneck.

## Open work

- Per-runner CI baselines: DONE 2026-11-19 — `tools/perf-gate.ps1` picks
  `docs/perf-baselines/<cpu-slug>.json` (reference slug in
  `docs/perf-reference-slug.txt`, currently this i3-12100F host); unknown runners
  merge a `candidate-baseline.json` artifact and pass advisory until a human
  promotes it into `docs/perf-baselines/`. Both branches self-tested locally
  (reference 1.00x all ok; unknown-runner merge pass).
- Capture-active arm64 bench: DONE 2026-11-19 (numbers above).