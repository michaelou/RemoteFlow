# Changelog

All notable changes to RemoteFlow are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and RemoteFlow uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). Versions come from git tags: a release is
whatever commit carries a `v`-prefixed tag, so an entry here and a tag are two halves of the same act.

## [Unreleased]

## [0.2.5] - 2026-08-11

RemoteFlow can now install the update it tells you about, which it has never done before.

### Added

- **RemoteFlow can install its own updates.** When a check finds a newer release, the About tab offers
  **Download and install** beside the link to the release page. It asks first, and the dialog says what it
  is about to do: download that release's installer from `github.com`, check it against the SHA-256 in the
  `checksums.txt` published with the release, then close RemoteFlow, install, and open it again. Nothing is
  downloaded until that button is pressed, and a download whose checksum does not match is deleted rather
  than run — there is no way to install one anyway. This replaces the position taken in
  [ADR-0016](docs/adr/0016-update-check.md); [ADR-0018](docs/adr/0018-self-update.md) explains what changed
  and is explicit that a checksum proves the download arrived intact, not who built it. Releases are still
  not code-signed, and the confirmation dialog says so.
- Only an installed copy updates itself. A portable copy, one that has been moved, or a build output
  directory says why the button is not there rather than leaving it to be guessed at, and a release with no
  installer this machine can verify does the same.
- An update that starts and never arrives is reported at the next launch, with the installer's log named
  and the downloaded installer kept so it can be run by hand.
- The installer now declares an `AppMutex`, so it and the uninstaller stop and ask rather than replacing
  files underneath a running RemoteFlow. The uninstaller had no such check before.

## [0.2.4] - 2026-08-11

Two things in the terminal that were plainly wrong: copying with the keyboard, and the colour of a
directory in `ls` output.

### Fixed

- **A keyboard copy takes the selection with it.** `Ctrl+Insert` and `Ctrl+Shift+C` did nothing after
  selecting with the mouse. A chord reaches the application as two events — the modifier going down, then
  the key — and the terminal clears its selection for any key it is handed, so the bare `Ctrl` wiped the
  selection a moment before the copy read it. A modifier on its own is now kept from the terminal while a
  selection exists; it sends the shell nothing either way. Typing an ordinary character over a selection
  still replaces it. Copy-on-select was broken by the same thing.
- **Terminal colours come from the chosen scheme.** Every ANSI colour was painted from the renderer's own
  fallback palette rather than RemoteFlow's, so a directory in `ls` output arrived as VGA navy `#000080` on
  the near-black background — about 1.3:1, and unreadable — while the scheme specified `#6CB6FF`. The
  scheme's sixteen colours are now published where the renderer looks for them, and switching schemes in
  Settings repaints them. Paper Light's bright blue was also below the contrast floor on white; it is now
  darker than its plain blue, which is what a light theme wants from a bold colour.

## [0.2.3] - 2026-08-11

The terminal workspace can be a grid instead of a stack of tabs, so several sessions are on screen at once.

### Added

- **The terminal workspace can show every session at once.** A **Grid** button next to the tab strip lays
  the open sessions out side by side instead of one at a time, and the picker beside it sets how many
  columns a row may hold. The tiles fill the area rather than leaving gaps: one session takes the whole
  workspace, two take half each, three under a limit of three take a third each — and a fourth makes it two
  rows of two rather than three tiles and a lone one. Each tile carries its own header with the session's
  name, environment and protocol, and its own **×** to close it. Remote desktops tile like anything else, so
  several remote screens can be watched together. The tab strip stays where it is: it remains the way to
  reorder sessions with the keyboard, and the way back out of a remote desktop surface with F6. The layout
  and its column count are remembered for the next run.

### Changed

- **Selection follows the keyboard.** Clicking or tabbing into a session makes it the selected one, which it
  had no need to be when only one session was ever on screen. Without it, **Close**, copy, paste and find —
  all of which act on the selection — would act on a different session than the one being typed into as soon
  as more than one is visible. Clicking into an embedded remote desktop counts too, even though that click
  never reaches RemoteFlow: the session asks to be selected when its surface takes focus.
- **Tiling resizes every live session.** Switching layouts or changing the column count resizes each
  terminal to its tile, so full-screen programs repaint and — because `ReflowOnResize` is off by default —
  output that was already wrapped keeps its old wrapping. Each remote desktop renegotiates its resolution
  to its own tile.

### Fixed

- **A remote desktop no longer loses live resizing at extreme viewport sizes.** The RDP control accepts a
  desktop between 200 and 4096 pixels and fails outside it, and a failed resize turned on SmartSizing for
  the rest of that session's life — nothing ever turned it back off. The requested viewport is now clamped
  to that range, so a very small tile crops or scales its picture and recovers when it grows, and an
  ultrawide monitor past 4096 pixels no longer costs the session true remote-resolution resize.

## [0.2.2] - 2026-08-10

The Connections page reads as a list of hosts rather than a column of text, and every page sits closer to
the sidebar.

### Changed

- **The connections list has rules between its rows.** Every row is the same height, each name and its
  `host:port` start on the same edge, and the environment badge is held at the right of the row instead of
  trailing the host name — so the badges form a column of their own. The two group rows, Favorites and
  Recent, are set in a smaller, quieter type than the hosts underneath them. Selecting a row now marks its
  name and glyph in the accent colour: hover and selection raise a row to the same surface, and without
  that the row the details pane belongs to was indistinguishable from whichever row the pointer was over.
- **The right-click menu answers anywhere on a row.** The blank space between a host's name and its badge
  was not hit-testable, so the menu — and the drag that reorders hosts into folders — only responded over
  the glyph, the name, and the badge. One consequence worth knowing: dropping onto the blank part of a
  *connection* row now resolves to that connection and is refused, where before it read as "move to the top
  level". The empty area below the list still moves a host to the top level.
- **The Connections header is a card.** The page title and its subtitle are gone; the sidebar already names
  the page. The search box sits at the top of a card with **New connection**, **New folder** and **Clear
  recent** below it, and the protocol, environment and tag filters — previously three rows of loose
  checkboxes taking more room than the list they filter — now live behind a **Filters** button, together
  with the summary of what is active and the way to clear it.
- **Every page sits closer to the sidebar.** Connections, Terminals, SFTP, Transfers, Backup and Settings
  had page margins of 16, 20, 24 and 28 between them; they are all 8 now, so switching pages no longer
  shifts where the content starts. Dialogs keep their own wider margins.

## [0.2.1] - 2026-08-10

### Fixed

- **Returning to the terminal workspace no longer freezes the window.** An embedded Remote Desktop tab
  keeps one view for the life of its session, so that its native window survives. Leaving the terminals
  page with such a tab selected and coming back built a second host for that one view, and a control
  cannot have two parents: the exception landed inside a layout pass, so the window stopped laying out
  and rendering altogether and only a restart brought it back. A host now takes the view over from the
  previous one.

## [0.2.0] - 2026-08-10

Remote Desktop stops being a thing RemoteFlow hands to another program. On Windows it is a tab like any
other, and the older way of opening it is still there for anyone who wants it.

### Added

- **Windows-only embedded Remote Desktop.** Windows can now open multiple Microsoft RDP ActiveX sessions
  as retained tabs beside local and SSH terminals, with reconnect/recovery controls, dynamic resolution,
  DPI-aware sizing, plain-text clipboard redirection, stored-credential handoff, and a documented F6
  focus escape. The existing **Open in external RDP client** action remains available, and can be selected
  as the default for process isolation or host compatibility. Embedded RDP is not available on Linux or
  macOS; those platforms keep the external-client workflow and native-client guidance. Display scaling is
  quantised to RDP's supported 100%, 140%, and 180% factors, and hosts without dynamic resolution use a
  SmartSizing bitmap fallback without reconnecting. Because `mstscax.dll` is in-process, a crash in that
  Windows component can also terminate RemoteFlow; the separate external client is the isolation
  fallback. See the [manual release playbook](docs/manual-test-rdp-embedded.md).

### Fixed

- **An imported backup shows up straight away.** Applying a backup — merge or replace — rewrote the
  database underneath the running application, and the Connections page went on showing the tree from
  before the import until RemoteFlow was closed and started again. An import now announces that
  everything reloaded: the tree, the tag filter chips, and the details pane are rebuilt from the imported
  data, and an editor left open on a connection the import may have removed is closed rather than left
  offering to save over it.

## [0.1.1] - 2026-08-09

A softer-looking application that can now tell you when there is a newer one, and still will not fetch it
for you.

### Added

- **An update check, off by default.** The About tab has a **Check for updates** button and a **Check
  automatically** tick box. Pressing the button runs one check; ticking the box runs one more each time
  RemoteFlow starts, and nothing in between — there is no timer and no background poll. A check is one
  HTTPS request to `api.github.com` for this project's newest release: it reads a version number, says
  whether this build is current, and offers a link to the release page when it is not.
  **RemoteFlow still does not update itself** — nothing is downloaded, nothing is installed, and the
  release page opens in your browser so you keep the chance to verify a download against `checksums.txt`.
  No account, no licence key, no installation identifier, and nothing about your machine or your
  connections is sent; the request names the software and nothing else. Leave the box unticked and
  RemoteFlow makes no unprompted request ever again. A release candidate is never offered to someone on a
  stable build, a build newer than the newest release is not offered a downgrade, and a check that cannot
  reach the network puts the reason on screen and stops there. The security posture in the README now
  spells all of this out.

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

[Unreleased]: https://github.com/michaelou/RemoteFlow/compare/v0.2.5...HEAD
[0.2.5]: https://github.com/michaelou/RemoteFlow/compare/v0.2.4...v0.2.5
[0.2.4]: https://github.com/michaelou/RemoteFlow/compare/v0.2.3...v0.2.4
[0.2.3]: https://github.com/michaelou/RemoteFlow/compare/v0.2.2...v0.2.3
[0.2.2]: https://github.com/michaelou/RemoteFlow/compare/v0.2.1...v0.2.2
[0.2.1]: https://github.com/michaelou/RemoteFlow/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/michaelou/RemoteFlow/compare/v0.1.1...v0.2.0
[0.1.1]: https://github.com/michaelou/RemoteFlow/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/michaelou/RemoteFlow/releases/tag/v0.1.0
