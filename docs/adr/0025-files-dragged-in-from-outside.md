# ADR-0025: Files dragged in from outside the application

- Status: Accepted
- Date: 2026-09-02

## Context

Three drags move files in RemoteFlow: remote to local, local to remote, and from the operating system's
file manager onto a remote listing. After [ADR-0022](0022-dual-pane-sftp-workspace.md) the SFTP page had
all three — its remote list has accepted `DataFormat.File` since [ADR-0013](0013-sftp-workspace.md) — and
the Storage page had the first two. `FileBrowserPane` recognised exactly two in-process formats, so a file
dragged off the desktop and onto a bucket was declined with no cursor and no message. The gap was not a
decision; it is where ADR-0021 stopped.

Holding a drag over a folder row also revealed a second, quieter gap. The pane has always announced
*"Drop into media-prod/2024"*, and then transferred into whichever prefix the pane was showing:
`EntryList_OnDrop` called `origin.TransferAsync()`, which reaches the page, which reads
`to.CurrentPath`. The message was true on the SFTP page, whose remote list resolves the hovered row
itself, and false on the Storage page's two panes.

## Decision

### The pane gains a second page-supplied seam, and only the remote pane gets a handler

`ExternalFilesHandler` — `Func<IReadOnlyList<string>, string, CancellationToken, Task>?` — beside the
existing `TransferHandler`, set by the page, with `AcceptsExternalFiles` for the view to gate the drag on.
The pane still learns nothing about buckets: it turns `IStorageItem`s into local paths, decides which
folder the pointer was over, and hands both to whatever the page put there. This is the same shape
ADR-0022's `FileBrowserExternalDrop` has, inverted — there the dragged-*from* page supplies the action
because it alone knows what is moving; here the dragged-*onto* page does, for the same reason.

**Only the remote pane is given one.** A file dragged out of the file manager and onto the local pane is
already on this machine, and copying it beside itself is not what the gesture means, so that drag is
declined visibly rather than accepted and silently ignored.

### In-process formats are read first, files last

A drag out of the SFTP remote list carries two payloads: staged files for other applications and an
in-process action for this one. Reading the file list first would download the whole selection a second
time — the exact waste ADR-0022 went to some length to avoid — so `ExternalDropFormat`, then
`PaneFormat`, then `DataFormat.File`, in both `DragOver` and `Drop`.

### A dropped path is described in Application, not in the view model

`LocalFileBrowserSource.TryDescribe` turns one path into a `FileBrowserEntry`, and the drop then takes the
identical route a pane-to-pane upload takes: counted, confirmed, one conflict resolver for the batch, one
shared queue. Nothing about the transfer knows the rows came from a drag rather than from a listing.

It is in Application for ADR-0021's reasons — `System.IO` behaviour gets plain `[Fact]` coverage there —
and two details are load-bearing. **Hidden entries are described rather than filtered**: the pane filters
them so it is not full of noise nobody asked for, but a file dragged on by hand was asked for, and
dropping it silently would look like a failed drop. And the trailing separator a file manager may hand
over is removed with `Path.TrimEndingDirectorySeparator`, which is root-aware — trimming by hand turns
`C:\` into `C:`, the current directory on that drive rather than its root, and `DirectoryInfo` keeps the
separator in `FullName`, so the entry's path would not match the same folder listed by the pane.

**A path that no longer exists is skipped, not failed.** A drag carrying nothing local at all — a browser
image, an attachment never spooled to disk — reports that instead of appearing to have worked, which is
the one outcome a drop may not have.

### A drop lands where the pointer was released

`TransferHandler` takes a destination, null meaning "wherever the other pane is pointed", which is what
the toolbar button and the row menu mean, having no pointer position to speak of. The drop path passes the
hovered folder for pane-to-pane drags too, so the pane's own drop-target message is now true on both
pages, and both directions match the SFTP remote list's long-standing behaviour.

`TransferAsync` stays parameterless so `TransferCommand` stays parameterless for the button that binds it;
`TransferToAsync` is the drop's way in. The same split gives `SftpWorkspaceViewModel` a
`UploadSelectionToAsync` beside its `UploadSelectionAsync`.

### The drag that never started

Testing the drop from the file manager showed that no drag *out of* either file list had ever worked — not
pane to pane on Storage, not out of the SFTP remote list to another application, in any direction. Both
pages declared `PointerPressed="…"` on their `ListBox`, and a press on a row never reaches such a handler:
`ListBoxItem` is a child of the list, so it sees the bubbling press first, and
`SelectingItemsControl.UpdateSelectionFromEvent` sets `Handled` the moment the press triggers selection —
for the right button as well as the left, which is why the row menu's "narrow the selection to the clicked
row" was dead too. A handler declared in markup asks only for unhandled events. The one drag in the
application that did work, the connections tree, attaches its handler to the *row template's* grid, inside
the container, which is why it never hit this.

So both lists now attach that handler in code with `handledEventsToo: true`. Running after the container
is what these want anyway: the selection the container just applied is already in place, so pressing an
unselected row and dragging in one motion works, where before it would have needed a separate click first.

`StorageKeyboardTests.APressOnARowIsHandledByTheRowSoADragHandlerHasToAskForHandledEvents` pins the
platform behaviour on the real control. It is a fact about Avalonia 12.1.1, and
`Directory.Packages.props` already says to upgrade that by hand.

**A press no longer starts the drag; four pixels of movement does.** `DragGesture` holds the press and
hands it back once the pointer has moved, because `DoDragDropAsync` accepts nothing else as a trigger —
both the X11 and Win32 sources read only its source visual, pointer and modifiers, so the wait does not
invalidate it. Without the threshold, `handledEventsToo` would have made every click on a selected row in
the SFTP remote list stage its whole selection to disk: that list downloads what is selected *before* the
drag begins, to build the file payload ADR-0013 requires. A click on a row holding a 4 GB file would have
fetched it, and left the staging directory behind. It also keeps a plain click and a double-click clear of
a pointer grab.

## Consequences

`StoragePageViewModel`'s private transfer splits in two: `TransferSelectionAsync`, which is what a pane
selection means, and `RunTransferAsync`, which is everything a transfer does once what is moving and where
it lands are both known. The second takes the pane that can expand a folder, the pane that owns the
destination, and the pane the message belongs on — the same pane for a selection, and the dropped-on pane
for a drag from outside, because that is where the user was looking.

## Known limitations and follow-ups

- **The Storage panes still cannot be dragged *out* of the application.** Both remain in-process only. A
  remote row would have to be staged to disk first, the way ADR-0013 stages an SFTP row, and would inherit
  the staging leak ADR-0022 sweeps on exactly one path.
- **The local pane declines an external drop**, so there is no drag-to-copy within the local filesystem.
- **The SFTP page's local pane likewise declines one.** Its remote list accepts files from the file
  manager, which is the direction that carries the gesture's meaning.
- **No conflict resolver on the SFTP side**, unchanged from ADR-0021: on Storage a dropped-in file that
  collides is resolved by the same dialog any upload gets, and on SFTP it fails that one item.

## Amendment to ADR-0021

[ADR-0021](0021-dual-pane-storage-workspace.md) is **amended** where "Dragging is between panes, in
process" describes the whole of it: the remote pane now also accepts a file list from outside the
application, and a drop resolves the hovered folder rather than the receiving pane's open one. Its
observation that nothing is staged to disk still holds — a path dragged in already exists, so the Storage
page still cannot leak a staging directory.
