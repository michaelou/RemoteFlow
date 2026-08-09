# Changelog

All notable changes to RemoteFlow are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and RemoteFlow uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). Versions come from git tags: a release is
whatever commit carries a `v`-prefixed tag, so an entry here and a tag are two halves of the same act.

## [Unreleased]

### Changed

- Rounded corners throughout. Buttons, text boxes, drop-downs, and list and tree rows share one radius,
  and the panels they sit on share a larger one. Nothing moved; everything is a little softer.
- **Connections is two panels.** The tree and the details or editor beside it each sit on their own card,
  instead of the tree sitting straight on the window background.
- **The connection tree draws real icons.** Favorites is a star, Recent a clock, a folder a folder, and a
  connection the thing it opens — a shell, a file browser, or a screen — in place of the text characters
  that stood in for them and rendered differently on every machine.
- **The SFTP browser has been rebuilt around icons and cards.** New folder, permissions, upload, and
  download are icons with their words beside them; back, forward, up, and refresh are icons in place of
  arrow characters. Every row shows a folder or file glyph, folders in the accent colour. The connection
  picker, the toolbar, and the listing are three cards, and the column headings show which column is
  sorted and in which direction — as an arrow, not a shade. The toolbar now reads in two lines,
  navigation then actions, so nothing is squeezed on a narrow window.

## [0.1.0] - 2026-08-09

The first tagged release. RemoteFlow is a local-first desktop workspace for the machines you administer:
SSH sessions, SFTP browsing and transfers, and Remote Desktop, organised in one place. Everything below
shipped in it. Windows gets prebuilt artefacts; macOS and Linux build from source.

### Added

- Connections, organised. Name, host, port, protocol, username, notes, folder, tags, and favourites, in a
  folder tree with drag-and-drop, inline rename, search, and filters by protocol, environment, and tag.
  Recent sessions are kept and can be cleared. Every connection is marked Development, Staging, or
  Production, and says so in words as well as colour.
- SSH sessions over Tmds.Ssh, with SSH.NET selectable as a fallback transport. Password, private key,
  agent, and keyboard-interactive authentication; keys can be discovered, pasted, browsed to, or generated
  as Ed25519.
- **Host key verification that behaves like OpenSSH.** Trust on first use by default, with the SHA-256
  fingerprint and randomart shown before you accept; Strict never prompts; Accept-any exists for lab
  machines and flags the connection as unverified. A changed key is never accepted silently, revoked keys
  refuse the connection, `known_hosts` imports including hashed hostnames, and comparison is constant-time.
  Trusted keys are listed and removable under Settings.
- An embedded terminal workspace: multiple local and SSH sessions as tabs, UTF-8 and ANSI, bracketed
  paste, scrollback search, configurable font, colour scheme, cursor, bell, and scrollback, shell profiles,
  and "open in system terminal". Tested against vim, nano, tmux, and htop. RemoteFlow drives XTerm.NET over
  a real PTY rather than implementing a terminal emulator.
- SFTP: browse, upload, download, rename, delete, create folders, and edit permissions where the server
  allows it, with a transfer queue you can watch, cancel, and retry.
- Remote editing. Open a remote file in your usual editor, keep working, and RemoteFlow uploads it when you
  save — and tells you when the remote copy changed underneath you, rather than overwriting it.
- **Credential storage that never touches the database.** Passwords, private-key passphrases, and RDP
  passwords go to Windows Credential Manager, the macOS login keychain, or libsecret, under a per-connection
  key; the database holds only a reference. Windows falls back to DPAPI-encrypted files when Credential
  Manager is unavailable, and an Argon2id/AES-GCM file vault exists for machines with no keyring at all.
- Backup and restore. A documented, versioned ZIP of connections, folders, tags, settings, and trusted host
  keys, importable as a merge or a replace, with credentials optionally included inside a separate
  encrypted entry and never anywhere else. See [docs/backup-format.md](docs/backup-format.md).
- An accessibility and keyboard-only pass. Every button, box, and list now tells a screen reader what it
  is — including the icon-only ones, which previously announced an arrow or nothing at all. The terminal
  announces as a text area named for its session and environment, its tabs take focus and say which
  session they are, and **`F6` moves focus out of the terminal**, which until now consumed `Tab` and had
  no way out. `Enter` presses the button the keyboard is on, opening the connection editor puts the caret
  in the first field, closing it returns the keyboard to the explorer, and committing a navigation hands
  the keyboard to the page rather than leaving it in the sidebar. The keyboard focus ring is a two-pixel
  light outline that stays visible on every surface, including the accent-filled primary button, where the
  previous accent-coloured ring was effectively invisible. See
  [docs/accessibility.md](docs/accessibility.md).
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

- Disabled text and failed-transfer messages were below the contrast floor on the darkest surfaces. Both
  now use palette colours that are measured, and the measurement is a test.
- The terminal workspace shows its arrows again. The shell-profile dropdown, the find bar's next and
  previous buttons, and its close button were each rendering two or three garbled letters instead of a
  symbol: the characters had been saved once through a Windows-1252 decode and kept the damage.
- The Windows taskbar button shows the RemoteFlow icon instead of a generic placeholder. Avalonia builds a
  24x24 icon for the window's large slot; the taskbar asks for 32x32 and falls back to the placeholder
  rather than accepting a different size, so RemoteFlow now applies the executable's own icon once the
  window is open.

The **Changed** and **Fixed** entries above describe work done against earlier pre-release states of the
same development line. Nobody upgrading from a published version encountered any of it; they are kept
because they say what the code does now and why.

[Unreleased]: https://github.com/michaelou/RemoteFlow/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/michaelou/RemoteFlow/releases/tag/v0.1.0
