# Rasa design

Direction (unslop pass, 2026-11-19):

- **Product.** Rasa turns a phone into a low-latency LAN microphone for a PC — streamers, callers, presenters.
- **Character.** Calm, exact, a little warm.
- **World.** A broadcast desk at night: warm graphite surfaces, one amber tally light.
- **Moment.** The live status strip — tally dot plus mono telemetry. The only rich element on the screen.
- **Signature.** The tally dot: amber while live, clay while reconnecting, muted when idle. Only a mic product has one.

## Audit of the previous screen

Template MAUI palette (`#512BD4` purple, lavender dark buttons), OpenSans nobody chose,
literal `#30363d` strokes, opacity-based text hierarchy, mixed row patterns (Mute inline,
NS/AGC stacked), no icons, no tabular figures, magenta/midnight template styles, .NET
template app icon, duplicate Shell nav bar title.

## Palette

Warm graphite ramp tinted toward the amber world; one accent in the chrome; semantic
colors toned down. Every text pair checked WCAG AA (script: `.tools/gen-ui-assets.ps1`).

| Key | Light | Dark | Role | Contrast (light / dark, on surface) |
| --- | --- | --- | --- | --- |
| Primary | `#96660A` | `#E2A33C` | interactive, tally live, primary button (light) | 5.0 / 7.6 |
| Secondary | `#A4593C` | `#D98A6C` | tally reconnecting | 5.0 / 6.2 |
| Accent | `#C08A20` | `#E2A33C` | graphics only (3:1) | 3.0 / 7.6 |
| Background | `#F5F3EE` | `#171512` | page | — |
| Surface | `#FFFFFF` | `#201D19` | cards | — |
| Surface2 | `#ECE8E0` | `#282420` | fields, disabled | — |
| Border | `#DDD6CA` | `#322D26` | hairlines | — |
| TextPrimary | `#201D18` | `#EDEAE3` | body | 16.8 / 14.0 |
| TextSecondary | `#5C564B` | `#A69F92` | labels, status | 7.3 / 6.4 |
| TextMuted | `#6E675C` | `#948C7F` | hints, meta | 5.6 / 5.1 |
| Success | `#3E7A3E` | `#8FBE8F` | good state | 5.2 / 7.9 |
| Warning | `#96660A` | `#E2A33C` | caution | 5.0 / 7.6 |
| Error | `#B3402F` | `#E08573` | failure | 5.7 / 6.2 |

Dark primary button is off-white (`#EDEAE3`) with dark text (14.0:1); the amber lives in
the tally, slider, switches and pairing code instead.

## Type

IBM Plex Sans 400/500/600 for the interface, IBM Plex Mono for figures (stats, pairing
code, version). Hierarchy by size and color: 28 wordmark, 16 section, 14 body, 13 status,
12 hint, 24 mono pairing code. The wordmark uses the English product name "Rasa".

## Spacing, radii, icons

4px scale only (4/8/12/16/24/32). Cards 16, controls 10, chips 8; fields sit inside
10-radius wrappers. Settings rows are one 44px grid: 20px icon column, 8px gap, label,
switch right — icons, labels and switches each align in one column. Icons: FluentUI
System Icons Regular, 20px inline, TextSecondary; code points generated from
`FluentSystemIcons-Regular.json` into `Helpers/IconFont.cs` (never hand-guessed).

## Motion

Nothing loops. Button press is a 0.98 scale; the tally changes color with state. No
glows, no gradients, no entrance animations.

## Credits

- unslop skill by Damir Sokolovsky (<https://github.com/heydamir/unslop>), CC BY 4.0.
- FluentUI System Icons, MIT (license file in `Resources/Fonts`).
- IBM Plex Sans/Mono, SIL OFL (license file in `Resources/Fonts`).

## QA status

Release APK builds clean (0 warnings); every `StaticResource` key audited against the
dictionaries; installed on the Redmi Note 11. Light + dark screenshots captured
2026-11-19 after unlock (`.tools/q4-*.png`): both themes coherent, system bars match
the page (DayNight `rasaBar` + runtime re-assert), fields read as text + native
hairline underline tinted by `colorControlNormal`/`colorControlActivated`
(appcompat attrs; the underline cannot be removed reliably on Android, so it is
the field indicator instead of a second wrapper). Live state captured
(`.tools/q9-live.png`): amber tally dot + "Connected to rasa-accept", pairing code
in amber Plex Mono, Disconnect swap, mic-in-use indicator. Headless connect
verified end-to-end via `--es rasa_ip` (Shell-aware page resolution).
## Windows server GUI (2026-11-19)

Same direction and token values as the client, implemented for Avalonia:

- Tokens: `src/RasaMic.Server.App/Styles/Colors.axaml` (Light/Dark theme dictionaries);
  type/row/card classes in `Styles/Styles.axaml`. The whole Fluent chrome (slider, switch,
  scrollbars, focus) is recolored through `FluentTheme.Palettes` (`ColorPaletteResources`
  per theme). **Every palette key must be set** — unset keys fall back to the opposite
  theme's defaults, which showed up as light-beige disabled fields on dark.
- Disabled states are pinned with app-level brushes: `TextControl*Disabled` and
  `Button*Disabled` (Surface2 fill, TextMuted text), matching the client's disabled look.
- Fonts embedded in `Assets/Fonts`. Family names as the files parse them: `IBM Plex Sans`,
  `IBM Plex Sans Medm`, `IBM Plex Sans SmBld`, `IBM Plex Mono`, `FluentSystemIcons-Regular`
  (Medium/SemiBold are separate families, not weight instances).
- Icons: `Helpers/IconFont.cs`, generated by `.tools/gen-server-icons.ps1` from the official
  Fluent JSON (same rule as the client: never hand-guessed).
- Tally lamp: `MainWindowViewModel.TallyBrush` — amber with a live session, clay while
  listening, muted when stopped; refreshed by the 1 Hz timer and on theme change.
- Descriptive content added: numbered pairing steps (same Wi-Fi → scan pins the key →
  talk), QR on a white tile for scan contrast, copy-payload button, manual-IP fallback
  hint, per-card hints, `Output: <backend>` line, tray behavior footer.
- Server card shows the PC's LAN IPv4 (virtual adapters filtered out, Copy button) and
  an Output selector (Monitor / Rasa Mic driver / PipeWire / HAL per OS) — the GUI could
  previously only reach monitor mode even with the driver installed.
- Tray + window icon: the amber broadcast mark (dot + two arcs, same geometry as the
  client SVG), drawn by `.tools/gen-tray-icon.ps1`; replaces the purple template mic.

QA: headless tests render with real Skia — `TestAppBuilder` uses `.UseSkia()` and
`UseHeadlessDrawing = false`, because the default headless font stub names every embedded
face "Default" and cannot resolve avares families. xUnit parallelization is disabled in
this assembly: the global mute-hotkey hook is process-wide and parallel classes race its
teardown. Evidence: `.tools/gui-light.png`, `gui-dark.png`, `gui-live-light.png`,
`gui-live-dark.png` (stopped + listening, both themes); 17/17 GUI tests, 135/135 overall;
Debug and Release builds 0 warnings / 0 errors.

## Client information architecture (2026-11-19, second pass)

The first M9 pass restyled a single scrolling column; user feedback called it a "long
strip". Rebuilt as two states sharing one status strip (tally + status + pairing code):

- **Connect** — discovered-server cards (name, `ip:port`, Connect button), manual IP and
  payload fields, other transports (USB/Bluetooth/Wi-Fi Direct) inside an expander,
  Connect button.
- **Live** — mute, gain, quality, 1 Hz stats; NS/AGC/polar/direction/stereo folded into an
  "Audio processing" expander; Disconnect.

Discovery: `ServerBrowser` (Client.Core) runs `BeaconListener` + `RasaMdnsBrowser`,
deduped by server id; `Found` fires once per new server or endpoint change (tested:
`ServerBrowserTests`). Paired servers (`KnownServerStore`) reconnect silently once per
appearance using the pinned payload; unknown servers wait for the user's tap — pairing
still pins the key, discovery only proposes. Android holds a WifiManager multicast lock
while browsing (`CHANGE_WIFI_MULTICAST_STATE` added to the manifest); iOS reuses the
existing NSBonjourServices + local-network declarations. Browsing stops while live or
backgrounded. The stats strip polls `SessionSupervisor.LastStats` at 1 Hz.

QA (2026-11-20, Redmi Note 11, unlocked): `.tools/overhaul-connect.png` (Live state reached
by silent reconnect to the remembered server: amber tally, pairing code, 100 pkt/s stats),
`.tools/overhaul-found.png` (Connect state: "Servers on your network" card for rasa-accept
at 192.168.1.5:47300 with Connect prompt, manual fields, collapsed transports),
`.tools/overhaul-live-expanded.png` (tapped the found card → new session + code; Audio
processing expanded with NS/AGC on), `.tools/overhaul-live-light.png` (light theme live).
Two on-device crashes found and fixed during QA: page-level `Resources["Field"]` (app
styles live in Application.Resources; the client has no Field key at all — rows are built
inline from theme tokens). Suite 137/137; GUI tests 17/17 three consecutive runs after
gating HotkeyService dispose on SharpHook HookEnabled.
