# ADR-0005: Embedded terminal stack

- Status: **Provisional - pending the terminal spike (#3)**
- Date: 2026-08-07
- Decision owners: RemoteFlow maintainers
- Related issue: [#3](https://github.com/michaelou/RemoteFlow/issues/3)
- Supported platform: Windows

## Context

RemoteFlow needs an embedded terminal that can render full-screen terminal applications, preserve terminal keyboard semantics, resize a PTY, and sustain large output without freezing the UI. There was no project evidence that an Avalonia-native terminal control met those requirements.

The candidate stack is:

- Avalonia Desktop 12.1.1
- SvcSystems.UI.Terminal 1.0.3, backed by XTerm.NET 1.0.15
- Porta.Pty 1.0.7 (ConPTY on Windows and a native PTY on Linux/macOS)

The manual harness is `tools/RemoteFlow.TerminalSpike`. It deliberately contains direct wiring rather than production abstractions.

## Decision

**Provisional choice, pending issue #3:** evaluate SvcSystems.UI.Terminal over XTerm.NET with Porta.Pty as the current candidate terminal stack. The Windows harness supplies evidence for the decision; it is not a production abstraction. RemoteFlow must not claim Linux or macOS terminal support unless a separate validation issue produces evidence and this ADR is amended.

If the spike rejects this stack, the pre-approved fallback investigation is xterm.js in a WebView. Vendoring XTerm.NET is a last resort: it would require maintaining a source fork, syncing upstream fixes and security patches, owning Avalonia integration and test coverage, publishing or consuming a private package, and carrying compatibility work for every .NET/Avalonia upgrade. That ongoing maintenance cost is higher than using the package and must be justified by a concrete blocking defect.

## Results

The Windows implementation received an overall owner acceptance on 2026-08-07. Individual criteria were not separately evidenced, so they are recorded as `ACCEPTED*` rather than `PASS`. Linux is `OOS` (out of scope).

| Area | Criterion | Windows | Linux | Evidence / notes |
| --- | --- | --- | --- | --- |
| TUI | vim: 500 lines, movement, numbers, visual block, substitution, save/quit; 256-color and truecolor | ACCEPTED* | OOS | Detailed evidence waived. |
| TUI | nano: edit/save, Ctrl+O, Ctrl+X, help bar, Ctrl+W | ACCEPTED* | OOS | Detailed evidence waived. |
| TUI | tmux: split/switch, borders, copy-mode, detach/attach | ACCEPTED* | OOS | Detailed evidence waived. |
| TUI | htop: 60 seconds of 1-second redraws, colors, meters, F-keys | ACCEPTED* | OOS | Detailed evidence waived. |
| TUI | less: 100,000 lines, paging, search, G, q | ACCEPTED* | OOS | Detailed evidence waived. |
| TUI | Alternate screen restores the normal buffer exactly | ACCEPTED* | OOS | Detailed evidence waived. |
| Encoding | CJK, combining accent, box drawing, and emoji | ACCEPTED* | OOS | Emoji width mismatch remains non-blocking. |
| Encoding | UTF-8 sequence split across PTY read boundaries | ACCEPTED* | OOS | Detailed evidence waived. |
| Keyboard | Ctrl+C (`03`), Ctrl+D, Ctrl+Z | ACCEPTED* | OOS | Harness displays emitted bytes. |
| Keyboard | Arrows, Home, End, PgUp, PgDn, Delete in vim insert mode | ACCEPTED* | OOS | Detailed evidence waived. |
| Keyboard | Alt+key is ESC-prefix; F1-F12 | ACCEPTED* | OOS | Detailed evidence waived. |
| Keyboard | Bracketed multiline paste in vim | ACCEPTED* | OOS | Detailed evidence waived. |
| Resize | PTY resize reaches the child process | ACCEPTED* | OOS | Windows uses ConPTY; `ReflowOnResize` is disabled. |
| Resize | Historical normal-buffer reflow gap is isolated from subsequent output | ACCEPTED* | OOS | Known risk accepted. |
| Performance | Sustained output without an unacceptable UI freeze | ACCEPTED* | OOS | No benchmark number was captured. |
| Performance | 10,000-line scrollback memory use | ACCEPTED* | OOS | No hard memory threshold was established. |

`ACCEPTED*` records the owner's overall Windows acceptance and risk waiver. It must not be represented later as row-level test evidence.

## API findings

These answers are established from the 1.0.3 public API and exercised by the harness.

| Question | Answer |
| --- | --- |
| What does `TerminalControlModel.UserInput` yield? | `TerminalUserInputEventArgs.Data` is `ReadOnlyMemory<byte>`. The control interprets Avalonia key events and emits the corresponding terminal byte sequence (UTF-8 text, control bytes, or VT escape sequences). It is terminal-encoded bytes, not raw physical key events. The harness forwards the bytes unchanged and displays their hex value. |
| Selection and clipboard hooks? | Yes. `SelectedText`, `HasSelection`, `CopySelectionAsync`, `PasteFromClipboardAsync`, `RightClickAction`, and `ContextRequested` are available. The harness exposes copy/paste and copy-or-paste right click. |
| Buffer search? | Yes. Search is buffer-based through `Search`, `SelectNextSearchResult`, and `SelectPreviousSearchResult`. The harness exposes find/next. |
| Color and font configuration? | Yes. `FontFamily`, `FontSize`, caret/selection brushes, and resource keys for the 256-color palette are exposed. Truecolor is rendered directly from terminal RGB values. |
| Resize propagation? | Yes. `TerminalSizeChangedEventArgs` contains columns/rows and the harness calls `IPtyConnection.Resize`, which uses ConPTY on supported Windows versions. |

## Evidence format

Future regression evidence belongs under `docs/evidence/terminal/`. Exported metric snapshots should be copied from `artifacts/terminal-spike/` into the evidence bundle when they support a bug report or benchmark change.

## Reconsideration rule

- A hard Windows failure in TUI correctness, keyboard handling, or throughput must open a tracking issue immediately.
- Two or more hard Windows failures supersede this ADR and trigger an xterm.js-in-WebView spike.
- A single failure with a viable workaround may retain this decision if the workaround and upstream issue are documented.
- Issue #24 must establish its own initial benchmark threshold because this acceptance did not capture a measured throughput threshold.

## Other platforms

Linux and macOS are unsupported. Before claiming support for either, open a follow-up validation issue, run the complete platform matrix, and amend this ADR with evidence.

## Consequences

The project can prototype a capable terminal without committing production code to an unvalidated dependency stack. Issue #3 must provide the acceptance evidence needed to accept, amend, or reject this ADR. Until then, downstream terminal work must keep the integration replaceable and must not treat the harness observations as a final platform-support commitment.
