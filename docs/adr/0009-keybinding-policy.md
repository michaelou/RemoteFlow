# ADR-0009: Terminal keybinding policy

- Status: Accepted
- Date: 2026-08-07

## Context

An embedded terminal shares keyboard shortcuts with a desktop application. Intercepting terminal control keys for application actions breaks established shell and TUI expectations.

## Decision

Within terminal focus, `Ctrl+C` always sends SIGINT (the terminal control byte `03`) to the remote or local process. Copy uses `Ctrl+Shift+C`; paste uses the corresponding non-conflicting terminal command, subject to the terminal control's clipboard policy. Application shortcut routing must yield to the terminal for keys required by terminal semantics.

Any exception must be explicit, documented, and tested with an interactive terminal application. Visual keyboard-shortcut hints must reflect the effective focus context.

## Consequences

Shells and TUIs behave predictably and users can reliably interrupt commands. The application cannot use bare `Ctrl+C` for copy while the terminal is focused. Cross-platform keyboard mapping requires regression tests with terminal byte-level evidence.
