# ADR-0008: Backup format v1

- Status: Accepted
- Date: 2026-08-07

## Context

Users need a portable backup of their RemoteFlow data, potentially including secrets, without relying on database-file compatibility or platform credential stores.

## Decision

Define backup format v1 as a versioned logical export protected by an Argon2id-derived key and an AES-GCM authenticated-encryption envelope. The envelope records the format version, KDF parameters, salt, nonce, and ciphertext so imports can validate and evolve safely. A user-provided passphrase is required to create or restore an encrypted backup.

Exports use a stable logical schema rather than raw SQLite pages. Import validates authentication and version before changing local state, and presents conflicts before overwrite behavior is chosen.

## Consequences

Backups are portable and tamper-evident, with parameters available for future tuning. Lost passphrases cannot be recovered by RemoteFlow. Format changes need versioned readers and tests with fixed compatibility fixtures.
