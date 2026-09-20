# Monitor Anchor

A tiny Windows tray utility that pins your monitor layout. On first launch it learns the layout you
have right now; rearrange and click **Persist current layout** whenever you want to update it. From
then on the app puts every monitor back to that resolution, refresh rate, position, orientation and
HDR state whenever Windows shuffles things around: monitor plug/unplug, dock/undock, sleep/resume,
sign-in, or a game/driver changing the mode. If a saved monitor is plugged in but Windows left it
switched off, the app switches it back on. It can also hold the screens awake.

It starts with Windows automatically.

## Build

Requires the .NET 10 SDK (framework-dependent build, so the .NET 10 Desktop Runtime must be present
on the machine that runs it).

```
dotnet publish -c Release -o publish
```

Produces `publish\MonitorAnchor.exe` (single file). Put it wherever you want it to live and run it
once; it registers itself under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. If you move the
exe later, launching it from the new location re-points the Run entry.

## Use

Right-click the tray icon (a small monitor with a white dot):

| Item | What it does |
| --- | --- |
| **Persist current layout** | Snapshot every active monitor's mode and HDR state and save it. Double-clicking the icon does the same. |
| Apply saved layout now | Force a restore immediately. |
| Show saved layout... | View what is saved and where the file lives. |
| Enforce layout on display changes | Toggle automatic restore. Off = the app just sits there. |
| Keep displays awake (never sleep) | Hold the display and system idle timers so the screens never turn off. On by default. |
| Start with Windows | Toggle the Run-key registration (on by default after first launch). |
| Open log | Opens the activity log in your text editor. |
| Exit | Quit (releases the keep-awake hold). |

Command-line switches (no tray icon, exit immediately):

| Switch | Effect |
| --- | --- |
| `--persist` | Save the current layout as the profile. |
| `--apply` | Restore the saved profile once. |
| `--dump` | Write the live layout, HDR state and connected monitors to `dump.txt` in the data folder. |
| `--test-enable` | Check, without changing anything, whether disabled-but-connected monitors could be re-enabled in a targeted way. Result goes to the log. |

Data folder: `%LOCALAPPDATA%\MonitorAnchor\` (`profile.json`, `settings.json`, `log.txt`).

## How it works

* Active monitors are enumerated with `EnumDisplayDevices` / `EnumDisplaySettingsEx`. Each one is keyed
  by its device-interface path (`\\?\DISPLAY#VENDORxxxx#...`), which is stable for a given monitor on a
  given port, so `\\.\DISPLAY3` becoming `\\.\DISPLAY5` after a replug does not matter.
* If the exact path is not found but exactly one saved monitor has the same vendor/product ID,
  that entry is used (monitor moved to a different port). Ambiguous matches are skipped.
* On `SystemEvents.DisplaySettingsChanged`, resume from sleep, or session unlock, the app waits 1.5 s
  for things to settle, then:
  1. Asks the DisplayConfig API which monitors are physically connected. A saved monitor that is
     connected but not part of the desktop is re-enabled with `SetDisplayConfig` using the current
     active paths plus one path for that monitor, so nothing else changes. If Windows rejects that,
     it falls back to "extend to all".
  2. Compares the live modes against the profile and pushes only the monitors that differ using
     `ChangeDisplaySettingsEx` with `CDS_NORESET`, committing everything in one call.
  3. Reconciles HDR per monitor through `DisplayConfigSetDeviceInfo` (the Windows 11 24H2 HDR call
     first, then the older advanced-colour call). HDR is only touched on monitors that reported HDR
     support when the profile was saved.
* Restoring triggers another change event; that pass finds everything matching and does nothing.
  If the driver rejects a mode three times in a row, automatic apply pauses for a minute.
* Monitors that are connected but not in the profile are left alone, including ones you have
  deliberately disabled (for example a closed laptop lid). Saved monitors that are unplugged are
  ignored until they come back.
* Keep-awake uses `SetThreadExecutionState` with the display and system "required" flags for as long
  as the app runs.

Not covered: DPI scaling, colour depth other than what was saved, and clone/duplicate topologies.
