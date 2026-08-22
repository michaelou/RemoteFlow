# ADR-0021: The dual-pane Storage workspace

- Status: Accepted
- Date: 2026-08-22

## Context

[ADR-0019](0019-object-storage-provider-abstraction.md) put the provider adapters in place and
[ADR-0020](0020-chunked-object-storage-transfers.md) put a chunked transfer engine behind them. Neither
had a screen. Moving a 4 GB object still meant a file picker and a trip to the Transfers page.

The SFTP workspace is single-pane and remote-first, and [ADR-0013](0013-sftp-workspace.md) said a dual-pane
mode could arrive later without changing the SFTP or transfer contracts. This is that later, and the
prediction held: nothing in `TransferEngine`, `TransferContracts.cs`, `TransfersPageViewModel.cs` or
`SftpWorkspaceViewModel.cs` changed.

## Decision

### One `FileBrowserPane`, instantiated twice

Bound as `{Binding Local}` and `{Binding Remote}` over an `IFileBrowserSource`. A shared base class that
`SftpWorkspaceViewModel` also derived from would mean editing that viewmodel, which this milestone does
not do; copy-pasting its 1,271 lines is not an option either.

Carried over and re-implemented generically rather than extracted: breadcrumbs, back and forward history
with their can-execute flags, the directory-first stable sort tie-broken on original index, type-ahead
selection by prefix, the 250 ms busy-indicator anti-flicker delay, and the inline error, feedback and
drop-target messages.

Deliberately **not** carried over: the Permissions and Owner columns and their mode formatting, which mean
nothing for a key; `SftpPath.ToShellLiteral`, likewise; the dot-file hidden filter, because the correct
local test is `FileAttributes.Hidden` and the object pane hides the toggle entirely; and inline rename on
the remote pane, because S3 has no rename.

### The local pane goes through `LocalFileBrowserSource` in Application

Not `System.IO` in the viewmodel. The behaviour is not trivial — a mid-enumeration
`UnauthorizedAccessException` on something like `C:\System Volume Information` has to yield a partial page
rather than blanking the pane, plus hidden-attribute filtering, drive roots against `/`, and `GetParent` at
a root — and in Application it gets plain `[Fact]` coverage instead of an Avalonia harness. `System.IO` is
the base class library, so the dependency-direction tests stay green. `System.IO` in the pane would also
make the pane non-generic, and there would be two pane classes again.

### `IFileBrowserSource` owns path handling

`Combine`, `GetParent`, `GetName`, `IsValidPath`, `GetBreadcrumbs`. That is what lets one pane serve
`C:\Users\andreas` and `media-prod/2024/`, and it is exactly why `SftpWorkspaceViewModel`'s
`path[0] == '/'` validation is not carried over: it rejects every Windows local path.

### The Storage page embeds the existing `TransfersPageViewModel` singleton, unfiltered

Under a header that says "All transfers". A second queue would mean two independent three-slot gates — six
concurrent transfers with neither aware of the other — and a duplicate of 523 tested lines. Filtering to
this session would require a per-session tag on `TransferQueueRequest`, which means editing the
provider-blind queue this reuse exists to avoid, plus a filtered collection view Avalonia does not hand you
for `ObservableCollection`. The sidebar status bar already shows this singleton on every page, so users
already experience it as *the* queue.

Accepted consequence: clearing completed from either surface clears both. A test asserts
`StoragePageViewModel.Transfers` is reference-equal to the injected singleton, so nobody quietly adds a
second one.

### A production `ITransferConflictResolver` ships here for the first time

Split the way `RemoteEditConflictResolver` already splits: an Avalonia-free `BatchTransferConflictResolver`
holding policy, so it is plain `[Fact]` testable, and a dialog service owning window construction and the
UI thread.

**"Apply to all" is a scope, not a decision.** `TransferConflictDecision` stays `{ Skip, Overwrite, Cancel }`.
One resolver instance per user gesture holds the count and the sticky answer, so the object's lifetime
*is* the batch. `AsyncLocal` cannot work: `QueueAsync` fires and forgets, so the gesture's call stack has
returned long before the queued transfers ask. A `BatchId` on `TransferConflict` would change an
Application contract to serve a UI affordance.

**The default decision is `Overwrite`**, because a put is atomic and idempotent in both providers, there is
no partial-object state to protect the way SFTP's rename-aside dance protects one, and a user who dropped a
file onto a prefix that already holds that key overwhelmingly means replace. It is the
`StorageConflictDefault` *setting* rather than a constant precisely because an unversioned bucket makes
overwrite unrecoverable.

**A null resolver still yields `Conflict`.** Fail closed, exactly as `TransferEngine` does. Nothing was
wired into `SftpWorkspaceViewModel`: SFTP behaviour is unchanged and
`TransferEngineTests.ExistingTargetRequiresResolverAndNeverClobbersByDefault` stays green. Wiring one there
is a separate issue with its own UX decision.

### Pagination by continuation token at the port, `IAsyncEnumerable` on top

The page boundary is exactly what the UI has to expose as "Load more" and be able to stop at, and it maps
one to one onto both providers' listing calls. `EnumerateRecursiveAsync` sits on top of it for the walks
where paging is not user-visible — delete plans and folder-transfer counts.

**A hard cap of 10 pages / 10,000 rows per prefix.** At the cap the Load-more button is replaced by a
non-actionable row: "10,000 of many shown. Narrow the prefix, or use the path box to go deeper — this view
does not load an entire bucket." Handing a `ListBox` a materialised list over a 500,000-key prefix is the
one thing this design must not do.

**Never fake a total.** S3 cannot cheaply count a prefix, so the message says "of many", never
"100,000 items". Sorting a truncated listing sorts only what is loaded, and the sort-header tooltip changes
to say so — this is the thing most dual-pane cloud browsers get silently wrong.

**The list is sorted as a plain array before it reaches the observable collection.** Clearing and re-adding
10,000 rows is 20,000 change notifications on the UI thread, which `VirtualizingStackPanel` does not fix.
No bulk collection type until profiling asks for one.

**The local pane uses the identical paging path** with a synthetic index token and the same cap, so a
`node_modules` holding 200,000 files behaves like a 200,000-key prefix and the pane has zero
source-specific branches.

### The filter box is server-side prefix narrowing, labelled "Starts with"

Both providers support a prefix and neither supports a substring search; offering a search the provider
cannot do is worse than not offering one. Typing re-lists with `prefix + filterText` — one request instead
of a hundred. `ObjectStoragePaging` gained an optional `NamePrefix` for it rather than the browser source
mangling the path, because a path with a partial name appended is a *folder* to both adapters and would
list nothing.

### Keyboard

`F5` refresh, `F7` new folder, `Delete` delete (confirmation-gated), `Enter` to descend or to transfer to
the other pane, `Backspace` and `Alt+Left` up, `Alt+Right` forward, `F2` rename on the local pane only,
`Ctrl+Shift+Left` and `Ctrl+Shift+Right` to jump panes, `Ctrl+L` for the path box, plus type-ahead.

**`Tab` is not bound.** [docs/accessibility.md](../accessibility.md) gives it to "move between controls",
and hijacking it creates exactly the keyboard trap `F6` exists to escape. Because the two panes are peer
controls in declaration order, `Tab` already walks local to remote for free. `F6` is not reused —
[ADR-0009](0009-keybinding-policy.md) makes it mean "escape the keyboard trap" application-wide — and
neither are `Ctrl+Tab` or `Alt+1`/`Alt+2`, which the terminal claims. ADR-0009's rules are scoped to
terminal focus and `docs/keybindings.md`'s function-key rows describe keys sent to the PTY, so `F5`, `F7`
and `F2` are free. These keys are documented by hand in `docs/keybindings.md` and are deliberately *not* in
`KeymapService.Bindings`, which is the terminal keymap.

### The accessible-name trap

One pane control used twice makes both Refresh buttons announce the same name: `AccessibleNameAuditTests`
passes and a screen-reader user is lost. Every actionable control binds `AutomationProperties.Name` to a
pane-scoped string derived from one `PaneName` property, so the two read "Refresh the local folder" and
"Refresh the remote prefix". A test asserts `Local.RefreshLabel != Remote.RefreshLabel`. Both list boxes
and both grid splitters are named too, the splitters with help text because a keyboard user can move them
with the arrow keys once focused.

The pane's `ListBox` is explicitly `Focusable="True"`. The Fluent list is not focusable by default — focus
belongs to the item containers — so a pane jump into an empty pane would otherwise silently do nothing.

### Layout

`Grid RowDefinitions="Auto,*,6,180,Auto"`. **The transfer row is a fixed pixel height, not `Auto`**: a
`GridSplitter` needs a pixel or star neighbour or it is inert. Transfer buttons live on each pane's toolbar
— "Upload" on the local pane, "Download" on the remote one — not in a centre column of arrows, which steals
width at every window size and lands awkwardly in tab order between two lists.

The inline error banner uses a new `Color.Danger.Surface` token with its own contrast-test entry, rather
than copying the hard-coded `#33D43B3B` in `SftpWorkspace.axaml`. A translucent fill has no measurable
contrast ratio; an opaque token does.

### Dragging is between panes, in process

The drag payload is a `DataFormat.CreateInProcessFormat<FileBrowserPaneViewModel>` carrying the pane the
rows came from; the receiving pane asks it to run its own transfer, which already knows where the other
side points. Nothing is staged to disk, so the Storage page needs no staging directory and cannot leak one.

## Consequences

`ConnectionOpenMode.Default` and `ConnectionOpenMode.Storage` now navigate to the Storage page and attach,
from every entry point — double-click, the explorer's context menu, the command palette — through the one
seam in `SshConnectionSessionOpener`.

A new `NavigationCompositionTests` builds the container the desktop host builds and resolves every
`NavigationPageRegistration.Factory`. `ProjectSmokeTests` only asserted an assembly name, so until now a
missing registration was a runtime crash on first navigation.

It runs as an `[AvaloniaFact]` rather than a `[Fact]`, which is not cosmetic:
`TerminalSettingsViewModel`'s constructor reads `FontManager.Current.SystemFonts`, and touching an Avalonia
global from a pool thread is the exact hazard `TestAppBuilder.cs` documents. Building the real container
off the dispatcher thread perturbed text measurement for every headless test that ran afterwards, which
surfaced as an unrelated SFTP hit-test failure three runs in five.

## Known limitations and follow-ups

- **No "this session only" filter on the transfer queue**, and no queue-wide aggregate byte totals — see
  ADR-0020.
- **The SFTP drag-out staging leak is untouched.** `SftpWorkspace.axaml.cs:146` stages into a temp
  directory nothing sweeps. It is deliberately not replicated here and is its own follow-up.
- **No remote editing of objects**, no tree-view navigation, and no server-side copy or rename.
- **The page view models are `IAsyncDisposable` only.** The container refuses to dispose one of those from
  its synchronous path, which the new composition test has to work around with `await using`. That shape
  predates this page; making it uniform is its own change.

## Amendment to ADR-0013

[ADR-0013](0013-sftp-workspace.md) is **amended, not superseded**. Its prediction that a dual-pane mode
could arrive without changing the SFTP or transfer contracts held. The SFTP workspace stays single-pane and
remote-first, and remains governed by it.
