# ADR-0022: The SFTP workspace becomes dual-pane

- Status: Accepted
- Date: 2026-08-23

## Context

[ADR-0021](0021-dual-pane-storage-workspace.md) built a dual-pane Storage page and closed by stating that
"the SFTP workspace stays single-pane and remote-first, and remains governed by
[ADR-0013](0013-sftp-workspace.md)". The result was two file-moving pages that behaved differently: on
Storage you saw both sides and dragged between them, while on SFTP a local path could only enter through the
operating-system picker or a drag from a file manager.

That difference is not a design position anyone chose; it is where the previous milestone stopped. This ADR
closes it, and adds the one thing neither page had: memory of where the local half was pointed.

## Decision

### The SFTP page gains a local pane, and keeps its remote list

`Grid RowDefinitions="Auto,*,6,180"` with the panes in `ColumnDefinitions="*,6,*"` — the Storage page's
shape, splitters and all, with the same application-wide transfer queue along the bottom.

The left half is the existing `FileBrowserPane` over the existing `LocalFileBrowserSource`, bound to a new
`SftpWorkspaceViewModel.Local`. **Nothing in the pane changed to make this work**, which is the evidence
that ADR-0021's "one pane class over one `IFileBrowserSource`" was the right seam.

**The right half is deliberately still `SftpWorkspaceViewModel`'s own list, not a third pane instance.** A
`SftpFileBrowserSource` would have made the page symmetrical and cost the five things
`IFileBrowserSource` has no room for: the Permissions and Owner columns and their mode formatting, the
properties dialog, the permissions editor, remote editing, and `SftpPath.ToShellLiteral`. Widening
`FileBrowserEntry` to carry a Unix mode and an ETag is exactly what ADR-0021 refused, and hiding those
features behind a capability flag is a larger change than this one. The consequence is a visibly
asymmetrical page — five columns on the right, three on the left, and a remote toolbar with buttons the
local pane has no equivalent for. That is accepted; the alternative was a feature regression.

The remote list's five fixed columns were narrowed (`3*,110,180,120,160` to `2*,80,125,95,105`) because
they now share the window with a second pane, and 570 px of fixed width in a 590 px pane leaves the file
name nothing.

### Transfers name their destination pane, and are reachable three ways

`UploadSelectionAsync` sends the local selection to whatever folder the remote list shows;
`DownloadSelectionAsync` sends the remote selection to whatever folder the local pane shows, and then
refreshes it so the arrivals are on screen rather than one `F5` away. Both are reachable from a toolbar
button, from the row's context menu, and from a drag — one implementation behind three gestures.

The picker-based **Upload…** and **Download…** stay. They predate the pane, they reach folders the pane is
not currently showing, and removing them was not asked for.

**The row menu's transfer entry is named on the row, not by reaching for the pane.** `TransferLabel` is
carried on `FileBrowserItemViewModel` because a `MenuFlyout` lives in a popup: `$parent[FileBrowserPane]`
does not find the pane from inside one, and the header would bind silently to nothing.

### Dragging remote to local hands over the staged files rather than downloading twice

This is the only genuinely hard decision here. ADR-0013 requires every path a native drag advertises to
exist for the whole drop, so `FileList_OnPointerPressed` downloads the selection into a staging directory
*before* `DoDragDropAsync` begins. Naively making the local pane accept that drag would download everything
a second time — 8 GB of traffic to move a 4 GB file.

Avalonia 12's `DataTransferItem.Set<T>(DataFormat<T>, Func<T>)` looked like the escape: stage lazily, only
if the operating system asks for `DataFormat.File`. It was rejected because the factory is synchronous and
the download is not. Blocking the UI thread on a transfer whose continuations need that same thread is a
deadlock, and whether a platform requests the format at drag start or at drop is a backend detail.

So the drag carries **two payloads**. The operating system gets the real staged files, unchanged. The local
pane gets a new in-process `FileBrowserExternalDrop` — a verb for the drop-target message and a
`Func<string, CancellationToken, Task>` the pane calls with the directory the pointer was over. The SFTP
page's implementation *moves* the already-staged files there and deletes the staging directory.

Two things fall out of that. One transfer, not two. And the staging directory ADR-0021 recorded as an
unswept leak is now cleaned up on this path — the first path on which it ever is. It still leaks when the
drag ends anywhere else, which remains a follow-up.

`FileBrowserExternalDrop` is what keeps the pane source-agnostic: the dragged-from page supplies the
action, because it is the only thing that knows what is moving, and the pane supplies the destination. The
pane learns nothing about SFTP. `MoveIntoAsync` lives on `LocalFileBrowserSource` in Application, not on
`IFileBrowserSource`: moving into a prefix on object storage is a billed, size-capped server-side copy plus
a delete, and there is nothing on that side to move *from*. It refuses an existing destination rather than
overwriting, because a silent clobber of a local file the user never named is not something a drag may do,
and it falls back to a recursive copy when `Directory.Move` cannot cross a volume — which, with staging
under the temporary folder, is the ordinary case on Windows rather than the exotic one.

### One `ILocalFolderMemory`, shared by both pages

A `SettingKey<string?> LastLocalFolder` behind an `ILocalFolderMemory` port, injected into the local pane
and into no other pane: the remote pane's root is pinned by the connection, and restoring a prefix from a
different bucket would open on an error banner.

**One value, not one per page.** Two independent memories are indistinguishable from none on whichever page
you did not use last, which is the whole reason to remember it.

**Written on arrival, not on the way out**, so a crash or a kill still leaves the folder remembered —
fire-and-forget and deliberately silent, because a failed settings write must not turn a folder that loaded
fine into an error banner. **Recall checks the folder still exists**, and a path that fails to list falls
through to the source's own root: a pane rooted on an ejected stick would otherwise open on an error every
launch until someone noticed the path box.

`NavigationCompositionTests` resolves `ILocalFolderMemory` from the real container, because an optional
constructor parameter whose service is not registered silently takes its default — both pages would quietly
stop remembering and no page-level test would notice.

### Keyboard

`Ctrl+Shift+Left`, `Ctrl+Shift+Right` and `Ctrl+L` now work on this page too, resolved against whichever
half holds focus. `Tab`, `F6`, `Ctrl+Tab` and `Alt+1`/`Alt+2` are left alone for the reasons ADR-0021 and
[ADR-0009](0009-keybinding-policy.md) give. The remote list keeps its own longer-standing keys — `Enter`,
`Backspace`, `Ctrl+R`, `F2`, `Delete` — rather than being retrofitted to the pane's `F5`/`F7` set, which
would be a change to muscle memory this ADR has no reason to make.

### Focus on load moves to the connection picker

It was the file list. The Storage page already focuses its picker, on the grounds that the keyboard should
land on the decision the page exists to make; with two lists on screen, "the file list" is no longer an
unambiguous place to land anyway.

## Consequences

`SftpWorkspaceViewModel` swaps its primary constructor for an explicit one. The local pane's Upload button
is an instance method, and a property initializer cannot reach one.

`SftpWorkspaceTests.ColumnHeadingsLineUpWithTheRowsBelowThem` had to be scoped to the remote half: it took
the first grid holding column-heading buttons, which is now the local pane's, and measured it against a
remote row.

ADR-0021's `FileBrowserPane.PaneFormat` became public. The SFTP remote list is not a pane and has to
recognise that format to accept an upload dragged out of the local pane beside it.

## Known limitations and follow-ups

- **The page is asymmetrical**, as described above, and will stay so until `IFileBrowserSource` can carry
  what an SFTP row knows.
- **The staging leak survives for drags that leave the application.** Only an in-app drop sweeps it.
- **A remote-to-local drop is a move, so it is not in the transfer queue.** The download that fed it was,
  as part of the drag; the move that follows is local and effectively instant.
- **No conflict resolver on SFTP transfers.** Unchanged from ADR-0021: a colliding local destination fails
  that one item with a message rather than prompting.

## Amendment to ADR-0021 and ADR-0013

[ADR-0021](0021-dual-pane-storage-workspace.md) is **amended** where it says the SFTP workspace stays
single-pane. Everything else it decides stands, and this change is the strongest evidence for it: a whole
second page grew a local half without one line of `FileBrowserPane` changing.

[ADR-0013](0013-sftp-workspace.md) is **amended, not superseded**. Its remote-first list, its virtualization
and its staging-directory drag-out are all still here and still governed by it. What changes is that the
list is no longer the only half of the page.
