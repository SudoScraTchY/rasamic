# Rasa performance notes

Method: measure first, optimize second, re-measure and record before/after.
Microbenchmarks: `tools/RasaMic.Benchmarks` (BenchmarkDotNet 0.14, MemoryDiagnoser).
Pipeline latency: `tools/RasaMic.LatencyProbe` (paced 10 ms PCM, tone bursts, feed→backend-write).
Acoustic end-to-end: LatencyProbe mode b (PC click → phone → stream back → cross-correlation), see README.
CI gates: `.github/workflows/perf.yml` — zero-allocation test, probe p95 < 50 ms, bench suite.

## LAN default profile

48 kHz stereo, Opus VBR 256 kbps, 10 ms frames, FEC on, adaptive jitter 10–20 ms.
Robust profile only under loss. Impulse targets: ≤30 ms Wi-Fi LAN, ≤20 ms wired/USB.
Pipeline-only probe (localhost, 30 bursts): median 15.5 ms, p95 32.0 ms, max 35.3 ms —
PASS at the 50 ms gate. Capture and playback add platform latency on top.
WAV mode (`rasamic-latencyprobe 30 50 out.wav`) cross-correlates the fed burst against
the backend's PCM for sample-accurate numbers: first-burst feed→write 20.46 ms (the
initial 2-packet rebuffer), sub-ms precision; steady-state bursts ride the ~16 ms median.

## Per-stage budgets and first measurements

Host: i3-12100F (AVX2), Windows 10 19045, .NET 11.0.0, BDN default job.
These are the committed baseline (`docs/perf-baseline.json`); `perf.yml` fails >10%
mean regression via `tools/perf-compare.ps1` (merge + compare; self-tested pass and
fail paths). Baseline is reference-host hardware — per-runner baselines are open work.

| Stage | Benchmark | Mean | p99 budget |
|---|---|---:|---:|
| capture→encode, mono 32k, NS+AGC | Feed_10ms_Mono_32k | 0.66 ms | 3 ms |
| capture→encode, stereo 256k, NS+AGC | Feed_10ms_Stereo_256k_NsAgc | 1.30 ms | 3 ms |
| Opus decode 10 ms | Decode_10ms | 10.1 µs | 1 ms |
| Opus PLC | Plc_10ms | 0.27 µs | — |
| AES-256-GCM seal / open, 320 B | Seal_320B / Open_320B | 0.28 / 0.30 µs | 0.3 ms |
| SIMD post-gain+limiter, 480 / 960 floats | Gain_480 / Gain_960 | 0.12 / 0.24 µs | 0.2 ms |
| jitter push+next (steady / 10% loss) | PushNext_* | 34–69 ns | — |

Every row reports 0 B allocated. `HotPathAllocTests` additionally asserts zero managed
allocations across 60 s of Feed/ReadPacket on the client hot path.

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

Stats (1 Hz) now carries p95 decode, jitter dwell, and backend write-gap per second;
the CLI ticker prints them (`dec … dwell … wr …`) and soak logs capture them.
First live Wi-Fi session: wr p95 11.7–15.7 ms (10 ms frame pacing + scheduling),
dwell p95 ≈ 0 ms — the adaptive buffer shrinks 2→1 packets after 5 s stable, so on a
clean LAN it adds almost nothing; dec < 0.05 ms (bench: 10 µs). 131/131 tests on
Windows and Linux with telemetry in place.

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