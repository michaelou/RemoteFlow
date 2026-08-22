# Releasing

A release is a tag plus a human decision. Pushing the tag builds and tests the artefacts and writes a
**draft** release; publishing it is a separate, manual act. Nothing in the automation publishes anything.

A published release reaches a machine one of two ways: someone fetches it, or someone presses **Download
and install** on the About tab, which fetches that release's own installer and runs it
([ADR-0018](adr/0018-self-update.md)). Both start with an opt-in check that reads `tag_name` from this
repository's `releases/latest`, and that constrains what a release may be tagged: **the tag has to be a
version this application can compare.** `v0.2.0` and `v0.2.0-rc.1` are; `nightly` is not, and a build that
met it would tell the user it could not compare rather than guess. See
[`SemanticVersion`](../src/RemoteFlow.Application/Services/SemanticVersion.cs).

Because `releases/latest` skips drafts and prereleases, tagging `v0.2.0-rc.1` never offers itself to
anyone running a stable build. A draft release is invisible to the check until it is published, which is
the behaviour to want: the check reflects what people can actually download.

### What the application now parses

Self-update reads two things out of a published release, so both are interfaces rather than conventions,
and the release workflow asserts them:

- **The installer asset name**, `RemoteFlow-<version>-<rid>-setup.exe`. Renaming it does not break the
  release; it breaks self-update for everyone already running an older build, silently, because the button
  simply stops appearing.
- **`checksums.txt`**, in `sha256sum` format: a lowercase 64-character digest, two spaces, a bare filename
  with no directory part. An installer whose digest is not listed there is one RemoteFlow refuses to run,
  so a release missing it offers no install button at all.

Neither is something to change without changing
[`GitHubUpdateChecker`](../src/RemoteFlow.Infrastructure/Updates/GitHubUpdateChecker.cs) and
[`Sha256Checksums`](../src/RemoteFlow.Application/Services/Sha256Checksums.cs) with it.

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
7. On Linux, install the `.deb` with `apt`, launch it from the application menu, and uninstall it. There
   is no Linux equivalent of the Windows smoke test, so this pass is the only thing that proves the
   package installs and the binary starts. Record both checklists in the pull request.
8. Press **Publish release**.

Tags are written `v<version>`, and MinVer turns `v0.1.0` into exactly `0.1.0`. A version with a
prerelease part (`v0.1.0-rc.1`) is marked as a prerelease on GitHub.

## What the workflow does

`.github/workflows/release.yml`, triggered by any `v*` tag:

| Job | Runner | What it does |
| --- | --- | --- |
| `build (win-x64)` | `windows-latest` | Publishes, packages, and launches the x64 artefacts. |
| `build (win-arm64)` | `windows-11-arm` | The same for ARM64, natively. |
| `build (linux-x64)` | `ubuntu-latest` | Publishes the tarball and the `amd64` `.deb`. |
| `build (linux-arm64)` | `ubuntu-24.04-arm` | The same for ARM64, natively. |
| `draft release` | `ubuntu-latest` | Checksums, release notes, and the draft itself. |

Each architecture builds on a runner of its own architecture. That costs runners and buys the thing the
workflow exists for: **every artefact is launched and asked for its version before it can reach a release
page.** A cross-published ARM64 binary cannot be run on an x64 runner, and "we could not test it" is not a
state a release should be in.

On Windows the smoke test (`scripts/smoke-test-artifacts.ps1`) extracts the zip and silently installs the
installer, runs `RemoteFlow.exe --version` on each, and requires the output to contain the version from
the tag. A binary that does not start, or a build whose version disagrees with its tag, fails the
workflow. Nothing downstream runs.

There is no Linux equivalent of that script, and the Linux legs do not pretend otherwise. What they do
check is the version, and they get it for free: `publish-linux.sh` runs the binary it just built, parses
`RemoteFlow --version`, and names the tarball and the package after **what the binary reported** rather
than after the tag. The leg then asserts that those two names — and the `Version` field inside the
package's control record — are the ones the tag implies. A build whose version disagrees with its tag
writes differently-named files and fails there. Installing the package and launching it stays on the
manual list above.

Only the `draft release` job can write to the repository, and `contents: write` is the only permission it
holds. Every action is pinned by commit SHA.

### Artefact names

A release carries **eight assets and a `checksums.txt`**, in two naming shapes.

Windows and the Linux tarballs use `RemoteFlow-<version>-<rid>`:

| File | What it is |
| --- | --- |
| `RemoteFlow-0.3.0-win-x64.zip` | Portable, self-contained; no .NET runtime needed. |
| `RemoteFlow-0.3.0-win-arm64.zip` | The same for ARM machines. |
| `RemoteFlow-0.3.0-win-x64-setup.exe` | Per-user installer. |
| `RemoteFlow-0.3.0-win-arm64-setup.exe` | The same for ARM machines. |
| `RemoteFlow-0.3.0-linux-x64.tar.gz` | Portable Linux build for Intel and AMD machines. |
| `RemoteFlow-0.3.0-linux-arm64.tar.gz` | The same for ARM machines. |

The Debian packages use Debian's own convention, because `apt` and `dpkg` expect it:

| File | What it is |
| --- | --- |
| `remoteflow_0.3.0_amd64.deb` | Debian/Ubuntu package for x64. |
| `remoteflow_0.3.0_arm64.deb` | The same for ARM64. |

A prerelease is written `0.3.0~rc.1` in a package name and `0.3.0-rc.1` everywhere else: Debian sorts `~`
before everything, including the empty string, so without the substitution `dpkg` would rank a release
candidate *above* the release it precedes. Every place in the workflow that builds a package name from
the tag does the same substitution.

`checksums.txt` covers all eight, in `sha256sum` format. The draft job names each of the eight rather than
globbing for them, and fails if the count in `dist` is anything other than eight — a release publishes
what is in that directory, so a stray file is not something to shrug at.

The version in a filename is read back out of the built binary rather than passed in, so an artefact
cannot be named something other than what it reports. See [packaging-linux.md](packaging-linux.md) for
what is inside the Linux artefacts and [packaging-windows.md](packaging-windows.md) for the Windows ones.

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
./scripts/publish-linux.sh --runtime linux-x64
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

Nothing in a release is signed. The Linux artefacts have no detached signature and there is no apt
repository behind them; `checksums.txt` is all a downloader gets, and it proves the file arrived intact
and nothing about who built it. See [packaging-linux.md](packaging-linux.md#signing).

On Windows there is no code-signing certificate yet either, so releases ship unsigned and SmartScreen will
call the publisher unknown. `scripts/sign-windows.ps1` is the only place that knows the difference, and it fails
rather than silently shipping unsigned when a certificate is configured but `signtool.exe` is missing. See
[packaging-windows.md](packaging-windows.md#signing).

An in-app update does not meet that SmartScreen prompt, because a file written by `HttpClient` carries no
Mark-of-the-Web and `CreateProcess` does not consult the attachment service. The mandatory SHA-256 check is
what stands in its place, and [ADR-0018](adr/0018-self-update.md) is explicit that an integrity check is
not an authorship check. Signing is still worth doing.
