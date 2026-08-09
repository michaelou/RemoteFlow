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

Changes to the terminal stack also need the manual pass in
[docs/manual-test-terminal.md](docs/manual-test-terminal.md): the keyboard, resize, and TUI behaviour that
no automated test covers. UI changes are held to
[docs/accessibility.md](docs/accessibility.md) — every actionable control needs an accessible name, and
the audit test fails the build without one. Building and running on macOS or Linux is documented in
[docs/building.md](docs/building.md), and user-facing failures belong in
[docs/troubleshooting.md](docs/troubleshooting.md).

## Versioning and the changelog

Versions come from git tags, through [MinVer](https://github.com/adamralph/minver). There is no version
number written down in a file to forget to bump:

- An untagged commit builds as a prerelease — `0.0.0-alpha.0.57`, where the last part counts commits since
  the last tag (or the root).
- A commit tagged `v0.1.0` builds as exactly `0.1.0`. The `v` prefix is required; MinVer is configured to
  expect it.
- Every assembly also records the commit, because the SDK appends it to
  `AssemblyInformationalVersion`. Check what a build claims to be with:

```shell
dotnet run --project src/RemoteFlow.Desktop -- --version
```

That prints `RemoteFlow <version> (commit <sha>)` and exits without opening a window. The Settings page has
an About tab showing the same two values, which is what to ask for in a bug report. A tree built without
`.git` reports `commit unknown` rather than failing.

Keep `CHANGELOG.md` current in the same pull request as the change, under `## [Unreleased]`, using the
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) headings (`Added`, `Changed`, `Deprecated`,
`Removed`, `Fixed`, `Security`). Write entries for the person upgrading, not for the person who wrote the
code: what changed for them, and what they have to do about it. Internal refactoring that no user can
observe does not need an entry. Releasing means renaming `Unreleased` to the version with a date, opening a
fresh `Unreleased` section, and tagging that commit. Pushing the tag then builds, tests, and drafts the
release; publishing it stays a manual decision. See [docs/releasing.md](docs/releasing.md).

## Packaging

Windows release artefacts — a portable zip and an installer, for x64 and ARM64 — are built with:

```shell
pwsh ./scripts/publish-windows.ps1
```

The installer step needs [Inno Setup 6](https://jrsoftware.org/isdl.php) and is skipped with a warning
when it is absent, so the zips still build without it. Signing no-ops unless a certificate is configured.
See [docs/packaging-windows.md](docs/packaging-windows.md) for the artefact layout, the signing
configuration, what uninstall does and does not remove, and the SmartScreen behaviour to expect from an
unsigned build.

## Dependencies and licences

Adding or bumping a package means regenerating the third-party notices in the same pull request:

```shell
pwsh ./scripts/generate-notices.ps1
```

Commit `THIRD-PARTY-NOTICES.md` and `build/licenses/package-licenses.txt` with the change. The build fails
when a resolved package is missing from the manifest or carries a licence not on the allow-list, and CI
fails when the notices are stale. See
[docs/third-party-licenses.md](docs/third-party-licenses.md) for what to do when a licence cannot be
identified — the answer is never to guess.

## Migrations

Database migrations may be squashed until the `v0.1.0` tag. After `v0.1.0`, migrations are append-only: never edit, remove, reorder, or squash a migration that has been released.

## Automation

`.github/workflows/ci.yml` runs the checks above on `windows-latest` for every push to `main` and every
pull request: restore, `Release` build with warnings as errors, and the unit tests. Linux and macOS are not
covered, so a cross-platform change still has to be exercised locally. The SSH integration suite needs a
Linux Docker engine and does not run in CI at all; every run writes that into its summary so a green run is
not read as more than it is. There is no coverage gate — the reviewable rule is that changes to Domain or
Application come with tests.

`.github/workflows/release.yml` runs on `v*` tags only. It builds each architecture on a runner of that
architecture, launches every artefact to check it starts and reports the version in the tag, and creates a
**draft** release with checksums and generated notes. It never publishes. See
[docs/releasing.md](docs/releasing.md).

CI adds trx and coverage reports by appending platform arguments through
`-p:ExtraTestingPlatformCommandLineArguments=...`, which `Directory.Build.targets` appends to any
`TestingPlatformCommandLineArguments` a test project already sets. Use that property rather than setting
`TestingPlatformCommandLineArguments` directly, which would discard the per-project test filters. The
reports land in each project's `bin/<config>/<tfm>/TestResults` and are uploaded as a run artefact.

Still run the local checks before opening a pull request, and record any environment-specific results in
the pull request.
