# Releasing

A release is a tag plus a human decision. Pushing the tag builds and tests the artefacts and writes a
**draft** release; publishing it is a separate, manual act. Nothing in the automation publishes anything,
and there is no auto-update mechanism and no update ping — RemoteFlow has no telemetry and no cloud
dependency, and a binary that phoned home to ask about versions would contradict both.

## Cutting a release

1. Land everything the release should contain.
2. In a pull request of its own: rename `## [Unreleased]` in `CHANGELOG.md` to the version with its date,
   open a fresh `Unreleased` section, and merge it.
3. Tag that commit and push the tag:

   ```shell
   git tag v0.1.0-rc.1
   git push origin v0.1.0-rc.1
   ```

4. Watch [the Release workflow](../.github/workflows/release.yml) run. It is the gate, not a formality —
   see below.
5. Read the draft release. Edit the generated notes into something worth reading.
6. Work through the manual checks in
   [packaging-windows.md](packaging-windows.md#verifying-a-release-candidate) — installing, uninstalling,
   and confirming your data survives are not things CI can prove.
7. Press **Publish release**.

Tags are written `v<version>`, and MinVer turns `v0.1.0` into exactly `0.1.0`. A version with a
prerelease part (`v0.1.0-rc.1`) is marked as a prerelease on GitHub.

## What the workflow does

`.github/workflows/release.yml`, triggered by any `v*` tag:

| Job | Runner | What it does |
| --- | --- | --- |
| `build (win-x64)` | `windows-latest` | Publishes, packages, and launches the x64 artefacts. |
| `build (win-arm64)` | `windows-11-arm` | The same for ARM64, natively. |
| `draft release` | `ubuntu-latest` | Checksums, release notes, and the draft itself. |

Each architecture builds on a runner of its own architecture. That costs a second runner and buys the
thing the workflow exists for: **every artefact is launched and asked for its version before it can reach
a release page.** A cross-published ARM64 binary cannot be run on an x64 runner, and "we could not test
it" is not a state a release should be in.

The smoke test (`scripts/smoke-test-artifacts.ps1`) extracts the zip and silently installs the installer,
runs `RemoteFlow.exe --version` on each, and requires the output to contain the version from the tag. A
binary that does not start, or a build whose version disagrees with its tag, fails the workflow. Nothing
downstream runs.

Only the `draft release` job can write to the repository, and `contents: write` is the only permission it
holds. Every action is pinned by commit SHA.

### Artefact names

`RemoteFlow-<version>-<rid>` where `<rid>` is `win-x64` or `win-arm64`:

| File | What it is |
| --- | --- |
| `RemoteFlow-0.1.0-win-x64.zip` | Portable, self-contained; no .NET runtime needed. |
| `RemoteFlow-0.1.0-win-arm64.zip` | The same for ARM machines. |
| `RemoteFlow-0.1.0-win-x64-setup.exe` | Per-user installer. |
| `RemoteFlow-0.1.0-win-arm64-setup.exe` | The same for ARM machines. |
| `checksums.txt` | SHA-256 of all four, in `sha256sum` format. |

The version in a filename is read back out of the built binary rather than passed in, so an artefact
cannot be named something other than what it reports.

Users verify a download with:

```shell
sha256sum --check --ignore-missing checksums.txt
```

### Release notes

`scripts/generate-release-notes.ps1` builds them from the commits between the previous tag and this one.
The repository squash-merges, so every commit subject on `main` is a pull request title with `(#123)`
appended; commits that reached `main` without a pull request are listed under their own heading rather
than dropped. Preview them for an existing tag with:

```shell
pwsh ./scripts/generate-release-notes.ps1 -Tag v0.1.0-rc.1
```

The generated text is raw material, deliberately complete rather than curated. Edit the draft before
publishing.

## Re-running a tag

The workflow is idempotent. It creates a release when there is none for the tag and updates the existing
draft when there is, so re-running a failed run repairs the draft instead of adding a second one.

It refuses to touch an **already-published** release. By then a human has decided those artefacts are the
release and people may have downloaded them; replacing files under a published tag would invalidate a
checksum someone has already recorded. If a published release is wrong, tag a new version.

## Running the pieces locally

```shell
pwsh ./scripts/publish-windows.ps1 -RequireInstaller
pwsh ./scripts/smoke-test-artifacts.ps1 -ExpectedVersion 0.1.0-rc.1 -Runtime win-x64
```

`-RequireInstaller` turns the "Inno Setup was not found" warning into an error, which is what the release
workflow wants: shipping zips and no installers because a tool moved is the kind of silent partial success
worth failing on.

The smoke test only checks that the installer **exists** unless you pass `-IncludeInstaller`. That switch
is off by default because installing is not side-effect free: the installer writes the per-user uninstall
entry that identifies an existing RemoteFlow install, so on a machine with RemoteFlow installed it would
repoint that entry at a temporary directory. CI passes it; on a workstation, only do so if you do not have
RemoteFlow installed. The smoke test can only exercise artefacts matching the machine's own architecture,
and says so rather than skipping quietly.

## Signing

There is no code-signing certificate yet, so releases ship unsigned and SmartScreen will call the
publisher unknown. `scripts/sign-windows.ps1` is the only place that knows the difference, and it fails
rather than silently shipping unsigned when a certificate is configured but `signtool.exe` is missing. See
[packaging-windows.md](packaging-windows.md#signing).
