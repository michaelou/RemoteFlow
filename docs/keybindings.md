# Terminal keybindings

This file is generated from `KeymapService.Bindings`. Changes must be made in the keymap data first.

| Platform | Binding | Action |
| --- | --- | --- |
| All | `Ctrl+C` | Send SIGINT (byte 03) |
| All | `Ctrl+Shift+T` | Open a new terminal |
| All | `Ctrl+Shift+W` | Close the active terminal |
| All | `Ctrl+Shift+A` | Select all terminal text |
| All | `Ctrl+Shift+F` | Find in terminal scrollback |
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
| All | `F6` | F6 |
| All | `F7` | F7 |
| All | `F8` | F8 |
| All | `F9` | F9 |
| All | `F10` | F10 |
| All | `F11 (terminal)` | Send terminal F11 (Shift avoids the app shortcut) |
| All | `F12` | F12 |

All other control-key combinations are sent to the PTY. `Alt` plus text is encoded as an ESC prefix. Ctrl+C sends byte `03` unless the optional, default-off CopyWhenSelected policy is enabled and a selection exists.
