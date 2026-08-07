# Contributing to RemoteFlow

Thank you for helping improve RemoteFlow. Keep changes focused, add tests where behavior changes, and document architectural decisions through an ADR when a change establishes a lasting convention.

## Local checks

Run these commands before opening a pull request:

```shell
dotnet build
dotnet test
```

Integration tests are opt-in and may require local services or credentials. Run them explicitly with:

```shell
dotnet test --filter Category=Integration
```

## Migrations

Database migrations may be squashed until the `v0.1.0` tag. After `v0.1.0`, migrations are append-only: never edit, remove, reorder, or squash a migration that has been released.

## Automation

Continuous integration is intentionally deferred until Milestone 8. Do not add CI workflows before then; contributors run the checks above locally and record any environment-specific results in the pull request.
