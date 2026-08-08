# Changelog

All notable changes to RemoteFlow are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and RemoteFlow uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). Versions come from git tags: a release is
whatever commit carries a `v`-prefixed tag, so an entry here and a tag are two halves of the same act.

## [Unreleased]

### Added

- Tag-driven versioning with MinVer. Every assembly records the version and the commit it was built from,
  `RemoteFlow.exe --version` prints both, and the Settings page has an About tab showing the same values.
- Windows release artefacts: a portable zip and an installer for both x64 and ARM64, built by
  `scripts/publish-windows.ps1`. The zip is self-contained and needs no .NET runtime installed. The
  installer is per-user, adds a Start-menu entry and an optional desktop shortcut, and **leaves your saved
  connections, settings, and credential references in place when you uninstall** unless you ask for them
  to be removed.
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

## [0.0.0] - unreleased

RemoteFlow has not had a tagged release yet. Everything up to this point was built and reviewed as
pre-release work across Milestones 1 to 7: connections and folders, the SSH transport and host key policy,
the embedded terminal, SFTP browsing and transfers, remote editing, credential storage, and the backup
format. The first tagged release will restate what shipped rather than trying to reconstruct that history
here.
