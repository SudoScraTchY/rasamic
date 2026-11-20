# Multi-client evaluation (M7)

Status: evaluated and live-verified, mixing not implemented. v1 deliberately
serves one active audio session; a second client gets
`Error("Busy: another device is connected")`.

## What exists today

- Single-slot gate: `Interlocked.CompareExchange(ref _activeSession, 1, 0)`
  after Hello validation (`RasaMicServer.cs`), so bare TCP probes and the
  reconnect ladder never evict a real client. The slot is released in a
  `finally` on any disconnect.
- Everything else is already session-local: ECDH keys, `OpusDecoder`,
  `JitterBuffer`, `ReplayWindow`, control/audio queues live inside
  `AudioLoop`. The audio header carries a random `sessionId` and the loop
  already rejects frames with a foreign id.
- Shared mutable state is only the slot flag and the backend. Backends are
  single-writer by design (mmap ring, IOCTL feed, PipeWire stream).

## What multi-client would take

1. **Session registry** — replace the CAS gate with a capped concurrent
   registry (suggest 4). `Busy` becomes the over-capacity answer; no wire
   change.
2. **Mix before the backend** — each session decodes to 48 kHz mono float;
   a mixer sums per-session buses (per-session gain already arrives via
   `SetGain`) and one writer feeds the backend. Mixing inside the backend
   would break the single-writer ring contract.
3. **Clock domains** — every phone drifts independently. The existing
   `DriftSlipPolicy` handles one writer against one reader; multi-client
   needs a per-session slip (or SpeexDSP resample, already linked) against a
   fixed 10 ms mix cadence. This is the risky part and the reason to keep it
   out of v1.
4. **UDP demux** — today each session binds its own `UdpClient` to the server
   port. With N sessions, use one shared receiver dispatching by `sessionId`
   (already in every audio header) into per-session jitter buffers.
5. **Control semantics** — per-session mute/gain stay per-session; the global
   hotkey remains a master mute after the mix.

## Live verification (2026-11-18, Windows, rasamicd --monitor)

Three sequential loopback clients against one server:

- **A** held the session 12 s: 64 pkt/s, 0.0% loss, jitter ~5.7 ms, RTT 15 ms.
- **B** connected while A was active: clean refusal
  `Server refused the connection: Busy: another device is connected`,
  exit 1, no crash. A kept streaming unaffected.
- **C** connected after A's `Bye`: paired normally (code displayed), exit 0.

Regression coverage: `MultiClientTests` (IntegrationTests) — Busy refusal
keeps the first session, and the slot frees for the next client after
disconnect. The client surfaces the server's `Error.Message` instead of a
generic "Expected HelloAck" protocol exception.

## Recommendation

Keep v1 single-session (WO Mic is single-client too). The protocol is already
forward-compatible — `sessionId` is on the wire and `Busy` is the natural
capacity response — so a post-1.0 "party mic" mode can add the registry,
mixer and UDP demux without breaking clients. Estimated effort: medium
(2–3 days), dominated by the per-session drift handling and its tests.