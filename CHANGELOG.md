# Changelog

All notable changes to RemoteFlow are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and RemoteFlow uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). Versions come from git tags: a release is
whatever commit carries a `v`-prefixed tag, so an entry here and a tag are two halves of the same act.

## [Unreleased]

### Added

- User documentation. The README now walks a new user from download to a connected session, with dark-mode
  screenshots, and states the security posture plainly: no telemetry, no cloud, no accounts, no update
  ping — and exactly what is stored where, what is **never** stored, how credentials are held on each
  platform, and how host keys are verified. `docs/building.md` covers building and running from source on
  macOS and Linux, which is how those platforms get RemoteFlow in v1. `docs/troubleshooting.md` answers the
  failures people actually meet, each with a concrete fix.
- Remote Desktop. An RDP connection opens in Windows' own Remote Desktop Connection client — RemoteFlow
  does not embed RDP. The connection editor gained an RDP section: domain, resolution presets or a custom
  size, full screen, all monitors, and clipboard and drive sharing. It says which client it found, and
  when it finds none it says what to install instead of failing at launch time.
  **Your password is never written into the `.rdp` file**, not even as the DPAPI blob the format allows:
  a file that leaks is a credential that leaks. If you have stored one, it is handed to Windows for the
  moment the session starts and taken straight back out again; the default is to store nothing and let
  Windows ask. The generated `.rdp` lives in a per-launch temporary folder that is deleted after the
  client has read it, and anything a crash left behind is swept at the next startup.
  macOS and Linux are not supported yet: RemoteFlow says so and names a client to use in the meantime.
- Tag-driven versioning with MinVer. Every assembly records the version and the commit it was built from,
  `RemoteFlow.exe --version` prints both, and the Settings page has an About tab showing the same values.
- Windows release artefacts: a portable zip and an installer for both x64 and ARM64, built by
  `scripts/publish-windows.ps1`. The zip is self-contained and needs no .NET runtime installed. The
  installer is per-user, adds a Start-menu entry and an optional desktop shortcut, and **leaves your saved
  connections, settings, and credential references in place when you uninstall** unless you ask for them
  to be removed.
- A release workflow. Pushing a `v*` tag builds both architectures on runners of their own architecture,
  launches every zip and installer to confirm it starts and reports the version in the tag, and creates a
  **draft** GitHub release with `checksums.txt` and generated notes. Releases are never published
  automatically, and there is deliberately no auto-update mechanism and no update ping.
- The About tab now shows the licence and links to the repository, names the log and data folders and
  opens either one, and lists every third-party package with its licence. When something has gone wrong
  during the session it says what, and offers the log folder — **on your own machine. RemoteFlow sends
  nothing anywhere; there is no crash reporting, no telemetry, and no update check.**
- `THIRD-PARTY-NOTICES.md`, generated from the packages actually resolved for the application, and
  embedded in the binary so it travels with a portable zip. The build fails if a package arrives under a
  licence RemoteFlow has not agreed to ship.
- This changelog.

### Changed

- The application binary is now `RemoteFlow.exe` rather than `RemoteFlow.Desktop.exe`.
- The sidebar shows an icon beside every destination, and Connections gained a **New connection** button in
  its header so creating one no longer depends on the empty state.
- Connection rows read as a single entry: the host sits under the name in small grey text, and the
  environment badge is smaller and consistently light blue rather than colour-coded per environment. The
  badge keeps its ●/◆/⚠ glyph, which is what distinguishes the environments without relying on colour.
- The connection details panel is a fixed 500px wide, and its Edit, Duplicate, and Delete buttons are now
  icons with tooltips.

### Fixed

- The Windows taskbar button shows the RemoteFlow icon instead of a generic placeholder. Avalonia builds a
  24x24 icon for the window's large slot; the taskbar asks for 32x32 and falls back to the placeholder
  rather than accepting a different size, so RemoteFlow now applies the executable's own icon once the
  window is open.

## [0.0.0] - unreleased

RemoteFlow has not had a tagged release yet. Everything up to this point was built and reviewed as
pre-release work across Milestones 1 to 7: connections and folders, the SSH transport and host key policy,
the embedded terminal, SFTP browsing and transfers, remote editing, credential storage, and the backup
format. The first tagged release will restate what shipped rather than trying to reconstruct that history
here.
