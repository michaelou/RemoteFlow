# Terminal keybindings

This file is generated from `KeymapService.Bindings`. Changes must be made in the keymap data first.

| Platform | Binding | Action |
| --- | --- | --- |
| All | `Ctrl+C` | Send SIGINT (byte 03) |
| All | `Ctrl+Shift+T` | Open a new terminal |
| All | `Ctrl+Shift+W` | Close the active terminal |
| All | `Ctrl+Shift+A` | Select all terminal text |
| All | `Ctrl+Shift+F` | Find in terminal scrollback |
| All | `Ctrl+Shift+K` | Open the command library |
| All | `Ctrl+Tab` | Select the next terminal |
| All | `Ctrl+Shift+Tab` | Select the previous terminal |
| All | `Alt+1` | Select terminal 1 |
| All | `Alt+2` | Select terminal 2 |
| All | `Alt+3` | Select terminal 3 |
| All | `Alt+4` | Select terminal 4 |
| All | `Alt+5` | Select terminal 5 |
| All | `Alt+6` | Select terminal 6 |
| All | `Alt+7` | Select terminal 7 |
| All | `Alt+8` | Select terminal 8 |
| All | `Alt+9` | Select terminal 9 |
| All | `F11` | Toggle full screen |
| All | `F6` | Move focus out of the terminal |
| Windows/Linux | `Ctrl+Shift+C` | Copy selection |
| Windows/Linux | `Ctrl+Insert` | Copy selection |
| Windows/Linux | `Ctrl+Shift+V` | Paste |
| Windows/Linux | `Shift+Insert` | Paste |
| macOS | `Cmd+C` | Copy selection |
| macOS | `Cmd+V` | Paste |
| All | `Up` | Cursor up |
| All | `Down` | Cursor down |
| All | `Right` | Cursor right |
| All | `Left` | Cursor left |
| All | `Home` | Home |
| All | `End` | End |
| All | `PageUp` | Page up |
| All | `PageDown` | Page down |
| All | `Delete` | Delete |
| All | `F1` | F1 |
| All | `F2` | F2 |
| All | `F3` | F3 |
| All | `F4` | F4 |
| All | `F5` | F5 |
| All | `F6 (terminal)` | Send terminal F6 (Shift avoids the app shortcut) |
| All | `F7` | F7 |
| All | `F8` | F8 |
| All | `F9` | F9 |
| All | `F10` | F10 |
| All | `F11 (terminal)` | Send terminal F11 (Shift avoids the app shortcut) |
| All | `F12` | F12 |

All other control-key combinations are sent to the PTY. `Alt` plus text is encoded as an ESC prefix. Ctrl+C sends byte `03` unless the optional, default-off CopyWhenSelected policy is enabled and a selection exists.

## Storage and SFTP pages

These are RemoteFlow's own bindings on the two dual-pane pages. They are **not** part of the terminal
keymap: nothing here is sent to a PTY, and none of it appears in `KeymapService.Bindings`.

The table below describes the local pane on either page, and the remote pane on the Storage page. The SFTP
page's remote list keeps its own longer-standing set — `Enter` to descend, `Backspace` up, `Ctrl+R`
refresh, `F2` rename, `Delete` delete — plus the three pane-level chords in the first three rows, which
work the same on both pages.

| Binding | Result |
| --- | --- |
| `Tab` / `Shift+Tab` | Move between controls, and between the two panes. Deliberately not rebound — see below. |
| `Ctrl+Shift+Left` / `Ctrl+Shift+Right` | Jump straight to the local or the remote pane. |
| `Ctrl+L` | Focus the path box of the pane the keyboard is in. |
| `Enter` | Descend into the selected folder, or transfer the selected files to the other pane. |
| `Backspace` / `Alt+Left` | Up one level. |
| `Alt+Right` | Forward, through the pane's own history. |
| `F5` | Refresh the focused pane. |
| `F7` | New folder in the focused pane. |
| `F2` | Rename. Not on the Storage page's remote pane: object storage has no rename. |
| `Delete` | Delete the selection, after a confirmation that counts what would go. |
| Typing | Type-ahead: jumps to the next row whose name starts with what you typed. |

`Tab` is not rebound, and `F6` is not reused. `Tab` belongs to "move between controls" — hijacking it
would create exactly the keyboard trap `F6` exists to escape — and the two panes are peer controls in
declaration order, so `Tab` already walks from the local list to the remote one. `Ctrl+Tab` and
`Alt+1`–`Alt+9` are the terminal's, and are left alone.

Both grid splitters are reachable by `Tab` and move with the arrow keys once focused.

## Embedded RDP on Windows

The embedded Microsoft RDP control owns keyboard input while its surface has focus. RemoteFlow uses
`KeyboardHookMode = OnRemoteComputer`: ordinary typing, function keys, and Ctrl+C/Ctrl+V belong to the
remote computer. Ctrl+Alt+End is the supported remote equivalent of Ctrl+Alt+Del.

RemoteFlow reserves exactly one unmodified key inside the surface:

| Binding | Result |
| --- | --- |
| `F6` | Move focus from the RDP surface to the selected session tab. |
| `Shift+F6` | Send F6 to the remote computer. |

F6 is handled by a `WH_GETMESSAGE` hook installed only on RemoteFlow's UI thread. It examines only
messages addressed to the hosted RDP window, removes the F6 message before the control sees it, and does
not inject into another process. This is necessary because the hosting spike measured zero
`IOleControlSite::TranslateAccelerator` calls: keys addressed to the child HWND bypass Avalonia entirely.

The following limitations are deliberate and apply while the RDP surface has focus:

| Shortcut | Limitation and reason |
| --- | --- |
| `Ctrl+Tab`, `Ctrl+Shift+Tab`, `Alt+1` … `Alt+9` | RemoteFlow cannot switch tabs because Avalonia never receives these keys. Press F6 first, then use the normal tab shortcut. The key itself remains available to the remote session. |
| `Ctrl+Shift+T`, `Ctrl+Shift+W`, `Ctrl+Shift+F`, `F11`, and other RemoteFlow bindings | They do not run while RDP has focus; their keystrokes go to the remote session. This prevents an app shortcut from silently stealing remote input. |
| `Alt+Tab` | Windows keeps this local in a windowed embedded session, so it is not a reliable remote task switcher. |
| Windows key and Windows-key combinations | The local Windows shell can reserve these in a windowed embedded session even with remote keyboard-hook mode selected. |
| `Ctrl+Alt+Del` | Windows reserves the secure-attention sequence locally. Use Ctrl+Alt+End for the remote computer. |
| `F6` | Reserved by RemoteFlow as the documented keyboard-trap escape. Use Shift+F6 when the remote application needs F6. |

Clicking the RDP surface returns keyboard focus to it. Clicking its tab, or pressing F6, returns focus to
the visibly focused tab. No other RemoteFlow shortcut is intercepted inside the embedded session.

Clipboard text redirection is configured per connection before the session starts. Saving a different
**Share my clipboard** value does not reconfigure an open session; reconnect to apply it. With sharing
enabled in two open RDP sessions, text can move between them through the shared local Windows clipboard.
If either connection disables sharing, that session neither reads nor writes local clipboard text.
RemoteFlow does not add file, image, or rich-text transfer channels.
