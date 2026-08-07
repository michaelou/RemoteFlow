@(
@{ Number=19; Milestone='3 - Embedded Terminal'
   Title='ITerminalChannel contract and Porta.Pty local PTY implementation'
   Labels=@('model:opus-5','effort:xhigh','area:terminal','type:feature','risk:load-bearing')
   Body=@'
```yaml
model: claude-opus-5
effort: xhigh
risk: load-bearing
depends_on: [2, 3]
blocks: [20, 27, 28]
read_first:
  - docs/adr/0005-terminal-stack.md
touches:
  - src/RemoteFlow.Application/Abstractions/ITerminalChannel.cs
  - src/RemoteFlow.Infrastructure/Pty/**
  - tests/RemoteFlow.Infrastructure.Tests/**
verify: dotnet test tests/RemoteFlow.Infrastructure.Tests
```

## Goal
The one contract that lets a single terminal control serve both a local shell and a remote SSH shell,
plus its local implementation.

## Why this is the highest-leverage interface in the app
```csharp
public interface ITerminalChannel : IAsyncDisposable
{
    PipeReader Output { get; }
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct);
    ValueTask ResizeAsync(int columns, int rows, CancellationToken ct);
    Task<int?> Exited { get; }                 // exit code; null if killed
    event EventHandler<ChannelClosedEventArgs>? Closed;
}
```
`PortaPtySession` (local) and `SshShellChannel` (#31, remote) both implement it. The view model binds to
`ITerminalChannel` and knows nothing about SSH or PTY: `channel.Output` -> `model.Feed(bytes)`,
`terminal.UserInput` -> `channel.WriteAsync`. **One control, one view model, two transports, zero
branching** - which is exactly what the transport-agnostic control buys us. Get this wrong and both #20
and #31 are wrong.

## Decisions already made - do not re-litigate
- `PipeReader` for output, not `Stream` or an event - it gives backpressure for free, which #24 needs.
- **`Porta.Pty` 1.0.7, not `Pty.Net`.** `Pty.Net` is unlisted on NuGet (last prerelease 2018) and flagged
  deprecated. Porta.Pty also avoids the .NET 7+ W^X problem: since .NET 7 the runtime enables
  Write-XOR-Execute memory protection by default, which conflicts with `fork()` when managed code runs in
  the child - Porta.Pty keeps managed code out of forked children entirely. That is precisely the bug
  class that breaks older forkpty bindings on modern .NET.
- Windows = ConPTY (Win10 1809+). **winpty is irrelevant for a 2026 app** - do not add it.

## Scope
`IPtyService.SpawnAsync(PtySpawnOptions)` -> `IPtySession : ITerminalChannel`, over
`PtyProvider.SpawnAsync`. Options: shell path, args, working directory, environment variables, initial
cols/rows. Disposal must kill the child **process tree**, not just the direct child.

## Acceptance criteria
- [ ] Spawn a shell, write `echo hi\n`, read `hi` back from `Output`.
- [ ] Resize is observable inside the child: `stty size` on POSIX, `mode con` on Windows, reports the new
      dimensions.
- [ ] Disposing the session kills the process tree with **no orphans** - assert by PID: spawn a shell,
      have it spawn a child, dispose, then assert neither PID is alive.
- [ ] `Exited` completes with the child's exit code on normal exit, and `null` when killed.
- [ ] `Closed` fires exactly once.
- [ ] Works on Windows (ConPTY) and Linux (POSIX). macOS may be `SkippableFact` if #3 deferred it.
- [ ] Cancelling the spawn token mid-spawn leaves no process and no leaked handles.
- [ ] A UTF-8 sequence written in two separate `WriteAsync` calls arrives intact at the child.

## Out of scope
Rendering (#20). SSH (#28, #31). Shell profile discovery (#27).
'@ },

@{ Number=20; Milestone='3 - Embedded Terminal'
   Title='Terminal control host and session view model'
   Labels=@('model:opus-5','effort:xhigh','area:terminal','type:feature','risk:load-bearing')
   Body=@'
```yaml
model: claude-opus-5
effort: xhigh
risk: load-bearing
depends_on: [3, 8, 19]
blocks: [21, 22, 24, 25, 26]
read_first:
  - docs/adr/0005-terminal-stack.md
  - docs/manual-test-terminal.md
  - src/RemoteFlow.Application/Abstractions/ITerminalChannel.cs
touches:
  - src/RemoteFlow.UI/Views/Terminal/TerminalView.axaml
  - src/RemoteFlow.UI/ViewModels/Terminal/TerminalSessionViewModel.cs
  - Directory.Packages.props
  - tests/RemoteFlow.UI.Tests/**
verify: dotnet test tests/RemoteFlow.UI.Tests
```

## Goal
A working, usable local shell inside the app - the first moment RemoteFlow is a terminal at all.

## Decisions already made - do not re-litigate
- `SvcSystems.UI.Terminal` 1.0.3 wrapping `XTerm.NET` 1.0.15, per ADR-0005 as confirmed by #3.
- **`ReflowOnResize = false`.** XTerm.NET issue #12 means the normal buffer has no resize reflow. Full-screen
  TUIs redraw on SIGWINCH anyway, so the only cost is that shell scrollback won't rewrap on window
  resize - an acceptable, documented limitation. The control's own README recommends this to keep
  full-screen TUIs stable.
- The view model binds to `ITerminalChannel`, never to a PTY or SSH type.

## Scope
- `TerminalView` wrapping the control; `TerminalSessionViewModel` bridging
  `channel.Output` -> `model.Feed(bytes)` and `terminal.UserInput` -> `channel.WriteAsync`.
- **UTF-8 decoding that survives chunk boundaries** - keep a stateful decoder across reads; never
  `Encoding.UTF8.GetString` a raw chunk.
- Marshalling PTY reads onto the UI thread correctly, focus handling, and a session-state surface
  (`SessionState` from #4) the tab strip can bind to.
- A clear "session ended (exit code N)" state when the channel closes.

## Acceptance criteria
- [ ] A local shell is fully usable inside the app: type, run commands, see output.
- [ ] **A multi-byte UTF-8 sequence split across two reads renders correctly** - explicit test feeding a
      4-byte emoji as two 2-byte chunks.
- [ ] Channel closure shows an unambiguous ended state including the exit code.
- [ ] Focus behaves: clicking the terminal focuses it; keyboard input reaches the channel.
- [ ] No unobserved task exception when the channel closes while a read is pending.
- [ ] Disposing the view model disposes the channel (no leaked shell process).
- [ ] **`SvcSystems.UI.Terminal` and Avalonia are pinned to exact versions in `Directory.Packages.props`,
      with a comment explaining that the control's floor (Avalonia >= 12.1.1) equals the current ceiling
      (12.1.1) - zero headroom, so every Avalonia bump must be gated on `docs/manual-test-terminal.md`.**
- [ ] Rendering correctness itself is covered by the manual checklist, not by an automated assertion -
      link it from the issue when closing.

## Out of scope
Tabs (#21), keymap (#22), clipboard (#23), resize plumbing (#24), settings (#25), find (#26).
'@ },

@{ Number=21; Milestone='3 - Embedded Terminal'
   Title='Tabbed terminal workspace with environment colour coding'
   Labels=@('model:sonnet-5','effort:high','area:terminal','type:feature','risk:contained')
   Body=@'
```yaml
model: claude-sonnet-5
effort: high
risk: contained
depends_on: [8, 20]
blocks: [34, 59]
read_first:
  - src/RemoteFlow.UI/ViewModels/Terminal/TerminalSessionViewModel.cs
  - src/RemoteFlow.UI/Styles/DesignTokens.axaml
touches:
  - src/RemoteFlow.UI/Views/Terminal/TerminalWorkspace.axaml
  - src/RemoteFlow.UI/ViewModels/Terminal/TerminalWorkspaceViewModel.cs
  - tests/RemoteFlow.UI.Tests/**
verify: dotnet test tests/RemoteFlow.UI.Tests
```

## Goal
The IDE-like workspace the vision calls for: many independent terminal sessions in tabs, with
environment colour coding so a Production tab is never mistaken for a dev one.

## Scope
- Tab strip: add, close, middle-click close, reorder by drag, overflow handling.
- Per-tab session lifecycle; titles from OSC 0/2 escape sequences with a user override.
- Exit indicators on ended tabs; close confirmation when a session is still live
  (`ConfirmCloseActiveSession` setting).
- Shortcuts: `Ctrl+Shift+T` new, `Ctrl+Shift+W` close, `Alt+1..9` switch, `Ctrl+Tab` cycle.
- **Environment colour coding**: tab background/accent from `EnvironmentKind` or `ColorOverrideHex`, plus
  a chrome tint for the active tab. Local (non-connection) sessions get a neutral colour.

## Acceptance criteria
- [ ] Ten concurrent local sessions run independently; output in one does not stall another.
- [ ] Closing a tab disposes its channel - **assert no leaked shell processes** by PID after closing all tabs.
- [ ] Tab reorder persists for the run.
- [ ] Closing the window with live sessions prompts **once**, listing them.
- [ ] A tab title set via OSC 2 updates live; a user override survives subsequent OSC 2 sequences.
- [ ] **A Production tab is unmistakable among ten tabs, and carries an icon or text cue as well as
      colour** - contrast >= 4.5:1 against the dark surface.
- [ ] `Alt+1` switches tabs and **does not reach the PTY** (coordinate with #22's reserved set).
- [ ] Overflow works with 30 tabs without breaking layout.

## Out of scope
Split panes (v2). Session restore across restarts (not in v1). SSH sessions (#34).
'@ },

@{ Number=22; Milestone='3 - Embedded Terminal'
   Title='Keymap service and the keybinding policy'
   Labels=@('model:opus-5','effort:high','area:terminal','type:feature','risk:contained')
   Body=@'
```yaml
model: claude-opus-5
effort: high
risk: contained
depends_on: [20]
blocks: [23, 58]
read_first:
  - docs/adr/0009-keybinding-policy.md
touches:
  - src/RemoteFlow.Application/Services/KeymapService.cs
  - src/RemoteFlow.UI/Input/**
  - docs/keybindings.md
  - tests/RemoteFlow.Application.Tests/**
verify: dotnet test tests/RemoteFlow.Application.Tests
```

## Goal
Every keystroke goes to the right place: the shell gets control characters, the app gets its shortcuts,
and nothing shadows a common TUI key.

## Decisions already made - do not re-litigate (this closes gap A6)
- **Windows/Linux: `Ctrl+C` ALWAYS sends `0x03`.** Copy is `Ctrl+Shift+C` or `Ctrl+Insert`; paste is
  `Ctrl+Shift+V` or `Shift+Insert`. This is the PuTTY/GNOME convention and the only choice that never
  breaks interrupting a runaway process.
- **macOS: `Cmd+C`/`Cmd+V` for clipboard; `Ctrl+C` still SIGINT.**
- Optional `CtrlCPolicy=CopyWhenSelection` (**default off**): copies only when a non-empty selection
  exists, then clears the selection so an immediate second `Ctrl+C` interrupts.
- **App shortcuts are confined to `Ctrl+Shift+*`, `Alt+<digit>`, `Ctrl+Tab` and `F11`.** Everything else
  goes to the PTY. This is what keeps vim, tmux and htop usable.
- The keymap is **data-driven**, so the whole table is unit-testable and the file format is ready for a
  v2 user-editable keymap UI.

## Scope
Per-OS keymap profiles; byte sequences for arrows, Home, End, PgUp, PgDn, Delete, F1-F12, and `Alt` as
ESC-prefix; application-cursor-keys mode; the reserved app-shortcut set; `docs/keybindings.md` generated
from the same data source the tests read.

## Acceptance criteria
- [ ] `Ctrl+C` sends `0x03` and interrupts a runaway `yes` (integration-style test against a real PTY).
- [ ] `Ctrl+Shift+C` copies and does **not** send `0x03`.
- [ ] `Alt+1` switches tabs and does **not** reach the PTY.
- [ ] **Every documented binding has a unit test asserting the exact byte sequence** or the exact command
      it maps to - table-driven over the keymap data.
- [ ] No app shortcut shadows a key vim, nano, tmux or htop needs (assert against an explicit list of
      TUI-critical keys).
- [ ] Arrow keys emit the correct sequence in both normal and application-cursor-keys mode.
- [ ] `docs/keybindings.md` is generated from - or verified against - the same data the tests use, so it
      cannot drift.
- [ ] The macOS profile maps `Cmd+C`/`Cmd+V` and leaves `Ctrl+C` as SIGINT.

## Out of scope
A user-editable keymap UI (v2 - but design the file format for it now). Clipboard plumbing (#23).
'@ },

@{ Number=23; Milestone='3 - Embedded Terminal'
   Title='Copy, paste, selection and bracketed paste'
   Labels=@('model:sonnet-5','effort:high','area:terminal','type:feature','risk:contained')
   Body=@'
```yaml
model: claude-sonnet-5
effort: high
risk: contained
depends_on: [20, 22]
blocks: []
read_first:
  - src/RemoteFlow.Application/Services/KeymapService.cs
touches:
  - src/RemoteFlow.UI/Views/Terminal/**
  - src/RemoteFlow.Application/Abstractions/IClipboardService.cs
  - tests/RemoteFlow.UI.Tests/**
verify: dotnet test tests/RemoteFlow.UI.Tests
```

## Goal
Copy and paste that behaves like a real terminal - including pasting code into vim without it being
mangled by auto-indent.

## Decisions already made - do not re-litigate
- **Bracketed paste is mandatory**, wrapping pasted content in `\e[200~` ... `\e[201~`. Without it,
  pasting indented code into vim insert mode cascades auto-indent and destroys the paste. This is the
  single most user-visible correctness issue in this milestone.
- CRLF is normalised to LF on paste.
- `CopyOnSelect` is a setting, **default off**.

## Scope
Mouse selection (char drag, double-click word, triple-click line), `Ctrl+Shift+A` select-all,
`IClipboardService` (declared in Application, implemented in UI), bracketed paste, newline
normalisation, per-line trailing-whitespace trimming on copy, and a dismissible-with-remember warning
when pasting more than one line or more than 4 KB.

## Acceptance criteria
- [ ] **A multi-line paste into vim insert mode preserves indentation** - the bracketed-paste test.
- [ ] CRLF and lone CR are both normalised to LF on paste.
- [ ] Copy preserves UTF-8 exactly, including wide characters and combining marks.
- [ ] Copy trims trailing whitespace per line but preserves interior spacing.
- [ ] The multi-line/large paste warning appears, and "don't ask again" persists.
- [ ] Double-click selects a word using sensible word boundaries; triple-click selects the logical row.
- [ ] `CopyOnSelect` off by default; when on, selecting copies without a keystroke.
- [ ] Paste when the app lacks clipboard access fails with a message, not an exception.

## Out of scope
Rectangular selection. Paste history. (Both v2.)
'@ },

@{ Number=24; Milestone='3 - Embedded Terminal'
   Title='Resize, cols/rows, output throughput and backpressure'
   Labels=@('model:opus-5','effort:xhigh','area:terminal','type:feature','risk:load-bearing')
   Body=@'
```yaml
model: claude-opus-5
effort: xhigh
risk: load-bearing
depends_on: [20]
blocks: []
read_first:
  - docs/adr/0005-terminal-stack.md
  - src/RemoteFlow.Application/Abstractions/ITerminalChannel.cs
touches:
  - src/RemoteFlow.UI/Views/Terminal/**
  - src/RemoteFlow.UI/ViewModels/Terminal/**
  - docs/manual-test-terminal.md
  - tests/RemoteFlow.UI.Tests/**
verify: dotnet test tests/RemoteFlow.UI.Tests
```

## Goal
The window can be resized without corrupting a TUI, and a flood of output cannot freeze the UI.

## Why this is load-bearing and `xhigh`
Both halves fail **silently**. A wrong resize debounce corrupts htop only under a fast drag; missing
backpressure makes the UI hang only on a large `cat`. Neither shows up in a unit test you'd think to
write, and the measured numbers from #3 are the only baseline available.

## Scope
**Resize:** cell metrics from the font, cols/rows computation, 100 ms debounce, propagate via
`ITerminalChannel.ResizeAsync`, `ReflowOnResize = false`. Document the normal-buffer non-reflow
behaviour in `docs/manual-test-terminal.md` with a screenshot, citing XTerm.NET issue #12.

**Throughput:** coalesce channel reads into <= 16 ms UI batches; cap bytes fed per frame; pause the
`PipeReader` when the UI lags; a drop-and-mark-truncated policy under sustained flood; scrollback trimming.

## Acceptance criteria
- [ ] Resizing the window updates `stty size` inside the shell.
- [ ] `htop` redraws correctly at the new size after a resize.
- [ ] A rapid drag issues **at most one** resize per debounce window (assert the call count).
- [ ] The normal-buffer non-reflow limitation is documented with a screenshot **and confirmed not to
      corrupt subsequent output** - only historical lines.
- [ ] `cat` of a 10 MiB text file keeps the UI responsive and completes; input latency stays acceptable
      throughout.
- [ ] `yes` for 30 s remains interruptible with `Ctrl+C` at any point during the flood.
- [ ] Memory with a 10 000-line scrollback stays within **the budget measured in #3** - a benchmark test
      guards that number so a regression fails rather than degrades quietly.
- [ ] Under sustained flood, dropped output is **marked as truncated** rather than silently discarded.

## Out of scope
GPU rendering. Fixing reflow upstream (tracked separately against XTerm.NET).
'@ },

@{ Number=25; Milestone='3 - Embedded Terminal'
   Title='Terminal appearance settings with live preview'
   Labels=@('model:sonnet-5','effort:medium','area:terminal','type:feature','risk:contained')
   Body=@'
```yaml
model: claude-sonnet-5
effort: medium
risk: contained
depends_on: [6, 20]
blocks: []
read_first:
  - src/RemoteFlow.Application/Abstractions/ISettingsStore.cs
touches:
  - src/RemoteFlow.UI/Views/Settings/TerminalSettings.axaml
  - src/RemoteFlow.UI/ViewModels/Settings/**
  - tests/RemoteFlow.UI.Tests/**
verify: dotnet test tests/RemoteFlow.UI.Tests
```

## Goal
Font, colours, cursor and scrollback are configurable, with a live preview, backed by the settings store.

## Scope
Settings page: monospace-filtered font family picker, font size, cursor style and blink, scrollback line
count, bell mode, and three bundled colour schemes - **dark (default)**, one light, one high-contrast.
Live preview pane.

Depends on what #3 found about the control's colour-scheme and font configurability - if a knob does not
exist, note it and scope that row out rather than working around it.

## Acceptance criteria
- [ ] Changes apply to already-open sessions where the control allows it; otherwise they apply to new
      sessions with a clear note in the UI (no silent no-op).
- [ ] Reducing the scrollback line count re-trims existing buffers.
- [ ] The font fallback resolves to a real monospace font on each OS when the configured one is missing.
- [ ] Invalid values (font size 0, negative scrollback) **clamp** rather than throw.
- [ ] The dark scheme is the default with no settings present.
- [ ] The high-contrast scheme meets >= 7:1 for normal text.
- [ ] Settings round-trip through `ISettingsStore` and survive a restart.

## Out of scope
Importing iTerm2 / Windows Terminal schemes (a good v2 issue).
'@ },

@{ Number=26; Milestone='3 - Embedded Terminal'
   Title='Scrollback and in-buffer find'
   Labels=@('model:sonnet-5','effort:medium','area:terminal','type:feature','risk:contained')
   Body=@'
```yaml
model: claude-sonnet-5
effort: medium
risk: contained
depends_on: [20]
blocks: []
read_first:
  - docs/adr/0005-terminal-stack.md
touches:
  - src/RemoteFlow.UI/Views/Terminal/**
  - tests/RemoteFlow.UI.Tests/**
verify: dotnet test tests/RemoteFlow.UI.Tests
```

## Goal
Scroll back through history and find text in it.

## Scope
Scroll via wheel, keyboard and scrollbar; jump-to-bottom on new output **only when already at bottom**;
`Ctrl+Shift+F` find with match highlight, next/previous, and case/regex toggles.

## Explicit fallback - decide from what #3 found
If XTerm.NET exposes a buffer-search API, use it. If not, implement over a buffer snapshot. **If that
proves infeasible, ship scrollback alone and split find into a follow-up issue** - and record in the
issue which of the three paths was taken. Do not spend the session fighting the control.

## Acceptance criteria
- [ ] 10 000 lines scroll without stutter.
- [ ] **Output arriving while the user is scrolled back does not yank the viewport to the bottom** - the
      single most irritating possible bug in this issue.
- [ ] New output *does* auto-scroll when the viewport is already at the bottom.
- [ ] Find highlights all matches and navigates next/previous.
- [ ] Case-sensitivity and regex toggles behave; an invalid regex shows a message rather than throwing.
- [ ] Which implementation path was taken is recorded in the issue on close.

## Out of scope
Search across all tabs.
'@ },

@{ Number=27; Milestone='3 - Embedded Terminal'
   Title='Local shell profiles and Open in System Terminal'
   Labels=@('model:sonnet-5','effort:medium','area:platform','type:feature','risk:contained')
   Body=@'
```yaml
model: claude-sonnet-5
effort: medium
risk: contained
depends_on: [6, 19]
blocks: []
read_first:
  - src/RemoteFlow.Infrastructure/Pty/PortaPtyService.cs
touches:
  - src/RemoteFlow.Application/Services/ShellProfileService.cs
  - src/RemoteFlow.Infrastructure/Platform/SystemTerminalLauncher.cs
  - tests/RemoteFlow.Infrastructure.Tests/**
verify: dotnet test tests/RemoteFlow.Infrastructure.Tests
```

## Goal
Sensible shell defaults on a clean machine, named profiles for the rest, and an escape hatch to the
user's real terminal - the "Optional Open in System Terminal" requirement.

## Scope
**Shell detection:** POSIX `$SHELL` then `/etc/passwd`; Windows PowerShell 7 (`pwsh`) then Windows
PowerShell then `cmd`.

**Named profiles:** shell path, args, working directory, environment variables, display name, icon.
A default profile; a new-tab-with-profile menu.

**`ISystemTerminalLauncher`:** Windows `wt.exe` -> `pwsh` -> `conhost`; macOS Terminal.app or iTerm2 if
present; Linux `$TERMINAL` -> `x-terminal-emulator` -> `gnome-terminal` -> `konsole` -> `alacritty` ->
`xterm`. Reachable from the tab context menu and the connection details pane. For an SSH connection it
shells out to the **system `ssh` client** with matching user/host/port/key.

## Acceptance criteria
- [ ] Detection picks a working shell on a clean machine of each OS.
- [ ] A profile with a bad executable shows a clear in-tab message, not a crash.
- [ ] Environment variables reach the child (assert via `env` / `set` output).
- [ ] The working directory is honoured.
- [ ] The SSH form produces a correct `ssh` command line per OS - asserted against a fake
      `IProcessRunner` with exact argv, including port and identity-file flags.
- [ ] **A password is never passed to an external client** - not on the command line, not via env.
      There is no supported way to do it safely, so the feature simply does not offer it.
- [ ] No terminal emulator found -> an actionable message naming what to install.

## Out of scope
Per-connection shell overrides.
'@ }
)
