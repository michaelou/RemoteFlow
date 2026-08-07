@(
@{ Number=45; Milestone='6 - Remote Desktop'
   Title='IRdpLauncher and Windows implementation'
   Labels=@('model:sonnet-5','effort:high','area:rdp','type:feature','risk:contained')
   Body=@'
```yaml
model: claude-sonnet-5
effort: high
risk: contained
depends_on: [10, 13]
blocks: [46, 47]
read_first:
  - src/RemoteFlow.Application/Abstractions/ICredentialProvider.cs
touches:
  - src/RemoteFlow.Application/Abstractions/IRdpLauncher.cs
  - src/RemoteFlow.Infrastructure/Platform/Rdp/**
  - tests/RemoteFlow.Infrastructure.Tests/**
verify: dotnet test tests/RemoteFlow.Infrastructure.Tests
```

## Goal
Launch the platform-native RDP client - the whole of the v1 RDP requirement. **No embedded RDP.**

## Decisions already made - do not re-litigate
- **A password is NEVER written into the `.rdp` file.** The `password 51:b:` field is a DPAPI blob at rest,
  tied to the user profile, and a file that leaks is a credential that leaks. Hand credentials to
  `mstsc` via `cmdkey /generic:TERMSRV/{host}` instead, and remove the entry after launch.
- The default is **do not store the password** at all - let Windows prompt.
- The generated `.rdp` goes in a per-launch temp directory and is deleted after launch, with a
  sweep-on-start for files left by a crash.

## Scope
```
IRdpLauncher
    Task<RdpLaunchResult> LaunchAsync(Connection connection, CancellationToken ct)
    Task<IReadOnlyList<RdpClientInfo>> DetectClientsAsync(CancellationToken ct)
```
`WindowsRdpLauncher`: generate the `.rdp` from `RdpOptions` (resolution, fullscreen, multimon, clipboard
and drive redirection, domain), launch `mstsc.exe`, optional `cmdkey` handoff, cleanup.
All process execution goes through `IProcessRunner` so it is testable.

## Acceptance criteria
- [ ] Launching connects to a real Windows host.
- [ ] **No password appears anywhere in the `.rdp` file** - assert by reading the generated file's full text.
- [ ] The temp `.rdp` is deleted after launch, and stale files from a previous crash are swept at startup.
- [ ] A fake `IProcessRunner` test asserts the **exact** `.rdp` contents and the exact `mstsc` argv for a
      representative connection.
- [ ] `cmdkey` entries created for a launch are removed afterwards.
- [ ] `RdpOptions` map correctly: resolution, fullscreen, multimon, clipboard, drive redirection, domain.
- [ ] `DetectClientsAsync` finds `mstsc.exe` on Windows and reports it with a version.
- [ ] A launch failure returns a typed result with a usable message, not an exception.

## Out of scope
Embedded RDP (an explicit v1 non-goal). RD Gateway. macOS/Linux (#46). Options UI (#47).
'@ },

@{ Number=46; Milestone='6 - Remote Desktop'
   Title='macOS and Linux RDP launchers'
   Labels=@('model:sonnet-5','effort:medium','area:rdp','type:feature','risk:contained')
   Body=@'
```yaml
model: claude-sonnet-5
effort: medium
risk: contained
depends_on: [45]
blocks: []
read_first:
  - src/RemoteFlow.Infrastructure/Platform/Rdp/WindowsRdpLauncher.cs
touches:
  - src/RemoteFlow.Infrastructure/Platform/Rdp/MacOsRdpLauncher.cs
  - src/RemoteFlow.Infrastructure/Platform/Rdp/LinuxRdpLauncher.cs
  - tests/RemoteFlow.Infrastructure.Tests/**
verify: dotnet test tests/RemoteFlow.Infrastructure.Tests
```

## Goal
The same launch behaviour on macOS and Linux, where there is no single canonical client.

## Decisions already made - do not re-litigate
- **macOS**: detect "Windows App" (Microsoft renamed Remote Desktop) and the legacy "Microsoft Remote
  Desktop"; launch via `open -a` with a generated `.rdp`; fall back to an
  `rdp://full%20address=s:host:port&...` URL.
- **Linux**: probe in order `xfreerdp3` -> `xfreerdp` -> `remmina`, with a `$REMOTEFLOW_RDP_CLIENT`
  override. **Pass the password via `/from-stdin`, never on the command line** - argv is world-readable
  through `ps`, so a command-line password leaks to every user on the machine.
- Do not write Remmina profile files - launch the binary.

## Scope
Both launchers, per-client argv construction, dynamic-resolution and clipboard flags, certificate-prompt
guidance, and per-distro install hints when nothing is found.

## Acceptance criteria
- [ ] macOS launches when a client is installed; a missing client produces a message linking the App Store page.
- [ ] The macOS URL fallback percent-encodes correctly (golden test on the generated URL).
- [ ] Linux connects via `xfreerdp3`.
- [ ] **No password appears in `ps` output during a Linux launch** - verified with a live process check,
      not just by inspecting the code.
- [ ] Each supported Linux client has an argv golden test against a fake `IProcessRunner`.
- [ ] The `$REMOTEFLOW_RDP_CLIENT` override is honoured.
- [ ] No client found -> distro-specific install guidance (apt / dnf / pacman), not a generic error.
- [ ] Probing order is deterministic and tested.

## Out of scope
Writing Remmina profiles. Bundling a client.
'@ },

@{ Number=47; Milestone='6 - Remote Desktop'
   Title='RDP options UI and client-detection guidance'
   Labels=@('model:haiku-4.5','area:ui','type:feature','risk:contained')
   Body=@'
```yaml
model: claude-haiku-4-5
risk: contained
depends_on: [18, 45]
blocks: []
read_first:
  - src/RemoteFlow.UI/Views/Connections/ConnectionEditor.axaml
  - src/RemoteFlow.Application/Abstractions/IRdpLauncher.cs
touches:
  - src/RemoteFlow.UI/Views/Connections/RdpOptionsSection.axaml
  - tests/RemoteFlow.UI.Tests/**
verify: dotnet test tests/RemoteFlow.UI.Tests
```

## Goal
The RDP section of the connection editor, plus an inline panel telling the user what to install when no
client is found.

## Scope
Add to the existing editor from #18 - do not create a new dialog:
- Domain, resolution presets plus custom width/height, fullscreen, multimon, clipboard redirection,
  drive redirection.
- A detected-client indicator driven by `IRdpLauncher.DetectClientsAsync()`.
- An inline "no RDP client found" panel with per-OS instructions, shown **only** when detection fails.

Every binding target and validation rule already exists (`RdpOptions` from #4, the editor from #18, the
detection API from #45) - this is form markup plus bindings.

## Acceptance criteria
- [ ] Every option round-trips to `RdpOptions` and reaches the launch (assert via the fake `IProcessRunner`).
- [ ] The client indicator reflects `DetectClientsAsync()` and refreshes when the section is shown.
- [ ] Custom resolution is validated (positive, within sane bounds) and rejects garbage inline.
- [ ] The guidance panel appears **only** when detection finds nothing.
- [ ] The section appears only when the connection's protocol is RDP.
- [ ] The section is keyboard-navigable with a sensible tab order.

## Out of scope
Per-launch overrides. Any launcher logic (#45, #46).
'@ },

@{ Number=48; Milestone='7 - Backup and Restore'
   Title='Backup archive format and versioned manifest'
   Labels=@('model:opus-5','effort:xhigh','area:backup','type:feature','risk:load-bearing')
   Body=@'
```yaml
model: claude-opus-5
effort: xhigh
risk: load-bearing
depends_on: [4, 6]
blocks: [49, 50, 52]
read_first:
  - docs/adr/0008-backup-format.md
  - src/RemoteFlow.Domain/Entities/Connection.cs
touches:
  - src/RemoteFlow.Application/Abstractions/Backup/**
  - src/RemoteFlow.Infrastructure/Backup/**
  - docs/backup-format.md
  - tests/RemoteFlow.Application.Tests/Fixtures/backup-v1-golden.zip
verify: dotnet test tests/RemoteFlow.Application.Tests
```

## Goal
A documented, versioned archive format. **The v1 format is a public compatibility promise** - once a user
has an archive on disk, we owe them the ability to import it forever.

## Decisions already made - do not re-litigate
- Zip container with `manifest.json` plus one JSON file per entity type.
- **`host-keys.json` is included.** Restoring connections without their trusted keys would either silently
  degrade the user's security posture or bury them in mismatch prompts - neither is acceptable.
- Forward-compat rules, stated explicitly in the format doc: **unknown fields are ignored; an unknown
  `formatVersion` is refused.** Ignoring unknown fields is what lets v1 read a v1.1 archive; refusing an
  unknown major version is what stops it from corrupting one.
- Ids are stable across export/import so re-importing is idempotent.

## Scope
```
manifest.json        formatVersion, appVersion, createdUtc, machineName? (opt-out), counts,
                     includesCredentials, credentialKdf { algorithm, m, t, p, salt }
connections.json     folders.json       tags.json      connection-tags.json
settings.json        host-keys.json     credentials.enc (optional, written by #52)
```
Plus `docs/backup-format.md` and a committed v1 golden archive as a test fixture.

## Acceptance criteria
- [ ] The format is documented in `docs/backup-format.md` including the forward-compat rules.
- [ ] A v1 golden archive is committed as a fixture and imports cleanly.
- [ ] Ids are stable across an export/import round trip.
- [ ] A hand-edited manifest with a bogus `formatVersion` is **refused** with a clear message.
- [ ] An archive containing an unknown extra field imports successfully, ignoring it.
- [ ] **No secret material appears anywhere in the plaintext entries** - assert by scanning every
      non-`.enc` entry in a generated archive for known secret values.
- [ ] The archive opens in any standard zip tool.
- [ ] **Adding a new entity to the domain model without updating the backup format FAILS a test** - this
      is the important one; it is what keeps the format honest as the app grows.
- [ ] Round-trip is lossless **property by property**, not by spot check.

## Out of scope
Export and import services (#49, #50, #51). Credential encryption (#52).
'@ },

@{ Number=49; Milestone='7 - Backup and Restore'
   Title='Export service and UI'
   Labels=@('model:sonnet-5','effort:medium','area:backup','type:feature','risk:contained')
   Body=@'
```yaml
model: claude-sonnet-5
effort: medium
risk: contained
depends_on: [8, 48]
blocks: []
read_first:
  - docs/backup-format.md
touches:
  - src/RemoteFlow.Application/Services/BackupService.cs
  - src/RemoteFlow.UI/Views/Backup/ExportView.axaml
  - tests/RemoteFlow.Application.Tests/**
verify: dotnet test tests/RemoteFlow.Application.Tests
```

## Goal
The Export half of the requirement, with scope options and an honest credential toggle.

## Scope
`IBackupService.ExportAsync` with scope: all / a selected folder subtree / selected connections.
Toggles for include-settings, include-host-keys, and include-credentials (gated on #52). Destination
picker, progress, and a result summary naming counts and the output path.

## Acceptance criteria
- [ ] Exporting an empty database produces a **valid** archive, not a crash.
- [ ] A subtree export includes the ancestor folders needed to rebuild the paths - otherwise the import
      cannot reconstruct the tree.
- [ ] The include-credentials toggle is **off by default** and warns when enabled.
- [ ] Selected-connections export includes their tags and their folders' paths.
- [ ] Progress reports for a large database and the operation is cancellable.
- [ ] The result summary states counts per entity type and the file path.
- [ ] Exporting to a path without write permission fails with a clear message and no partial file.

## Out of scope
Scheduled or automatic backups. Import (#50, #51).
'@ },

@{ Number=50; Milestone='7 - Backup and Restore'
   Title='Import inspection and preview'
   Labels=@('model:sonnet-5','effort:high','area:backup','type:feature','risk:contained')
   Body=@'
```yaml
model: claude-sonnet-5
effort: high
risk: contained
depends_on: [48]
blocks: [51]
read_first:
  - docs/backup-format.md
touches:
  - src/RemoteFlow.Application/Services/BackupService.cs
  - src/RemoteFlow.UI/Views/Backup/ImportPreview.axaml
  - tests/RemoteFlow.Application.Tests/**
verify: dotnet test tests/RemoteFlow.Application.Tests
```

## Goal
Tell the user exactly what an import would do **before** it touches anything.

## Decisions already made - do not re-litigate
- **Inspection is strictly read-only** and must never mutate the database.
- **A newer `formatVersion` is refused at inspection time, not mid-import.** Failing after a partial write
  is the worst possible outcome.

## Scope
`InspectAsync(path)` -> `BackupInspection`: version compatibility, per-type counts, whether credentials
are present, and detected conflicts by folder path, tag name and connection identity. Plus a preview UI
showing what Merge and what Replace would each do.

## Acceptance criteria
- [ ] Inspection is read-only - assert the database is byte-identical afterwards.
- [ ] Conflicts are enumerated with human-readable descriptions, not ids.
- [ ] A corrupt or truncated archive fails inspection with a **specific** error naming what was wrong.
- [ ] A newer `formatVersion` is refused **at inspection**, before any write.
- [ ] An archive missing an expected entry (e.g. no `tags.json`) is handled as "none" rather than failing,
      where that is semantically valid.
- [ ] Counts in the preview match what the apply step actually does (cross-check in #51's tests).
- [ ] The preview clearly distinguishes Merge from Replace consequences.

## Out of scope
Applying the import (#51).
'@ },

@{ Number=51; Milestone='7 - Backup and Restore'
   Title='Merge and Replace apply with transactional rollback'
   Labels=@('model:sonnet-5','effort:high','area:backup','type:feature','risk:contained')
   Body=@'
```yaml
model: claude-sonnet-5
effort: high
risk: contained
depends_on: [50]
blocks: []
read_first:
  - src/RemoteFlow.Application/Services/BackupService.cs
touches:
  - src/RemoteFlow.Application/Services/BackupService.cs
  - tests/RemoteFlow.Application.Tests/**
verify: dotnet test tests/RemoteFlow.Application.Tests
```

## Goal
The Merge and Replace requirements, executed so that a failure never leaves a half-imported database.

## Decisions already made - do not re-litigate
- The whole apply runs **inside one transaction**, plus a pre-import copy of the DB file as a belt-and-braces backup.
- `Replace` requires **typed** confirmation, not just a button click - it destroys data.
- Credentials referenced but absent are **reported, never silently dropped**.

## Scope
`MergeStrategy.Merge` with `MergeConflictPolicy` (`PreferLocal` / `PreferImported` / `RenameImported`),
and `MergeStrategy.Replace` (wipe then load). Id-collision handling, folder path rebuilding, tag
de-duplication, and a post-import report.

## Acceptance criteria
- [ ] **Importing an archive into the database it came from is idempotent under `PreferLocal`** - nothing
      duplicates, nothing changes.
- [ ] `RenameImported` produces `"Name (imported)"` and preserves both records.
- [ ] `PreferImported` overwrites local records and reports what it replaced.
- [ ] `Replace` requires typed confirmation and leaves the pre-import `.bak`.
- [ ] **A fault injected mid-import rolls back to the exact prior state** - assert the whole DB is
      byte-identical to before.
- [ ] Folder paths are rebuilt correctly for a deep imported tree.
- [ ] Tags de-duplicate case-insensitively against existing tags.
- [ ] Credentials referenced but absent are reported in the summary.
- [ ] The merge matrix is covered: empty target, identical target, partial overlap, deep folder trees.
- [ ] Unicode and emoji in names, notes and tags survive the round trip.

## Out of scope
Selective per-item import (v2).
'@ },

@{ Number=52; Milestone='7 - Backup and Restore'
   Title='Encrypted credential export and import'
   Labels=@('model:opus-5','effort:xhigh','area:security','type:feature','risk:load-bearing')
   Body=@'
```yaml
model: claude-opus-5
effort: xhigh
risk: load-bearing
depends_on: [12, 48]
blocks: []
read_first:
  - docs/adr/0008-backup-format.md
  - src/RemoteFlow.Infrastructure/Security/Crypto/**
touches:
  - src/RemoteFlow.Infrastructure/Backup/CredentialEnvelope.cs
  - src/RemoteFlow.UI/Views/Backup/**
  - tests/RemoteFlow.Infrastructure.Tests/**
verify: dotnet test tests/RemoteFlow.Infrastructure.Tests
```

## Goal
The requirement's "optional encrypted credential export", done so that a leaked archive is not a leaked
password set.

## Decisions already made - do not re-litigate
- **Reuse the Argon2id and AES-GCM primitives from #12** (`Security/Crypto/`). Do not write a second
  crypto implementation - two implementations means two things to get wrong and only one that gets reviewed.
- Per-record AES-256-GCM with a random 96-bit nonce; key from Argon2id(passphrase, m=64 MiB, t=3, p=1)
  with a random 128-bit salt stored in the manifest.
- **AAD binds each record to its connection id AND the manifest hash.** Without this, an attacker who can
  edit the archive can swap two encrypted records between connections and make you authenticate to the
  wrong host with the wrong secret - the ciphertext still verifies, because GCM alone only proves the
  *bytes* are intact, not *where they belong*.
- KDF parameters are read **from the manifest**, so hardening them later does not orphan old archives.
- **There is no recovery path.** The UI must say plainly that a lost passphrase means unrecoverable
  credentials.

## Scope
`credentials.enc`, the envelope format, a passphrase-strength gate, explicit consent UI on export, and
import that restores secrets into the **target machine's** credential store via `ICredentialProvider`.

## Acceptance criteria
- [ ] Round-trip restores usable credentials into the target OS store.
- [ ] A wrong passphrase fails cleanly **without revealing** the record count or whether the file is valid.
- [ ] **Flipping any single byte of ciphertext or of the manifest fails authentication** (tamper test).
- [ ] **Swapping two records between connections fails** (the AAD binding test).
- [ ] KDF parameters are read from the manifest, so an archive written with different parameters imports.
- [ ] The UI states plainly that a lost passphrase is unrecoverable, before the export runs.
- [ ] A weak passphrase is rejected or requires explicit override.
- [ ] Known-answer tests for the envelope run on all three OSes.
- [ ] The passphrase and derived key buffers are zeroed after use.
- [ ] With the credentials toggle off, `credentials.enc` is **absent** - not present-but-empty.

## Out of scope
Passphrase escrow. Hardware-token wrapping.
'@ },

@{ Number=53; Milestone='8 - Packaging and Release'
   Title='CI: build and test workflow (the first CI in the repo)'
   Labels=@('model:sonnet-5','effort:high','area:build','type:infra','risk:contained')
   Body=@'
```yaml
model: claude-sonnet-5
effort: high
risk: contained
depends_on: [2, 6]
blocks: [54]
read_first:
  - CONTRIBUTING.md
  - .runsettings
touches:
  - .github/workflows/ci.yml
verify: push a branch and confirm all three matrix legs pass
```

## Goal
**The first workflow file in the repository**, per the project's "CI only at Packaging and Release"
decision. Everything it runs should already have been working locally for seven milestones - this issue
transcribes that, it does not invent it.

## Scope
`.github/workflows/ci.yml`:
- Matrix `windows-latest` / `ubuntu-latest` / `macos-latest`.
- `actions/setup-dotnet` pinned to `global.json`.
- Restore, build with warnings-as-errors, run unit tests on all three.
- **Integration tests on the Linux leg only** (Docker is available there), with a visible skip note on the
  other two so a reader does not assume they ran.
- NuGet and build caching; test-result and coverage artefacts.
- Concurrency cancellation for superseded runs; minimal `permissions:`.
- **All actions pinned by commit SHA**, not by tag.

## Decisions already made - do not re-litigate
- **No coverage gate.** A coverage percentage on a project with this much XAML measures the wrong thing.
  The reviewable rule is "changes to Domain or Application come with tests"; XAML may ship untested.

## Acceptance criteria
- [ ] All three legs are green on `main` and on pull requests.
- [ ] Integration tests run on Linux and are visibly skipped elsewhere.
- [ ] A deliberately introduced warning **fails** the build (verify once, then revert).
- [ ] Total wall time is under a stated budget.
- [ ] Every action is pinned by SHA.
- [ ] `permissions:` grants only what is needed (`contents: read` for CI).
- [ ] A superseded run is cancelled rather than left to finish.
- [ ] Test results are uploaded as artefacts and readable from a failed run.

## Out of scope
Release automation (#56). Code signing. Coverage gates.
'@ },

@{ Number=54; Milestone='8 - Packaging and Release'
   Title='Versioning with MinVer and changelog'
   Labels=@('model:haiku-4.5','area:build','type:infra','risk:contained')
   Body=@'
```yaml
model: claude-haiku-4-5
risk: contained
depends_on: [53]
blocks: [55]
read_first:
  - Directory.Build.props
  - CONTRIBUTING.md
touches:
  - Directory.Build.props
  - Directory.Packages.props
  - CHANGELOG.md
  - CONTRIBUTING.md
verify: dotnet build; dotnet run --project src/RemoteFlow.Desktop -- --version
```

## Goal
Tag-driven SemVer and a maintained changelog, so a release artefact can always be traced to a commit.

## Decisions already made - do not re-litigate
- **MinVer, not Nerdbank.GitVersioning** - one package, no config file, git tags are the only source of truth.
- Keep-a-Changelog format, with an `Unreleased` section maintained by convention (documented in CONTRIBUTING).

## Scope
Add MinVer to `Directory.Build.props`; create `CHANGELOG.md`; surface the version in an about box and via
a `--version` flag; document the release-notes convention in CONTRIBUTING.

The plan for each step is already fixed - this is transcription against a loud failure signal
(`--version` either prints the right thing or it does not).

## Acceptance criteria
- [ ] An untagged build produces a prerelease version (e.g. `0.0.0-alpha.0.N`).
- [ ] Tagging `v0.1.0` produces exactly `0.1.0`.
- [ ] `--version` prints the version **and** the commit SHA, then exits 0 without starting the UI.
- [ ] `CHANGELOG.md` has an `Unreleased` section and follows Keep-a-Changelog headings.
- [ ] CONTRIBUTING documents the changelog convention.
- [ ] `dotnet build` still succeeds with warnings-as-errors.

## Out of scope
Automatic changelog commits. Release automation (#56).
'@ },

@{ Number=55; Milestone='8 - Packaging and Release'
   Title='Windows packaging: self-contained publish, portable zip and Inno installer'
   Labels=@('model:sonnet-5','effort:high','area:build','type:infra','risk:contained')
   Body=@'
```yaml
model: claude-sonnet-5
effort: high
risk: contained
depends_on: [54]
blocks: [56]
read_first:
  - Directory.Build.props
touches:
  - src/RemoteFlow.Desktop/**
  - build/windows/**
  - scripts/publish-windows.ps1
verify: run scripts/publish-windows.ps1 then launch the published exe with --version
```

## Goal
A Windows release a user can download and run. **Windows is the only platform that ships signed
artifacts in v1** (user decision) - macOS and Linux remain buildable from source and are documented as
such in #58.

## Decisions already made - do not re-litigate
- **Portable zip + Inno Setup, not MSIX.** MSIX's container semantics complicate Credential Manager access
  and arbitrary local filesystem access - which is exactly what an SSH/SFTP client needs. The container
  model fights the product.
- Self-contained publish, `win-x64` and `win-arm64`. **No trimming, no AOT** (EF Core + Avalonia XAML
  reflection - already set in #2).
- Per-user install by default; uninstall **leaves user data** unless purge is explicitly chosen.

## Scope
Self-contained publish profiles, app icon and file metadata, a portable zip, an Inno Setup installer
(Start-menu entry, optional desktop shortcut, clean uninstall), and a signing hook that no-ops when no
certificate is configured.

## Acceptance criteria
- [ ] **The portable zip runs on a clean Windows 11 with no .NET runtime installed.**
- [ ] The installer installs, launches, and uninstalls.
- [ ] **Uninstall leaves `%APPDATA%\RemoteFlow` intact unless purge is chosen** - a user's connections and
      credentials must not vanish because they reinstalled.
- [ ] `--version` works on the published binary (packaging smoke test).
- [ ] Both `win-x64` and `win-arm64` produce working artefacts.
- [ ] The signing hook no-ops cleanly with no certificate present, and signs when one is supplied.
- [ ] SmartScreen behaviour with an unsigned build is documented for #58.
- [ ] The app icon and file version metadata appear correctly in Explorer.

## Out of scope
MSIX and Store submission. Purchasing a code-signing certificate. macOS and Linux packaging (build from
source; documented in #58).
'@ },

@{ Number=56; Milestone='8 - Packaging and Release'
   Title='Release workflow: tag-triggered artifacts, checksums and draft release'
   Labels=@('model:sonnet-5','effort:medium','area:build','type:infra','risk:contained')
   Body=@'
```yaml
model: claude-sonnet-5
effort: medium
risk: contained
depends_on: [55]
blocks: [58]
read_first:
  - .github/workflows/ci.yml
  - scripts/publish-windows.ps1
touches:
  - .github/workflows/release.yml
verify: push a v0.1.0-rc.1 tag and confirm a draft release with all artefacts and matching checksums
```

## Goal
Tagging produces a reviewable draft release, with nothing published without a human deciding to.

## Decisions already made - do not re-litigate
- Releases are created as **drafts** requiring human publication.
- **No auto-update mechanism and no update pings** - the app has no telemetry and no cloud dependency, and
  a silent updater would contradict both.

## Scope
`.github/workflows/release.yml`: tag-triggered, builds the `win-x64` and `win-arm64` artefacts, generates
`checksums.txt` (SHA-256), runs a launch smoke test per artefact (`--version` on the published binary),
generates release notes from merged PR titles, and creates a draft GitHub Release.

## Acceptance criteria
- [ ] Pushing `v0.1.0-rc.1` produces a draft release with both artefacts.
- [ ] `checksums.txt` matches the uploaded files (verify by recomputing).
- [ ] **The workflow fails if any smoke test fails** - a broken binary must never reach a release page.
- [ ] Re-running the workflow for the same tag is idempotent - it does not create a second release.
- [ ] The release is a **draft**, never auto-published.
- [ ] Release notes are generated and readable.
- [ ] Artefact names follow a documented convention including version and RID.
- [ ] Actions are pinned by SHA; `permissions:` grants `contents: write` only.

## Out of scope
Auto-update. Publishing to winget or any store.
'@ },

@{ Number=57; Milestone='8 - Packaging and Release'
   Title='Third-party attribution, about box, log and data folder access'
   Labels=@('model:haiku-4.5','area:ui','type:feature','risk:contained')
   Body=@'
```yaml
model: claude-haiku-4-5
risk: contained
depends_on: [9]
blocks: []
read_first:
  - src/RemoteFlow.Application/Abstractions/IAppPaths.cs
touches:
  - THIRD-PARTY-NOTICES.md
  - src/RemoteFlow.UI/Views/About/**
  - scripts/generate-notices.ps1
verify: dotnet build; open the about box and confirm every action works
```

## Goal
License compliance and self-service diagnostics, with no network involved.

## Scope
- `THIRD-PARTY-NOTICES.md` generated from the resolved package graph, with a `scripts/generate-notices.ps1`
  to regenerate it.
- An about dialog: version and commit (from #54), the MIT license, repository link.
- **Open log folder** and **Open data folder** actions using `IAppPaths`.
- A crash-report helper that opens the log folder with the last error surfaced - **local only, no upload**.
- A build-time check that fails if a package has an unrecognised license, guarding against accidental
  copyleft ingress into an MIT project.

The target is fully specified and the failure signal is objective - every package either appears in the
notices or it does not.

## Acceptance criteria
- [ ] Every runtime package appears in the notices with its license.
- [ ] The about box shows the MinVer version and the commit SHA.
- [ ] Open-log-folder and open-data-folder work on all three OSes.
- [ ] The license check **fails the build** when given a package with an unrecognised license (verify with
      a temporary fake entry, then revert).
- [ ] The crash helper opens the folder locally and sends nothing over the network.
- [ ] `scripts/generate-notices.ps1` is idempotent - running it twice produces no diff.

## Out of scope
Any network reporting or telemetry - an explicit project non-goal.
'@ },

@{ Number=58; Milestone='8 - Packaging and Release'
   Title='User documentation, keybindings reference and troubleshooting'
   Labels=@('model:sonnet-5','effort:medium','area:build','type:docs','risk:contained')
   Body=@'
```yaml
model: claude-sonnet-5
effort: medium
risk: contained
depends_on: [22, 56]
blocks: []
read_first:
  - docs/keybindings.md
  - docs/backup-format.md
  - docs/manual-test-terminal.md
touches:
  - README.md
  - docs/keybindings.md
  - docs/troubleshooting.md
  - docs/building.md
verify: follow the README install and first-connection steps on a clean machine
```

## Goal
A new user can install RemoteFlow and connect to a host using only the README - and can find out exactly
where their secrets are stored.

## Scope
- **README**: what it is, dark-mode screenshots, feature list, Windows install, and a
  **security posture** section: no telemetry, no cloud, no accounts; what is stored where; what is
  **never** stored; how credentials are held per platform; how host keys are verified.
- `docs/keybindings.md` - completed from #22's data source.
- `docs/building.md` - **building on macOS and Linux from source**, since v1 ships no artifacts for them.
- `docs/troubleshooting.md`: no RDP client found, libsecret missing (and what the passphrase vault means),
  SmartScreen on an unsigned Windows build, the terminal reflow-on-resize limitation, Docker needed for
  integration tests.
- Link `docs/manual-test-terminal.md` for contributors.

## Acceptance criteria
- [ ] A new user can install and connect using only the README (walk it on a clean machine).
- [ ] The security section states plainly what is stored where and what is never stored.
- [ ] **Every documented keybinding matches the keymap data source used by #22's tests** - verified, not
      hand-copied, so the doc cannot drift from behaviour.
- [ ] `docs/building.md` gives working build steps for macOS and Linux.
- [ ] Every troubleshooting entry names a concrete symptom and a concrete fix.
- [ ] **Screenshots are dark-mode** (the app's default).
- [ ] Every internal link resolves.

## Out of scope
A documentation website. Localization (English only in v1).
'@ },

@{ Number=59; Milestone='8 - Packaging and Release'
   Title='Accessibility and keyboard-only pass'
   Labels=@('model:sonnet-5','effort:medium','area:ui','type:feature','risk:contained')
   Body=@'
```yaml
model: claude-sonnet-5
effort: medium
risk: contained
depends_on: [16, 18, 21, 39]
blocks: []
read_first:
  - src/RemoteFlow.UI/Styles/DesignTokens.axaml
touches:
  - src/RemoteFlow.UI/**
  - tests/RemoteFlow.UI.Tests/**
verify: dotnet test tests/RemoteFlow.UI.Tests
```

## Goal
The whole primary workflow is completable without a mouse, and no meaning is carried by colour alone.

## Why this is one cross-cutting issue rather than a line item in each UI issue
Accessibility problems are mostly *composition* problems - focus order across views, a shortcut that works
in one page and not another, a colour cue that is fine in isolation and ambiguous next to its neighbour.
Those only become visible once all four main surfaces exist.

## Scope
Automation names and roles on interactive controls; logical focus order across the shell and every page;
visible focus indicators; a contrast audit against the dark palette; complete keyboard paths to every
primary action; a screen-reader smoke test (Narrator / VoiceOver / Orca).

## Acceptance criteria
- [ ] **Create -> configure -> connect -> transfer a file is completable with the keyboard alone** - the
      headline test.
- [ ] Every actionable control has an accessible name.
- [ ] Contrast is >= 4.5:1 for text and >= 3:1 for UI components, on the dark theme.
- [ ] **No meaning is conveyed by colour alone** - environment badges and tab colours carry an icon or
      text cue (re-verify #16 and #21).
- [ ] The focus indicator is visible on every focusable control against the dark surface.
- [ ] The terminal exposes a sensible accessible role rather than an unlabelled custom control.
- [ ] A screen reader announces navigation between pages.
- [ ] Findings that cannot be fixed in v1 are filed as follow-up issues rather than silently dropped.

## Out of scope
Full WCAG 2.2 AA certification. Localization.
'@ }
)
