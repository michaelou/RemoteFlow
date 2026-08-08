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

`.github/workflows/ci.yml` runs the checks above on `windows-latest` for every push to `main` and every
pull request: restore, `Release` build with warnings as errors, and the unit tests. Linux and macOS are not
covered, so a cross-platform change still has to be exercised locally. The SSH integration suite needs a
Linux Docker engine and does not run in CI at all; every run writes that into its summary so a green run is
not read as more than it is. There is no coverage gate — the reviewable rule is that changes to Domain or
Application come with tests.

CI adds trx and coverage reports by appending platform arguments through
`-p:ExtraTestingPlatformCommandLineArguments=...`, which `Directory.Build.targets` appends to any
`TestingPlatformCommandLineArguments` a test project already sets. Use that property rather than setting
`TestingPlatformCommandLineArguments` directly, which would discard the per-project test filters. The
reports land in each project's `bin/<config>/<tfm>/TestResults` and are uploaded as a run artefact.

Still run the local checks before opening a pull request, and record any environment-specific results in
the pull request.
