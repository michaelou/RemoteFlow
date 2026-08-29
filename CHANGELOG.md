# Changelog

All notable changes to RemoteFlow are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and RemoteFlow uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). Versions come from git tags: a release is
whatever commit carries a `v`-prefixed tag, so an entry here and a tag are two halves of the same act.

## [Unreleased]

### Changed

- **Production is green and development is red.** The two were the other way round. Green is the
  environment you are allowed to be in and red the one you are not, so the colours now say that. Staging is
  unchanged. This is one palette rather than three: the chip in the list, the swatch beside the Environment
  picker and the accent on a session tab all resolve to the same value for the same word, and a test holds
  them there.
- **The environment chip carries its environment's colour.** PROD, STAGE and DEV were three words on the
  same pale blue chip, so the environment of a row was only known once it had been read. Each now writes its
  word in that environment's own colour, on a fill made for it — muted rather than loud, because a chip is a
  small block of solid colour repeated down the whole list — and sits a few pixels further off the edge of
  the card. The word inside the chip is unchanged and is still what actually says which environment; the
  colour only finds it faster. A colour override moves the connection's session tab and leaves the chip
  alone, so a production box painted purple still shows PROD in production's green.
- **The connection editor is a stack of cards rather than one long form.** A connection is several unrelated
  decisions — where the host is and how to authenticate to it, which options its protocol brings, where it
  files, and what you wrote about it — and on a single surface all of it read as one list of thirty fields.
  Those are four cards now, separated by six pixels, with the heading and the buttons on cards of their own
  so they stay put while the middle scrolls. The protocol card is absent rather than empty for the protocols
  that bring no extra options.

### Added

- **The pane beside the connection list can be closed, and the list takes the whole page when it is.** Both
  the details and the editor carry a close button, and Escape does the same from either; the pane's column
  collapses with it rather than leaving a 500-pixel hole. Clicking a connection opens it again — including
  the row it was closed from, which raises no selection change of its own.

## [0.6.1] - 2026-08-26

### Fixed

- **Every terminal in the tab layout works again, not just the last one.** A session that is not the one on
  screen keeps its place in the workspace, at full size, because a remote desktop cannot survive being
  re-hosted — but its tile also kept painting its own background over the whole area and swallowing clicks.
  The tiles stacked in the order the tabs are in, so the last session covered all the others: selecting any
  earlier tab left a blank panel that could not be typed into. Switching to the grid and back made it
  obvious, because a new terminal is added at the end and selected, so it always looked right until you went
  back to an older tab.
- **Output on Linux no longer walks off to the right.** RemoteFlow's terminal was built with newline
  translation switched off, so a line feed from the shell moved the cursor down a row without returning it to
  the first column, and every line of `ls` started where the previous one ended. It also left the shell's own
  line editor counting columns that did not match the screen, which is what redrew the line you were typing
  over the wrong one and repeated characters as you typed or pasted. Windows was unaffected because its
  console renders the screen itself, and macOS was already configured correctly.
- **A new terminal on Linux and macOS now starts your own shell, not a stripped one.** bash was launched with
  `--noprofile --norc`, which suppressed the prompt, the aliases and the colours from your own
  configuration, while PowerShell on Windows kept its profile — so the same application looked configured on
  one platform and bare on the other. A shell profile you have already saved keeps the arguments it was
  created with; clear it under Settings → Terminal to pick up the new default.

## [0.6.0] - 2026-08-24

### Added

- **RemoteFlow can now back itself up on its own.** Turn it on under Backup → Automatic and every change to
  a connection, folder or tag writes a fresh archive about thirty seconds after you stop editing — so a
  rename, a retag and a move produce one backup rather than three. Backups can go to a folder on this
  computer, or to an SFTP connection or storage account you have already saved, which means the place your
  backups live is configured once, in the same list as everything else. You choose how many to keep and
  older ones are removed after each successful run; files RemoteFlow did not write are never touched, so a
  manual export sitting in the same folder is safe.
- **Automatic backups always include your saved credentials**, encrypted with a passphrase you set once and
  which is kept in your system credential store, never in RemoteFlow's database. Without it a restored
  backup would give you back a list of hostnames and nothing you could sign in with. If no passphrase is
  set, RemoteFlow writes nothing and says so rather than quietly making an archive you cannot restore from.
- **The Backup page reports how the last automatic run went** — when, where to, and what went wrong if
  anything did. A failed upload never interrupts what you are doing; it waits for you there, and retries at
  the next change and the next launch.
- **RemoteFlow can now open its own credential vault.** On a computer with no system keyring it can use —
  most often Linux without libsecret — RemoteFlow keeps saved passwords and keys in an encrypted file of its
  own. Nothing ever opened that file, so on those machines credentials silently could not be saved or read
  at all. RemoteFlow now asks for the vault passphrase when it starts: once to choose one the first time,
  with a confirmation box and a warning that nothing else holds a copy, and once to recall it thereafter. A
  wrong passphrase can be retyped; declining leaves RemoteFlow running without saved secrets rather than
  failing to start, and the Backup page offers a way back in without restarting. Machines with a working
  keyring are untouched and see no prompt.

### Changed

- Folder and tag edits now announce themselves internally, the way connection edits already did. Without
  that, an automatic backup would have gone stale after a folder rename with nothing to show for it.

## [0.5.0] - 2026-08-24

### Added

- **The connection explorer's toolbar gained two buttons.** One closes every folder — including folders
  nested inside closed ones — and opens them all again once they are shut; its glyph and its tooltip say
  which way it will go, and what it does is remembered the same way collapsing one folder by hand is. The
  other drops the `host:port` line from under every name, which takes a row from 45 pixels to 26 and shows
  about seventy per cent more of the tree. That choice is remembered between sessions.
- **Each protocol has its own glyph and its own colour** in the explorer: a shell, a file browser, a
  screen, a bucket and now a cloud, in green, blue, violet, amber and cyan. An S3 bucket and an Azure
  container used to share the storage glyph and were told apart only by reading the row. The shape carries
  the difference as well as the colour does, both palettes clear the contrast floor for a meaningful
  graphic in light and dark, and the colours follow a theme switch rather than freezing at the one they
  were first drawn in.

### Changed

- **New folder and Clear recent are glyph buttons**, as are the two new ones beside them: five worded
  buttons on one line crowded out the search box above them. Each names itself with a tooltip and to a
  screen reader — the primary **New connection** button keeps its words.
- **File and folder listings are compact.** Smaller type and a 24-pixel row instead of a 30- or 32-pixel
  one, on the local, remote and SFTP browsers alike, so a folder shows about a third more of itself before
  you scroll. The size and date columns narrowed to match, which gives the width back to the file name.
- **The SFTP page's two column-heading strips line up.** The remote half used to hang its toolbar in a card
  of its own with different padding, which left its headings roughly a row and a half below the local
  pane's; it now mirrors the pane row for row. Its "Hidden files" box reads **Hidden**, as the local one
  does, and its error banner uses the same colour token as the rest of the app rather than a hard-coded red.
- **Transfers sits below Storage in the sidebar**, under the two pages whose work it queues.

## [0.4.0] - 2026-08-23

### Added

- **The SFTP page is dual-pane, like the Storage page.** Your own files on the left, the server on the
  right, the transfer queue along the bottom. Select rows and press **Upload** or **Download**, right-click
  them and choose the same from the row menu, or drag them from one pane to the other. **Download** goes to
  whatever folder the local pane is showing and refreshes it, so what arrived is on screen rather than one
  refresh away; **Download…** still asks for a folder, and **Upload…** still opens a picker, for the times
  the pane is not pointed where you want.

  `Ctrl+Shift+Left`, `Ctrl+Shift+Right` and `Ctrl+L` work here now too. The remote list keeps everything it
  had — the permissions and owner columns, the properties and permissions dialogs, remote editing, the
  shell-safe path — and its own keys: `Enter`, `Backspace`, `Ctrl+R`, `F2`, `Delete`.

  Dragging out of the remote list still works against your file manager, and dragging it onto the local pane
  now finishes the transfer that drag already started rather than fetching everything a second time — one
  transfer of a 4 GB file instead of two, and the temporary directory it used is cleaned up afterwards.

  See [docs/adr/0022-dual-pane-sftp-workspace.md](docs/adr/0022-dual-pane-sftp-workspace.md) for why the
  right-hand list is deliberately not a third copy of the pane, and what that costs.
- **The local pane reopens where you left it**, on both pages, from one shared memory: walk to a folder on
  the Storage page, switch to SFTP, and you are still in it. A remembered folder that has since been deleted
  or unmounted opens your home directory instead of an error.
- **Right-clicking a row offers the transfer**, reading **Upload** on a local pane and **Download** on a
  remote one — the same action as the toolbar button and the drag.

## [0.3.0] - 2026-08-22

### Added

- **S3 and Azure Blob accounts can be saved as connections.** Choose **S3** or **Azure Blob** as the
  protocol and the editor asks for what that provider needs: an access key ID and a region for S3, a
  storage account name for Azure. The host and port fill themselves in — `s3.{region}.amazonaws.com`,
  `{account}.blob.core.windows.net`, port 443 — and stop doing so the moment you type over them, which is
  how a sovereign-cloud account such as `*.blob.core.chinacloudapi.cn` is reached. The secret access key or
  account key is stored in the platform keychain like every other RemoteFlow credential; it never goes in
  the database or a plaintext backup entry.
- **S3-compatible services are supported.** A custom endpoint and a path-style-addressing switch make the
  S3 protocol work against MinIO, Ceph/RGW, Backblaze B2, Cloudflare R2 and Wasabi.
- **A bucket or container can be named on the connection**, for a key that is scoped to one and therefore
  cannot list them. Leave it blank to browse the whole account. An optional prefix starts browsing further
  in.

  See
  [docs/adr/0019-object-storage-provider-abstraction.md](docs/adr/0019-object-storage-provider-abstraction.md)
  for what the foundation covers and what is deliberately deferred.
- **Objects in the gigabytes transfer in parallel chunks.** Uploads split into parts and downloads into
  ranges, sized automatically from the object — an 8 MiB part for a 4 GiB object, 64 MiB for a 500 GB one —
  with four in flight at a time. Memory stays flat regardless of object size, the progress bar never goes
  backwards, and the speed and time-remaining figures for these transfers follow a five-second window
  rather than the average since the transfer started, so a link that slows down is reported honestly.

  A failed part is retried on its own rather than restarting the transfer, and cancelling aborts the
  incomplete upload so its parts are not left behind and billed. RemoteFlow cannot promise that after a
  crash or a power cut, so set a lifecycle rule on the bucket — S3's `AbortIncompleteMultipartUpload` at
  seven days — as the durable backstop. Azure needs nothing: uncommitted blocks expire on their own.

  See [docs/adr/0020-chunked-object-storage-transfers.md](docs/adr/0020-chunked-object-storage-transfers.md)
  for the decisions and the known limitations.
- **A Storage page, with your own files beside the bucket.** Local filesystem on the left, bucket or
  container on the right, transfer queue along the bottom. Select rows and press **Upload** or
  **Download**, drag between the panes, or press `Enter`. Double-clicking an S3 or Azure Blob connection
  opens it here. A folder is counted and confirmed before a byte moves.

  The queue at the foot of the page is the same one the **Transfers** page shows, not a second one, so a
  transfer started anywhere appears everywhere — and clearing completed items from either surface clears
  both.

  Two things it will not pretend to do. **"Starts with" is not a search:** both providers narrow a listing
  by prefix and neither searches by substring, so the box re-asks the provider rather than sifting rows
  already on screen. And **a listing stops at 10,000 rows** with a line saying "of many shown" — never a
  made-up total, because S3 cannot cheaply count a prefix. Sorting a truncated listing sorts what is
  loaded, and the column tooltip says so.

  On Windows the local pane has a **drive picker** when the machine has more than one ready volume,
  labelled the way the operating system labels it — `D:\ (Backup)`. It follows wherever you navigate, and
  it is re-read on **Refresh** so a drive you have just plugged in appears.

  Object storage has no rename, so `F2` works on the local pane only. Keys are in
  [docs/keybindings.md](docs/keybindings.md); the whole feature, including the bucket lifecycle rule worth
  setting, is in [docs/object-storage.md](docs/object-storage.md).
- **A conflict prompt for transfers, with "apply to all".** When the destination already exists RemoteFlow
  overwrites by default — a put is atomic and idempotent in both providers. Set **Storage conflict
  default** to *Prompt* to be asked instead, which is worth doing on an unversioned bucket. Answering once
  with **Apply to all remaining items** covers the rest of that one drag and nothing else. SFTP transfers
  are unchanged.

### Changed

- Protocol names are now written out in full wherever they are shown — the filter chips, the details pane
  and the protocol picker say **Azure Blob**, not `AZUREBLOB`.
- The S3 **Region** box suggests AWS's regions as you type, and warns when what you entered is not one of
  them — `eu-west` offers `eu-west-1`, `eu-west-2`, `eu-west-3`. It stays a warning rather than a rule,
  because the same field serves S3-compatible services where the region is whatever that deployment calls
  it; setting a custom endpoint silences it.
- The database schema version moves to 2. A database that has been opened by this release, or that contains
  an S3 or Azure Blob connection, is refused by RemoteFlow 0.2.6 and earlier with a message telling you to
  upgrade, rather than failing part-way through opening the connections page. Existing databases are
  stamped to 2 on first launch and need no action.
- **Linux artefacts are built by CI now, not by hand.** 0.2.6 was the first release to carry a `.deb` and
  a tarball, and both were built on a maintainer's machine and attached to the draft afterwards, which
  meant `checksums.txt` was regenerated over all eight files and re-uploaded by hand. A tag now runs four
  build legs — `win-x64`, `win-arm64`, `linux-x64` and `linux-arm64`, each on a runner of its own
  architecture — and the draft arrives complete, with one `checksums.txt` covering all eight assets. What
  you download is unchanged in name and content; what changed is that nothing about it was assembled by
  hand. Automation still only ever writes a draft: publishing is a person pressing the button.
- A backup written by this release that contains an S3 or Azure Blob connection cannot be imported by
  RemoteFlow 0.2.6 or earlier: protocol names are stored as strings, and an older build refuses an archive
  naming one it does not know rather than importing a connection it could not open. Backups containing only
  SSH, SFTP and RDP connections are unaffected in both directions.
- RemoteFlow now ships the AWS SDK for S3 and the Azure Storage Blobs SDK, and the packages they pull in:
  thirteen new entries in `THIRD-PARTY-NOTICES.md`, taking it from 84 packages to 97. Eleven of the
  thirteen come from the Azure side, and four of those — `Microsoft.Identity.Client`,
  `Microsoft.Identity.Client.Extensions.Msal`, `Microsoft.IdentityModel.Abstractions` and
  `System.Security.Cryptography.ProtectedData` — are an MSAL stack RemoteFlow never calls. Azure Blob
  connections authenticate with a shared account key, not with Entra ID, but `Azure.Core` depends on MSAL
  unconditionally, so it is restored, published inside the artefacts, and attributed like everything else.
  All thirteen are MIT or Apache-2.0.

### Fixed

- **Editing an S3 or Azure Blob connection now keeps the change.** Every object-storage setting — region,
  endpoint, path-style addressing, bucket, prefix — was written correctly when the connection was first
  created and then silently discarded on every save afterwards, with no error: the update path copies each
  block of options by hand and this one was never added to that list. Correcting a mistyped region appeared
  to work, saved nothing, and failed to connect against the old value. Existing connections need only be
  re-saved with the right values.

## [0.2.6] - 2026-08-22

### Added

- **A Debian package for Linux.** `./scripts/publish-linux.sh` builds a portable tarball and a `.deb` per
  architecture, the counterpart to `publish-windows.ps1`. The package installs to `/opt/remoteflow`, puts
  `remoteflow` on `PATH`, and registers a launcher entry and icons, so RemoteFlow appears in the
  application menu instead of requiring a hand-written desktop file. Uninstalling keeps your connections,
  settings and host keys: everything RemoteFlow writes follows the XDG base directory spec and lives under
  `$HOME`, which `dpkg` never touches. See [docs/packaging-linux.md](docs/packaging-linux.md). This is the
  first release to attach Linux artefacts; they are built and verified on a maintainer's machine rather
  than by CI, which has no Linux release job.
- Application icons for Linux, extracted from the existing Windows `.ico` and committed as PNGs at
  `build/linux/icons/`. The publish output has never contained an icon, so the desktop entry documented in
  [docs/building.md](docs/building.md) pointed at a file that did not exist.

### Changed

- SSH.NET moves from 2025.1.0 to 2026.0.0, which fixes GHSA-q939-rpr3-3284 (HIGH): `ScpClient`'s recursive
  download let server-controlled filenames escape the destination directory. RemoteFlow has never
  referenced `ScpClient` — SFTP is the only file transfer path — so the vulnerability was unreachable, but
  the advisory failed the build, and being unreachable today is not a reason to stay on it. No adapter
  changes were needed; the release declares no known breaking changes.

### Fixed

- **The build no longer fails on a Debian or Ubuntu machine.** Those distributions ship .NET SDK 10.0.1xx,
  which `global.json` rejects, so `dotnet` reported "A compatible .NET SDK was not found" rather than
  building. The prerequisites in [docs/building.md](docs/building.md) now say so and give the
  `dotnet-install.sh` invocation that works.
- **The Windows build is green again.** 63 of 270 UI tests had been failing since 0.2.5, all of them
  reported as `TypeInitializationException` on an Avalonia control, which reads like 63 unrelated breakages
  and was one: `RoutedEvent.Register` writes into a plain `Dictionary`, so two threads running an Avalonia
  type initialiser at once corrupt it and every later control construction throws. The UI and Infrastructure
  test assemblies both mix `[AvaloniaFact]`, which runs on the headless dispatcher thread, with plain
  `[Fact]` on pool threads, and xunit parallelises by collection. Both now disable collection
  parallelisation. The race never reproduced on Linux at any thread count, which is why it survived: it
  needs the timing of a particular machine, not a particular platform.
- Six tests in `AppInstallInfoTests` failed on Linux and macOS. They assert Windows path semantics —
  drive-letter roots and `\` separators — while running through the host's path APIs, which off Windows
  reinterpret them: an unrooted `C:\…` literal picks up the working directory, and a working directory
  under `bin/Release` then makes every case look like a build output. They are now skipped off Windows,
  matching how the repository already handles macOS keychain and Windows job-object tests.

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

[Unreleased]: https://github.com/michaelou/RemoteFlow/compare/v0.6.1...HEAD
[0.6.1]: https://github.com/michaelou/RemoteFlow/compare/v0.6.0...v0.6.1
[0.6.0]: https://github.com/michaelou/RemoteFlow/compare/v0.5.0...v0.6.0
[0.5.0]: https://github.com/michaelou/RemoteFlow/compare/v0.4.0...v0.5.0
[0.4.0]: https://github.com/michaelou/RemoteFlow/compare/v0.3.0...v0.4.0
[0.3.0]: https://github.com/michaelou/RemoteFlow/compare/v0.2.6...v0.3.0
[0.2.6]: https://github.com/michaelou/RemoteFlow/compare/v0.2.5...v0.2.6
[0.2.5]: https://github.com/michaelou/RemoteFlow/compare/v0.2.4...v0.2.5
[0.2.4]: https://github.com/michaelou/RemoteFlow/compare/v0.2.3...v0.2.4
[0.2.3]: https://github.com/michaelou/RemoteFlow/compare/v0.2.2...v0.2.3
[0.2.2]: https://github.com/michaelou/RemoteFlow/compare/v0.2.1...v0.2.2
[0.2.1]: https://github.com/michaelou/RemoteFlow/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/michaelou/RemoteFlow/compare/v0.1.1...v0.2.0
[0.1.1]: https://github.com/michaelou/RemoteFlow/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/michaelou/RemoteFlow/releases/tag/v0.1.0
