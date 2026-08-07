# ADR-0003: EF Core and SQLite persistence

- Status: Accepted
- Date: 2026-08-07

## Context

RemoteFlow is local-first and needs transactional storage with a migration path, simple backups, and reliable development tooling. Desktop operations can overlap through UI events and background work.

## Decision

Use EF Core with SQLite. Obtain an `IDbContextFactory` and create a new context per operation; do not keep a shared context for the application lifetime. Migrations are the schema history. They may be squashed before the `v0.1.0` tag, then become append-only forever.

The database remains an Infrastructure concern behind application-facing repositories or services. Migrations are reviewed with the code that changes the model and are exercised against an existing database where relevant.

## Consequences

SQLite keeps deployment and backup simple, while EF Core provides typed mapping and migrations. Per-operation contexts avoid tracking and thread-affinity bugs. SQLite's concurrency constraints require short transactions and careful retry/error handling; features requiring a server database need a new decision.
