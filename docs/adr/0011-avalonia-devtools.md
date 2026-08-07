# ADR-0011: Avalonia 12 developer tooling

- Status: Accepted
- Date: 2026-08-07

## Context

RemoteFlow targets Avalonia 12.1.1. The standalone `Avalonia.Diagnostics` package has no 12.x release,
so carrying its older 11.3 package would introduce a mismatched Avalonia dependency graph.

## Decision

Do not reference `Avalonia.Diagnostics` in the application. Avalonia 12 removed that package; its
supported replacement is `AvaloniaUI.DiagnosticsSupport` with `AttachDeveloperTools` (or the
equivalent app-builder extension) and the standalone Avalonia DevTools. RemoteFlow will not add that
optional tooling and license configuration to normal builds.

## Consequences

Development builds stay aligned on Avalonia 12.1.1 and production packages cannot accidentally ship
the legacy diagnostics assembly. A future team decision to adopt Avalonia DevTools will add its
current support package and license configuration explicitly.
