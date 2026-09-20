# Ideas and open items

Things considered but not built, so they are not lost. None of them is needed for the tool to do its job.

## Open items

- **winget submission.** Every release attaches `winget-manifests.zip` with correct hashes. Submitting is a PR to
  [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) under the owner's account. The MIT license is
  in place, which the manifests require.
- **Real code-signing certificate.** The MSIX is signed with a self-signed certificate (`msix/MonitorAnchor.cer`,
  private key in the `MSIX_CERT_PFX_BASE64` / `MSIX_CERT_PASSWORD` repository secrets, expires 2036). Users must
  trust it by hand, and SmartScreen warns on the exes. Azure Trusted Signing would drop into the same workflow
  step. Only matters once other people install it.
- **MSIX install smoke test in CI.** The package packs, signs and verifies but has never been installed. A step
  that trusts the certificate on the runner, `Add-AppxPackage`s the fresh MSIX and runs `MonitorAnchor.exe --dump`
  through the execution alias (checking for "MSIX package" in the dump) would catch a broken package before
  release. Packaged-mode code paths (startup task, App Installer updates, virtualised data folder) are untested.

## Feature ideas

1. **Drive the KVM over its serial port.** The Level1Techs HDMI 2.1 KVM takes single-letter commands over RS-232
   (`V=N` video, `U=N` USB, `A=N` audio, 19200 8N1); see
   [Level1TechKVMControl](https://github.com/guscatalano/Level1TechKVMControl). The app could switch video back to
   this machine when it enforces a layout, or offer a "switch here" menu item. Needs the RS-232 adapter connected
   to this PC. The one idea that would change daily use.
2. **Global hotkeys.** A key combination to apply the active layout or persist the current one, for when a game
   leaves the desktop scrambled and the tray icon is a hunt.
3. **Export and import layouts.** `layouts.json` already copies cleanly between machines with the same monitors;
   a menu item would make that discoverable.
4. **Portable mode.** If a `portable` marker file sits next to the exe, keep the data folder beside it instead of
   under `%LOCALAPPDATA%`. Useful only for running from a USB stick.
5. **Manual layout choice when ambiguous.** Today the largest matching layout wins automatically. A "use this
   layout for these monitors" override would help if two saved layouts share the same monitor set on purpose
   (e.g. a work arrangement and a gaming arrangement for the same desk).
6. **Persist window memory across restarts.** Window placements are kept in memory only; a restart while a
   monitor is away forgets them. Writing them to `windows.json` would close that gap.

## Known limitations

- Clone/duplicate topologies are not handled; each monitor is treated as its own extended display.
- Colour depth is only ever whatever was saved (always 32-bit on modern Windows).
- HDR restore has only been exercised on hardware that reports HDR support; the two VG259QM panels on the
  development machine do not expose it over their current connection.
- Fake monitors follow the parsec-vdd protocol exactly but have not been run against the real driver.
- The DPI-scale and HDR device-info calls are undocumented Windows internals; stable for years, but Microsoft
  could change them.
