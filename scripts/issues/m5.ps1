@(
@{ Number=37; Milestone='5 - SFTP'
   Title='ISftpService and the Tmds.Ssh implementation'
   Labels=@('model:sonnet-5','effort:high','area:sftp','type:feature','risk:contained')
   Body=@'
```yaml
model: claude-sonnet-5
effort: high
risk: contained
depends_on: [29, 31]
blocks: [38, 39, 41]
read_first:
  - src/RemoteFlow.Application/Abstractions/Ssh/ISshConnection.cs
touches:
  - src/RemoteFlow.Application/Abstractions/Sftp/**
  - src/RemoteFlow.Infrastructure/Sftp/**
  - tests/RemoteFlow.Ssh.IntegrationTests/**
verify: dotnet test tests/RemoteFlow.Ssh.IntegrationTests --filter Category=Integration
```

## Goal
Every remote filesystem operation the requirements list, behind one port, with correct path handling and
typed errors.

## Scope
```
ISftpService
    Task<IReadOnlyList<RemoteFileInfo>> ListAsync(string path, CancellationToken ct)
    Task<RemoteFileInfo?> StatAsync(string path, CancellationToken ct)
    Task CreateDirectoryAsync(string path, CancellationToken ct)
    Task RenameAsync(string from, string to, CancellationToken ct)
    Task DeleteAsync(string path, bool recursive, CancellationToken ct)
    Task SetPermissionsAsync(string path, UnixFileMode mode, CancellationToken ct)
    Task<string> GetRealPathAsync(string path, CancellationToken ct)
    Task<Stream> OpenReadAsync(string path, CancellationToken ct)
    Task<Stream> OpenWriteAsync(string path, CancellationToken ct)
```
`RemoteFileInfo`: name, full path, size, mtime, `UnixFileMode`, owner, group, is-directory, is-symlink,
symlink target.

Path normalisation (`.`, `..`, mixed separators, absolute vs relative) is centralised here so no caller
has to reinvent it. Error mapping to typed results: permission denied, not found, not a directory,
already exists, quota exceeded.

## Decisions already made - do not re-litigate
- Obtained from `ISshConnection.OpenSftp()`, wrapping `Tmds.Ssh.SftpClient` - it reuses the authenticated
  connection rather than opening a second one.
- **Symlinks are reported as links with their target, never silently followed.** Silently following is how
  a recursive delete escapes the directory the user thought they were deleting.

## Acceptance criteria
- [ ] Every operation verified against the #29 container.
- [ ] **UTF-8 filenames round-trip**, including CJK and emoji.
- [ ] Pathological names survive: spaces, single and double quotes, a literal newline, a 255-byte name.
- [ ] Symlinks are reported as links with their target, not followed.
- [ ] Permission-denied returns a typed result, not an exception.
- [ ] `.`, `..`, trailing-slash, and mixed-separator paths all normalise consistently.
- [ ] A directory with 5000 entries lists within a stated budget.
- [ ] `GetRealPathAsync` resolves `~` and relative paths against the server's notion of home.
- [ ] Streams from `OpenReadAsync`/`OpenWriteAsync` are disposed correctly, and disposing mid-read does
      not corrupt the connection for the next operation.

## Out of scope
Transfer orchestration (#38). Any UI. Server-side copy/move across directories (v2).
'@ },

@{ Number=38; Milestone='5 - SFTP'
   Title='Transfer engine: progress, cancellation, queue and atomic writes'
   Labels=@('model:sonnet-5','effort:high','area:sftp','type:feature','risk:contained')
   Body=@'
```yaml
model: claude-sonnet-5
effort: high
risk: contained
depends_on: [37]
blocks: [42, 44]
read_first:
  - src/RemoteFlow.Application/Abstractions/Sftp/ISftpService.cs
touches:
  - src/RemoteFlow.Application/Services/TransferEngine.cs
  - tests/RemoteFlow.Ssh.IntegrationTests/**
verify: dotnet test tests/RemoteFlow.Ssh.IntegrationTests --filter Category=Integration
```

## Goal
Uploads and downloads that report progress honestly, cancel promptly, and never leave a half-written file
where a complete one is expected.

## Decisions already made - do not re-litigate
- **Download to `{name}.part`, then rename on completion.** A cancelled or failed transfer must never leave
  a truncated file at the destination path - that is how users lose data they think they have.
- Bounded concurrency, default 3 - unbounded parallel transfers starve each other and the interactive session.
- Retry once on a transient error; never retry a permission or not-found error.
- **Resume is explicitly deferred to v2** - say so in the issue rather than half-implementing it.

## Scope
Upload and download with `IProgress<TransferProgress>` (bytes done, total, rate, ETA), cancellation,
recursive directory transfers preserving structure, a bounded queue, and a conflict-on-existing policy
(prompt rather than clobber).

## Acceptance criteria
- [ ] A 200 MiB transfer reports smooth progress with a sane rate and ETA.
- [ ] **Cancelling within 1 s leaves no partial file at the final path** - the `.part` file is removed or
      left only under a clearly temporary name.
- [ ] A recursive upload preserves directory structure and reports per-file progress.
- [ ] Cancelling one queued transfer does not disturb the others.
- [ ] A target-exists conflict **prompts** rather than overwriting.
- [ ] Concurrency is capped at the configured value (assert the maximum simultaneous in-flight count).
- [ ] A transient failure retries once; a permission error does **not** retry.
- [ ] Progress for a zero-byte file completes rather than dividing by zero.
- [ ] Transferring a 0-byte and a 1-byte file both succeed (boundary cases).

## Out of scope
Resume (v2). Bandwidth limiting. The UI panel (#44).
'@ },

@{ Number=39; Milestone='5 - SFTP'
   Title='SFTP workspace UI with OS drag-and-drop'
   Labels=@('model:sonnet-5','effort:high','area:sftp','type:feature','risk:contained')
   Body=@'
```yaml
model: claude-sonnet-5
effort: high
risk: contained
depends_on: [8, 34, 37]
blocks: [40, 41, 42, 59]
read_first:
  - src/RemoteFlow.Application/Abstractions/Sftp/ISftpService.cs
  - src/RemoteFlow.UI/Styles/DesignTokens.axaml
touches:
  - src/RemoteFlow.UI/Views/Sftp/**
  - src/RemoteFlow.UI/ViewModels/Sftp/**
  - tests/RemoteFlow.UI.Tests/**
verify: dotnet test tests/RemoteFlow.UI.Tests
```

## Goal
Browse a remote filesystem and move files in and out of it by dragging.

## Decisions already made - do not re-litigate
- **Remote-only pane plus OS drag-and-drop, not a dual-pane commander.** The requirement is "browse,
  upload/download"; a second local pane duplicates the OS file manager for roughly double the UI cost.
  Record dual-pane as a clean v2 addition in an ADR.
- Dragging *out* needs Avalonia's temp-file-then-move dance - the file must exist before the drop completes.

## Scope
- Virtualised list: name, size, modified, permissions, owner. Sortable columns, stable sort.
- Breadcrumb plus an editable path bar accepting a typed absolute path.
- Up / back / forward / refresh; hidden-file toggle bound to `SftpOptions.ShowHiddenFiles`.
- Double-click to navigate; type-to-select; multi-select; full keyboard navigation.
- Loading and error states inline in the pane.
- **Drag from the OS -> upload** into the hovered directory, with the exact target shown.
  **Drag from the pane -> download** to the drop location.
- Upload... / Download to... commands via `IFilePickerService`.

## Acceptance criteria
- [ ] A 5000-entry directory scrolls smoothly (virtualisation confirmed).
- [ ] Navigating into a permission-denied directory shows an **inline error and the pane stays usable** -
      no modal, no dead end.
- [ ] The path bar accepts a typed absolute path and navigates to it.
- [ ] Dropping a folder from Explorer / Finder / Nautilus uploads it recursively.
- [ ] Dragging out produces a **complete** file at the drop location, not a zero-byte placeholder.
- [ ] Drop feedback names the exact target directory before the drop.
- [ ] Dropping onto a read-only directory fails with a clear message and **no partial state**.
- [ ] Sorting is stable and directories group before files.
- [ ] Fully keyboard-navigable, including entering and leaving directories.

## Out of scope
Mutating operations (#40). Permissions dialog (#41). Remote editing (#42). Dual-pane (v2).
'@ },

@{ Number=40; Milestone='5 - SFTP'
   Title='File operations UI: rename, delete, mkdir and properties'
   Labels=@('model:sonnet-5','effort:medium','area:sftp','type:feature','risk:contained')
   Body=@'
```yaml
model: claude-sonnet-5
effort: medium
risk: contained
depends_on: [39]
blocks: []
read_first:
  - src/RemoteFlow.UI/ViewModels/Sftp/SftpWorkspaceViewModel.cs
touches:
  - src/RemoteFlow.UI/Views/Sftp/**
  - tests/RemoteFlow.UI.Tests/**
verify: dotnet test tests/RemoteFlow.UI.Tests
```

## Goal
The mutating half of the SFTP requirement - rename, delete, create folder - with a delete confirmation
that cannot be triggered by accident.

## Scope
Inline rename with collision detection **before** the request; delete with a recursive confirmation that
states the item count; new folder; a properties dialog showing full metadata; copy-path; refresh of
exactly the affected view after any mutation.

## Acceptance criteria
- [ ] **A recursive delete requires a confirmation that states the item count and cannot be triggered by a
      stray Enter** - the default focus is Cancel.
- [ ] A rename collision is caught locally before the request is sent.
- [ ] Every mutation refreshes exactly the affected view - not the whole tree, and not nothing.
- [ ] Operations are cancellable mid-flight.
- [ ] A failed delete partway through a recursive operation reports what succeeded and what did not,
      rather than a bare error.
- [ ] Creating a folder that already exists shows a clear message.
- [ ] The properties dialog shows size, mtime, mode (octal and rwx), owner, group, and symlink target
      where applicable.
- [ ] Copy-path copies a path that actually works when pasted into a shell on that host.

## Out of scope
Permissions editing (#41). Server-side copy/move across directories (v2).
'@ },

@{ Number=41; Milestone='5 - SFTP'
   Title='Permissions editor with octal and rwx grid'
   Labels=@('model:sonnet-5','effort:high','area:sftp','type:feature','risk:contained')
   Body=@'
```yaml
model: claude-sonnet-5
effort: high
risk: contained
depends_on: [37, 39]
blocks: []
read_first:
  - src/RemoteFlow.Application/Abstractions/Sftp/ISftpService.cs
touches:
  - src/RemoteFlow.UI/Views/Sftp/PermissionsDialog.axaml
  - tests/RemoteFlow.UI.Tests/**
verify: dotnet test tests/RemoteFlow.UI.Tests
```

## Goal
The "Permissions (where supported)" requirement: a two-way rwx grid and octal field, with a recursive
apply that fails safely.

## Decisions already made - do not re-litigate
- **Owner and group are display-only in v1.** chown/chgrp generally needs elevation and has no safe
  cross-server story - document it as out of scope rather than half-supporting it.

## Scope
An rwx checkbox grid (user/group/other) two-way bound to a live octal field, including setuid, setgid and
sticky bits. Optional recursive apply distinguishing files from directories (so `+x` on a tree doesn't make
every file executable). Owner/group shown read-only.

## Acceptance criteria
- [ ] The grid and the octal field stay in sync in **both** directions, including the three special bits.
- [ ] Typing an invalid octal value is rejected without corrupting the grid.
- [ ] Recursive apply distinguishes files from directories.
- [ ] A recursive apply **reports per-item failures without aborting the whole run**, and leaves
      already-applied changes visible and reported.
- [ ] Applying `000` to the current directory **warns first** - it is a self-lockout.
- [ ] A server that does not support chmod produces a clear "not supported" message.
- [ ] Round-trip: read a mode, display it, apply it unchanged, and `StatAsync` reports the same value.

## Out of scope
chown / chgrp. ACLs.
'@ },

@{ Number=42; Milestone='5 - SFTP'
   Title='Remote editing pipeline: temp file, watch and auto-upload'
   Labels=@('model:opus-5','effort:xhigh','area:sftp','type:feature','risk:contained')
   Body=@'
```yaml
model: claude-opus-5
effort: xhigh
risk: contained
depends_on: [38, 39]
blocks: [43]
read_first:
  - src/RemoteFlow.Application/Services/TransferEngine.cs
touches:
  - src/RemoteFlow.Application/Services/RemoteEditService.cs
  - src/RemoteFlow.Infrastructure/Platform/FileEditorLauncher.cs
  - src/RemoteFlow.Infrastructure/Platform/WatchedFileMonitor.cs
  - tests/RemoteFlow.Infrastructure.Tests/**
verify: dotnet test tests/RemoteFlow.Infrastructure.Tests
```

## Goal
The five-step remote-editing flow from the requirements: download to temp, open in the default editor,
detect save, upload automatically.

## Why this is `xhigh` despite being "just a file watcher"
Save detection is the part that looks trivial and is not. **Most editors save by writing a temp file and
renaming it over the original**, which a file-level `FileSystemWatcher` never sees. Watch the *directory*,
debounce, then confirm by hash. And editors touch mtime without changing bytes, so a naive watcher causes
upload storms - which over SFTP means the user's connection is saturated by their own editor.

## Decisions already made - do not re-litigate
- Temp path: `{CacheDir}/remote-edit/{sessionId}/{sha256(remotePath)[..8]}/{originalFileName}`. **The
  original filename is preserved** so the editor picks the right syntax mode - that alone justifies the
  nested directory.
- Watch the **directory**, 750 ms debounce, then confirm the local file's hash actually changed.
- Snapshots captured at download: `RemoteSnapshot { Size, MTimeUtc, Sha256 if Size <= 8 MiB }` and
  `LocalSnapshot` of the temp file - consumed by #43.

## Scope
`IRemoteEditService`, `IFileEditorLauncher` (`ShellExecute` / `open` / `xdg-open`), `IWatchedFileMonitor`
(directory watch + debounce + hash confirmation, with a polling fallback for network or FUSE paths), an
"editing N remote files" indicator, and cleanup on session close behind an unsaved-changes guard.

## Acceptance criteria
- [ ] Editing and saving in **VS Code, Notepad/gedit/TextEdit, and vim** each trigger **exactly one**
      upload per save - the atomic-write-rename test.
- [ ] A save that changes **no bytes** triggers **zero** uploads.
- [ ] Ten rapid saves within the debounce window produce one upload, not ten.
- [ ] The original filename is preserved in the temp path.
- [ ] Closing the session with a modified-but-not-uploaded file prompts.
- [ ] No temp file survives a clean shutdown.
- [ ] A crash leaves temp files that are swept on next start rather than accumulating forever.
- [ ] The indicator shows the correct count as files are opened and closed.

## Out of scope
Conflict detection (#43). An in-app editor - AvaloniaEdit is the right future choice; record it in an ADR.
'@ },

@{ Number=43; Milestone='5 - SFTP'
   Title='Remote edit conflict detection and resolution'
   Labels=@('model:sonnet-5','effort:high','area:sftp','type:feature','risk:contained')
   Body=@'
```yaml
model: claude-sonnet-5
effort: high
risk: contained
depends_on: [42]
blocks: []
read_first:
  - src/RemoteFlow.Application/Services/RemoteEditService.cs
touches:
  - src/RemoteFlow.Application/Services/RemoteEditService.cs
  - src/RemoteFlow.UI/Views/Sftp/ConflictDialog.axaml
  - tests/RemoteFlow.Ssh.IntegrationTests/**
verify: dotnet test tests/RemoteFlow.Ssh.IntegrationTests --filter Category=Integration
```

## Goal
The requirement's "warn on remote conflicts" - which the doc never defined. This issue defines it and
implements it.

## The definition (closes gap A8)
**Immediately before upload**, re-stat the remote file. It is a conflict if `Size` or `MTimeUtc` differs
from the `RemoteSnapshot` captured at download, or if the hash differs when one is available. Re-statting
*at upload time* rather than trusting the download-time snapshot is what makes this correct - the window
between download and save is exactly where someone else's edit lands.

## Decisions already made - do not re-litigate
- Four resolutions: **Overwrite remote** / **Keep both** (upload as
  `{name}.remoteflow-{yyyyMMdd-HHmmss}{ext}`, original untouched) / **Discard local** / **Cancel** (keep
  the temp file and keep watching).
- Hashing is skipped above 8 MiB, with size+mtime still enforced.
- `RemoteEditConflictDefault` setting, default `Prompt`.

## Acceptance criteria
- [ ] **Modifying the remote file between download and save ALWAYS produces a conflict prompt** -
      integration test that does exactly this against the container.
- [ ] The dialog shows both snapshots' size and mtime side by side.
- [ ] **Keep both** uploads to the timestamped name and leaves the original byte-identical.
- [ ] **Cancel** keeps the temp file and keeps watching, so the next save re-checks.
- [ ] **Discard local** requires confirmation.
- [ ] A same-second mtime change with a different size is still detected (mtime granularity is not
      sufficient on its own).
- [ ] Files over 8 MiB skip hashing but still detect size/mtime changes.
- [ ] No conflict is reported when nothing changed remotely - no false positives on a normal save.
- [ ] The default resolution honours the setting when it is not `Prompt`.

## Out of scope
Three-way merge or a diff view.
'@ },

@{ Number=44; Milestone='5 - SFTP'
   Title='Transfer manager panel'
   Labels=@('model:sonnet-5','effort:medium','area:sftp','type:feature','risk:contained')
   Body=@'
```yaml
model: claude-sonnet-5
effort: medium
risk: contained
depends_on: [38]
blocks: []
read_first:
  - src/RemoteFlow.Application/Services/TransferEngine.cs
touches:
  - src/RemoteFlow.UI/Views/Transfers/**
  - tests/RemoteFlow.UI.Tests/**
verify: dotnet test tests/RemoteFlow.UI.Tests
```

## Goal
One place to see everything in flight, with the shell showing an aggregate so the user does not have to
keep the panel open.

## Scope
The Transfers sidebar page: active / queued / completed / failed lists with progress, rate, ETA;
per-item cancel and retry; clear-completed; reveal-in-folder for finished downloads; and an aggregate
status indicator in the app shell.

## Acceptance criteria
- [ ] 20 queued transfers render and update **without UI lag** (throttle updates; do not bind to a
      per-byte progress event).
- [ ] Cancel and retry work per item.
- [ ] Failures show the reason and are retryable.
- [ ] The aggregate indicator matches the panel contents.
- [ ] Clear-completed removes finished items and **does not touch active ones**.
- [ ] Reveal-in-folder works on all three OSes.
- [ ] An empty state reads sensibly rather than showing an empty box.
- [ ] Progress updates are coalesced - assert the UI update rate stays bounded during a fast transfer.

## Out of scope
Cross-restart transfer persistence. Bandwidth limiting.
'@ }
)
