# Windows virtual-mic feed — recon notes (M2)

## External-cable alternative

Windows users can avoid the project driver and test-signing entirely by
installing [VB-CABLE](https://vb-audio.com/Cable/) and running
`rasamicd --cable`. Rasa writes to **CABLE Input**; applications record from
**CABLE Output**. The GUI provides endpoint selection and `rasamicd doctor`
checks both sides. Rasa does not bundle, extract, install, or remove the vendor
driver. See [OPERATIONS.md](OPERATIONS.md#windows-external-cable).

The notes below document the project's own driver path for a branded Rasa Mic
endpoint and remain independent of the external-cable backend.

Read-only inspection of two sources, per plan:

1. Our fork base: `drivers/windows` = clone of [VirtualDrivers/Virtual-Audio-Driver](https://github.com/VirtualDrivers/Virtual-Audio-Driver) (MIT).
2. WO Mic client 6.4 installer (`WOMicClientSetup6.4.exe`, NSIS; driver 2.1.0.0 per site, INF says 1.5.0.0 2017-05-06), extracted to `.tools/womic-recon/` (gitignored, never redistributed).

## WO Mic driver findings (inspiration)

- `driver/womic.inf`: legacy **WDM/KS portcls** driver, SYSVAD-derived. PDB string in `womic.sys`:
  `...\07_MicDevice\4_src\micdevice\micarray\x64\Win7Release\vadarray.pdb` — built from the WDK **micarray (SYSVAD)** sample, Win7 release config.
- Root-enumerated hardware ID `*WOMic`; installed with bundled **devcon.exe** (no pnputil); x64 only (`ntAMD64`); service `wovad_micarray`, StartType=3 (demand).
- Interfaces: KSCATEGORY_AUDIO + KSCATEGORY_CAPTURE ("Wave") and topology; friendly name "WO Mic Device".
- `womic.sys` imports `IoCreateDevice`/`IoDeleteDevice` and contains UTF-16 strings **`\Device\WOMic`** and **`\DosDevices\WOMic`** → WO Mic creates a **control device `\\.\WOMic`** alongside the audio miniport; the client feeds PCM through it (IOCTL). This validates our **IOCTL v1** plan.
- Installer also bundles `adb.exe` + AdbWinApi/AdbWinUsbApi → WO Mic USB mode = **adb tunnel**, matching our M6 adb-reverse-first decision.
- No event-driven mode opt-in, no ARM64, no float formats visible; driver is 2017-era. We improve on all of these.

## Fork base findings (Virtual-Audio-Driver)

**Not ACX** (plan assumption corrected): classic **PortCls WaveRT + KMDF** hybrid.
`WdfDriverCreate(WdfDriverInitNoDispatchOverride)` + `PcInitializeAdapterDriver` (`Source/Main/adapter.cpp:297-336`); `PcAddAdapterDevice` (`adapter.cpp:375-429`).

### Capture data path
- `CMiniportWaveRTStream::UpdatePosition` → **`WriteBytes`** (`Source/Main/minwavertstream.cpp:1333-1337`, body `1392-1422`).
- The fork **already replaced the tone generator with silence**: `WriteBytes` just `RtlZeroMemory`s the DMA buffer (line 1417). `ToneGenerator::GenerateSine` (`Source/Utilities/ToneGenerator.cpp:147`) is dead code; `g_DisableToneGenerator=1` (`adapter.cpp:52`).
- Cadence: high-res `ExTimer` every **1 ms** in KSSTATE_RUN (`minwavertstream.cpp:1261-1267`); `TimerNotifyRT` (`1548-1650`) at DISPATCH under `m_PositionSpinLock`; `GetPosition` also calls `UpdatePosition` (`776-803`).
- Format: `DataRangeIntersection` **forces format[0]** for mic (`Source/Main/minwavert.cpp:248`): **48 kHz / 2 ch / 32-bit int PCM**, hard-locked in `Source/Filters/micarraywavtable.h:21-24,35-64,301-318`. Other listed formats (44.1/16, 48/16, 96/24, 192/24, 48/32-float) are ignored by intersection.

### Feed hook (our patch)
- Patch **`WriteBytes` (minwavertstream.cpp:1392)**: copy from a non-paged lock-free SPSC ring instead of zeroing; keep wrap + position bookkeeping (`m_ullLinearPosition`) intact. Runs at DISPATCH under spinlock → ring indices must be cache-line separated, no locks.
- **Format conversion**: mic endpoint is stereo 48k/32-bit; our stream is mono float32 48k. Convert in user mode (feed stereo int32 directly) for v1 — zero kernel DSP. Optionally widen `DataRangeIntersection` later.
- DMA buffer: `AllocateBufferWithNotification` (`minwavertstream.cpp:460-537`), packet size = `m_ulDmaBufferSize/m_ulNotificationsPerBuffer` (528-529). Event-driven mode opted in via INF (`PKEY_AudioEndpoint_Supports_EventDriven_Mode`, inx 64-80,126-130).
- Flush/reset ring on STOP/PAUSE in `SetState` (`1167-1277`).

### Control device
- **None exists.** Audio interfaces only (`IoRegisterDeviceInterface` KSCATEGORY_AUDIO, `Source/Main/common.cpp:1446`; refstring `WaveMicArray1`, `Source/Filters/minipairs.h:130`).
- Add a **WDF control device** (`WdfControlDeviceInitAllocate` + `WdfDeviceCreateDeviceInterface`, custom GUID; `EvtDeviceFileCreate`/`EvtIoDeviceControl` queue) created in DriverEntry/AddDevice → `\\.\RasaMicCtl`. WDF dispatch is otherwise disabled, so this does not conflict with portcls.
- v1: `IOCTL_RASA_WRITE_PCM` (METHOD_IN_DIRECT, 10 ms stereo-int32 chunks). v2: shared-memory section handle via IOCTL → MDL lock/map, refcounted teardown.

### INF rename targets (`Source/Main/VirtualAudioDriver.inx`)
- Hardware ID `ROOT\VirtualAudioDriver` (29) → `ROOT\RasaMic`; sections `VIRTUALAUDIODRIVER_SA*` (37-158); service (133-140); catalog/sys names (7,14); friendly names/Provider (187-201) → "Rasa Mic" / Rasa Contributors; keep event-driven opt-in + permissive SDDL (149). Min OS build 22000 (26).

### Build
- `build.bat`: VS2019/2022 MSBuild, x64 default; ARM64 disables InfVerif/ApiValidator/signing (112-127). `PlatformToolset=WindowsKernelModeDriver10.0`, KMDF 1.x (`Source/Main/Main.vcxproj:24-57`), no pinned WDK → **CI via EWDK or WDK NuGet** (`Microsoft.Windows.WDK.x64`), no local WDK needed. No upstream driver CI exists; we add `.github/workflows/driver.yml`.

### Gotchas
- Idle D3 after 3 s (inx 47-50) — feed stalls when endpoint idle; keep-alive or accept silence-on-idle.
- `WriteBytes` also runs from `GetPosition` at PASSIVE — ring must be safe at both IRQLs (non-paged, interlocked indices).
- Render path writes debug files unless `DoNotCreateDataFiles=1` (default 1, `adapter.cpp:51`).
- Underrun ETW at `minwavertstream.cpp:1611-1623` — reuse for our stats.

## Decisions confirmed by recon
1. IOCTL control-device feed v1 (WO Mic does exactly this with `\\.\WOMic`).
2. User-mode mono→stereo float→int32 conversion; kernel stays dumb.
3. adb-reverse USB first (WO Mic bundles adb for the same reason).
4. WDF control device, not ACX callbacks; fork is portcls/KMDF.
5. Test-signing + devcon/pnputil install; user enables testsigning + reboots (never automated).

## Implemented (v1 feed)

- `Source/Inc/rasamic_feed.h` — shared contract: interface GUID
  `{8B4E9D2A-3C61-4F7E-9A0D-2E5B6C8F1A47}`, `IOCTL_RASA_WRITE_PCM`
  (METHOD_IN_DIRECT, stereo int32 @48k in, `RASA_WRITE_RESULT` out),
  `IOCTL_RASA_FLUSH`, 256 KB ring capacity (~682 ms).
- `Source/Main/rasamic_feed.cpp` — global non-paged SPSC byte ring
  (cache-line-separated monotonic Head/Tail, acquire/release ordering),
  WDF control device created in `AddDevice` (non-fatal on failure: mic
  still enumerates, outputs silence), sequential IO queue, flush on
  file cleanup (feed process death) and on stream STOP/PAUSE.
- `minwavertstream.cpp` — `WriteBytes` drains the ring (silence on
  underrun); `SetState` flushes on capture STOP/PAUSE.
- `adapter.cpp` — `RasaFeedInit` after `PcAddAdapterDevice`,
  `RasaFeedCleanup` in `DriverUnload` after portcls unload.
- INF — hardware ID `ROOT\RasaMic`, friendly name "Rasa Mic", provider
  "Rasa Contributors"; event-driven opt-in and permissive SDDL kept.
- User mode: `src/RasaMic.Server/Backends/WindowsDriverBackend.cs`
  (SetupDi enumeration → CreateFile → IOCTL feed, mono float → stereo
  int32, reusable buffers, drop counter); `rasamicd --driver` selects it.
  The IOCTL result reports `RingFillBytes`; the backend runs the shared
  `DriftSlipPolicy` (20 ms fill target, ≤1-frame slip per 10 s, 1 s in
  emergency) — drop skips a frame, repeat re-sends the previous one.
- CI: `.github/workflows/driver.yml` (WDK via choco, x64 test-sign,
  ARM64 validation-off per build.bat). Remote snapshot 2026-11-26: the workflow
  exists but has zero runs; the pinned fork commit matches remote `main`.
- Install: `tools/scripts/install-driver.ps1` (self-signed cert, trust,
  signtool, pnputil + devcon). **User must enable testsigning + reboot
  first**; devcon.exe must sit next to the script or be on PATH (WDK tool).

Not verified locally: no WDK on this machine — kernel code compiles only
in CI. v2 shared-memory feed deferred until v1 passes Driver Verifier.