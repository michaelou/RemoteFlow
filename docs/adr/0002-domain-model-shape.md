# ADR-0002: Domain model shape

- Status: Accepted
- Date: 2026-08-07

## Context

Connections, terminal sessions, credentials, and environments have related lifecycles but must remain understandable and safely editable. Coupling a session to a saved connection would prevent ad-hoc work and complicate history.

## Decision

Model sessions as independent domain records. A session may record connection metadata or a source reference for convenience, but it is not owned by a connection and remains valid when that connection changes or is deleted. `EnvironmentKind` is a first-class enum, rather than free text, so the supported environment categories are explicit and queryable.

Entities keep stable identifiers, timestamps, and explicit ownership relationships. Credentials are referenced by identifiers and their secret material is handled by the credential subsystem, not duplicated into session or connection records.

## Consequences

The model supports saved and ad-hoc sessions without hidden cascade rules. Enum changes require a migration and deliberate compatibility handling. Some user-defined labels remain free text, but they are not allowed to substitute for `EnvironmentKind`.
