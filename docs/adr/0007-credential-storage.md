# ADR-0007: Credential storage

- Status: Accepted
- Date: 2026-08-07

## Context

RemoteFlow handles passwords, private-key passphrases, and tokens. Storing them with normal application data would expose secrets through database copies and backups.

## Decision

Use the platform credential storage facility on each supported platform through an application abstraction. On Linux, where an accessible system keyring is unavailable, use an encrypted-file vault fallback with a user-controlled unlock secret and authenticated encryption. Store only opaque credential references with normal domain records.

The UI must never log secret values, and adapters return secrets only for the duration of the operation that requires them. Export and backup flows use the dedicated backup envelope rather than copying platform-store internals.

## Consequences

The normal path follows each operating system's security model. Linux remains usable on minimal systems but the fallback introduces unlock, recovery, and secure-memory responsibilities. Tests use fakes and never real personal credential stores.
