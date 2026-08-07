# ADR-0010: Dark-mode-first theming

- Status: Accepted
- Date: 2026-08-07

## Context

RemoteFlow is used for long-running terminal and operations work, where dark interfaces are common. Scattered literal colors would make accessibility and a future light theme expensive.

## Decision

Design dark mode first and define colors, spacing, typography, borders, elevation, and interaction states as named design tokens. Avalonia resources consume semantic tokens such as surface, text, focus, danger, and terminal selection rather than hard-coded values. Components use those semantic resources, not palette values directly.

Light mode or alternate themes may be added later by supplying another token set; they do not change component structure. Terminal palette behavior remains governed by the terminal-stack decision.

## Consequences

The initial product has a coherent dark experience and accessibility fixes can be centralized. Token naming and resource discipline add small upfront work. New controls must be reviewed for contrast, focus visibility, and state coverage.
