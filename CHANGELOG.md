# Changelog

All notable changes to Monitor Anchor. The release workflow uses the section for the tagged version as the
release notes, so keep the heading format `## X.Y.Z - YYYY-MM-DD`.

## 2.4.0 - 2026-09-20

**New**
- **Display link retrain detection.** Several display-change events with the same monitors in a short burst,
  or one together with the GPU's HDMI audio endpoint bouncing, are logged as "Display link retrained N time(s)"
  with the link details at that moment, counted across restarts and shown in diagnostics. This is what a
  flickering or blanking HDMI/DisplayPort link looks like from Windows.
- **System and link facts** at startup and in diagnostics: machine name, CPU, RAM, Windows build, uptime,
  power-plan display-off and sleep timeouts, every GPU with driver version and date, and per monitor the
  connector type, colour format, bits per channel, exact refresh timing (119.880 vs 120.000) and pixel clock.
  Two machines' dumps can now be compared line by line.
- GPU audio endpoint churn no longer counts toward the dock threshold in KVM detection.

## 2.3.0 - 2026-09-20

**New**
- **Presence in the log.** A line when input stops for five minutes ("away, no input since 03:52"), a line when it
  resumes ("back after 5 h 10 min away"), and a heartbeat every half hour, so the log says whether anyone was at
  the machine when something happened.
- **Display power in the log.** "Displays turned off/on/dimmed by Windows" whenever Windows changes the
  console display state, plus session lock/unlock/connect lines.
- **Human input is told apart from injected input.** Low-level hooks see the "injected" flag on synthetic
  events, so the app's own jiggles, tours and F15 presses no longer count as the user being present, and the
  idle keep-alives no longer reset their own idle clock. Diagnostics reports human idle versus system idle and
  counts injected input from other software.

## 2.2.0 - 2026-09-20

**New**
- **Window memory.** While the layout is intact, the app remembers where every window sits. After a monitor comes
  back and the layout is restored, windows that Windows shoved onto another screen are moved back to their
  remembered place, maximised state included. This covers the cases Windows' own "remember window locations"
  misses. Off by default; toggle *When a monitor returns, move windows back*.
- Unit tests for the pure logic (layout selection, KVM classification, EDID parsing, log trimming, scale
  index maths), run by the CI pipeline.
- `LICENSE` (MIT) and this changelog.

## 2.1.0 - 2026-09-20

**New**
- **MSIX package** (`MonitorAnchor.msix`) alongside the exes, with a Start Menu entry, clean uninstall and startup
  managed by Windows. Signed with the project's certificate; install `MonitorAnchor.cer` into Trusted People once.
- **Built by CI.** Every release comes out of GitHub Actions from the tagged commit, with checksums and winget
  manifests attached.
- Diagnostics shows where the app runs from (installed, packaged, or elsewhere).

## 2.0.0 - 2026-09-19

**New**
- **Multiple layouts.** One saved layout per set of monitors; the right one applies automatically based on which
  monitors are connected. Layouts submenu to apply, rename or delete. The existing profile is imported.
- **Display scaling** captured and restored per monitor.
- **Install to Programs folder** (`--install` / `--uninstall`), offered once when run from Downloads, the desktop or
  a temp folder.
- Monitors named by their real model instead of "Generic PnP Monitor".

## 1.9.1 - 2026-09-19

**Fixed**
- The Close button on the About window.

## 1.9.0 - 2026-09-19

**New**
- About window with version, build flavour and links to GitHub, releases and the blog.

## 1.8.0 - 2026-09-19

**Changed**
- Idle behaviours grouped into a *When idle* submenu: mouse choice (leave alone / nudge / hover over every window),
  a separate Remote Desktop poke, and the idle threshold.

## 1.7.0 - 2026-09-19

**New**
- Hover the mouse over every visible window when idle.
- Windows Update submenu: pause for 1 week to 1 year, or resume (asks for administrator approval).
- `--tour` runs one mouse tour.

## 1.6.0 - 2026-09-19

**New**
- Keep Remote Desktop sessions alive when idle: each session window (mstsc, the Windows App, hosts like mRemoteNG)
  is focused in turn, the mouse nudged over it and F15 pressed.
- Diagnostics lists Remote Desktop windows; `--classes <text>` dumps window classes.

## 1.5.0 - 2026-09-19

**New**
- KVM detection: monitor and USB input-device changes are correlated and logged as KVM switches (including
  USB-only switches) or dock events; counts and an EDID pass-through check appear in diagnostics.
- Log viewer "Always on top" checkbox.

## 1.4.0 - 2026-09-19

**New**
- Live log viewer that tails the file and auto-scrolls.
- Log retention: two days, 2 MB cap.

## 1.3.0 - 2026-09-19

**New**
- New icon (monitor with an anchor), diagnostics window, reorganised tray menu, README with diagrams and screenshots.

## 1.2.0 - 2026-09-19

**New**
- Automatic updates from GitHub releases, verified against the release's size and SHA-256 digest.
- Fake monitor delay presets.
- Idle mouse jiggler.
- `--dump` shows each monitor's decoded EDID.

## 1.1.0 - 2026-09-19

**New**
- Optional fake monitors (Parsec Virtual Display Driver) standing in for unplugged monitors; driver installer action.
- Window position logging around display changes.

## 1.0.0 - 2026-09-19

First release: learns and enforces per-monitor resolution, refresh rate, position, orientation, primary flag and
HDR; re-enables monitors Windows left switched off; keep-awake; starts with Windows.
