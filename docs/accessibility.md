# Accessibility

RemoteFlow is usable without a mouse, and nothing it shows depends on a colour being seen. This page
records what that means concretely, what is checked automatically, and what a person still has to check
by hand before a release.

The target is WCAG 2.2 AA for the parts that apply to a desktop application. Full certification is not
claimed.

## Working without a mouse

Every primary action has a keyboard path.

| | |
| --- | --- |
| `Tab` / `Shift+Tab` | Move between controls. The focus ring is a two-pixel light outline; it is drawn only for focus that arrived from the keyboard. |
| `Enter` or `Space` | Press the focused button. |
| `Ctrl+K` | Quick connect. Type, `Enter` to open, `Esc` to dismiss. |
| Arrow keys | Move the sidebar highlight, which changes the page as it goes. |
| `Enter` in the sidebar | Commit the page and hand the keyboard to it, so the page's own controls are the next tab stop. |

In the **connection explorer**: arrow keys walk the tree, `Enter` connects, `F2` renames, and `Delete`
deletes.

Opening the **connection editor** puts the caret in the Name field. Fields run in reading order to
**Save** and **Cancel**; closing the editor returns the keyboard to the tree rather than dropping it.

In the **terminal workspace**: `Ctrl+Shift+T` opens a session, `Ctrl+Tab` and `Ctrl+Shift+Tab` cycle,
`Alt+1`–`Alt+9` jump to one, and `Ctrl+Shift+W` closes the active one. These selection shortcuts include
RDP tabs when embedded desktops are open. Tabs themselves take focus:
`Enter` or `Space` selects, `Delete` closes. Everything else the keymap does not claim belongs to the
terminal — see [keybindings.md](keybindings.md).

**`F6` leaves the terminal.** A terminal has to consume `Tab`, because `Tab` is a byte the remote program
wants; that makes it a keyboard trap unless something else gets you out. `F6` is that something, and it
moves focus to the session's tab, from where `Tab` continues through the rest of the application.
`Shift+F6` still sends the terminal its own `F6`, the same arrangement `F11` already uses for full
screen.

In **SFTP**: the path box accepts a typed path, the file list supports type-to-select, and every toolbar
button is a tab stop with a name rather than an arrow glyph alone.

## What a screen reader hears

Every actionable control has an accessible name. Icon-only buttons — the arrows, the chevron, the close
crosses — carry an explicit one, because a tooltip is not a name and is never read on keyboard focus.

- The terminal announces as a **text** control named for its session and environment, not as an
  unlabelled custom control.
- Terminal tabs announce their title and environment: *"web-01, production"*. RDP tabs additionally
  announce the protocol and live connection status: *"DC01, RDP, production, Connected"*.
- Committing a navigation moves focus into the page region, which is named for the page, so the change
  of page is announced.
- The transfer status line is a polite live region: a transfer finishing is announced without taking
  focus away from whatever is being done.

## Colour is never the only signal

Environment is the one thing a mistake about is expensive, and it is shown in colour — so it always
carries words as well. Explorer badges pair a glyph with `DEV`, `STAGE`, `PROD`, or `CUSTOM`; terminal
tabs show `DEV`, `STG`, `PROD !`, or `LOCAL` beside the coloured chip. An ended session shows a dot *and*
a message. Failed transfers are red *and* say why.

## Contrast

On the dark theme — the default, and the one most people will only ever see:

- Text is at least **4.5:1** against every surface it can sit on.
- The focus ring is at least **3:1** against every surface *and* against the accent-filled primary
  button, which is why the ring is not itself the accent: blue on blue measured 2.1:1.
- Control outlines are at least **3:1** against the surface behind them.

## What is checked automatically

`dotnet test tests/RemoteFlow.UI.Tests` enforces the parts that rot silently:

| Test | What it stops |
| --- | --- |
| `AccessibleNameAuditTests` | Reads every view as XML and fails on any button, box, or list that would reach a screen reader unnamed. A new icon button cannot be merged without a name. |
| `DarkPaletteContrastTests` | Measures every palette pair, including the composited control outline from the theme, so a palette tweak or a theme upgrade cannot quietly drop below the floor. |
| `KeyboardAndScreenReaderTests` | Enter presses a focused button; navigation hands focus to the page; terminal tabs are focusable and named; the terminal surface has a role; every environment carries text. |
| `ConnectionEditorTests` | Opening the editor puts the keyboard in the first field. |

## What still has to be done by hand

Automation cannot hear a screen reader. Before a release, on the dark theme:

1. **Windows / Narrator.** `Ctrl+Win+Enter`. Tab through the sidebar, Connections, the editor, the
   terminal, and SFTP. Every stop should say what it is and what it does. The terminal should announce as
   a text area naming its session.
2. **macOS / VoiceOver** and **Linux / Orca.** The same walk. Both platforms build from source — see
   [building.md](building.md).
3. **The whole job, keyboard only.** Unplug the mouse and do it end to end: create a connection,
   configure it, connect, open SFTP, and transfer a file. Any point where the keyboard cannot continue is
   a bug, not a technique to learn.
4. **Zoom to 200%** and confirm nothing is clipped or unreachable.

Record what you found in the pull request, the way
[manual-test-terminal.md](manual-test-terminal.md) does for the terminal.

## Known gaps

These are real, and each is tracked as its own issue rather than left implied:

- The terminal exposes a role and a name, but not its **contents**. A screen reader cannot read the
  scrollback or follow output as it arrives — [#70](../../issues/70).
- There is **no high-contrast or forced-colours mode**: RemoteFlow keeps its own palette even when the
  operating system asks for the system one — [#71](../../issues/71).
- **Tab order is not verified against visual order.** Most views rely on declaration order; the connection
  editor sets it explicitly. Nothing checks either against the layout — [#72](../../issues/72).
- Localization is out of scope for v1: every string above is English.
