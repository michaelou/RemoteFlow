# Contributing to RemoteFlow

Thank you for helping improve RemoteFlow. Keep changes focused, add tests where behavior changes, and document architectural decisions through an ADR when a change establishes a lasting convention.

## Local checks

Run these commands before opening a pull request:

```shell
dotnet build
dotnet test
```

SSH integration tests are opt-in and require a running Docker engine (Docker Desktop on Windows). The
default `dotnet test` run excludes them and does not start or contact Docker. Run the harness explicitly
from the repository root with:

```shell
pwsh ./scripts/run-integration.ps1
```

The harness builds a local Ubuntu/OpenSSH image on first use and reuses one container for the test
collection. Tests use only checked-in fixture credentials and host keys; never reuse them outside tests.
The equivalent direct command is:

```shell
dotnet test tests/RemoteFlow.Ssh.IntegrationTests --filter Category=Integration
```

## Migrations

Database migrations may be squashed until the `v0.1.0` tag. After `v0.1.0`, migrations are append-only: never edit, remove, reorder, or squash a migration that has been released.

## Automation

Continuous integration is intentionally deferred until Milestone 8. Do not add CI workflows before then; contributors run the checks above locally and record any environment-specific results in the pull request.
