# Windows packaging

Windows is the only platform RemoteFlow ships prebuilt artefacts for in v1. macOS and Linux build from
source; that is documented for users separately.

## Building the artefacts

```shell
pwsh ./scripts/publish-windows.ps1
```

Everything lands in `artifacts/`, which is git-ignored:

| Artefact | Contents |
| --- | --- |
| `RemoteFlow-<version>-win-x64.zip` | Portable build for Intel and AMD machines. |
| `RemoteFlow-<version>-win-arm64.zip` | Portable build for ARM machines (Snapdragon, Surface Pro X and later). |
| `RemoteFlow-<version>-win-x64-setup.exe` | Installer for x64. |
| `RemoteFlow-<version>-win-arm64-setup.exe` | Installer for ARM64. |

Useful switches: `-Runtime win-x64` to build one architecture, `-SkipInstaller` to produce only the zips
(also what happens automatically, with a warning, when Inno Setup is not installed), `-RequireInstaller`
to turn that warning into an error, and `-KeepPublishOutput` to leave the intermediate publish tree in
`artifacts/publish/` for inspection.

The version in every filename is read back out of the built binary rather than passed in, so an artefact
cannot be named something other than what it reports. The script runs `--version` on each binary it can
execute — a cross-architecture build is published but not run, and says so.

### What is in the zip

A self-contained publish: the .NET runtime is inside it, so it runs on a clean Windows 11 with no runtime
installed. Around 300 files, roughly 80 MB compressed. Trimming and AOT are deliberately off — EF Core
and Avalonia's XAML loader both rely on reflection a trimmer cannot follow. Extract anywhere and run
`RemoteFlow.exe`; nothing is written outside `%APPDATA%\RemoteFlow` and `%LOCALAPPDATA%\RemoteFlow`.

## The installer

Inno Setup, not MSIX. MSIX's container semantics restrict Credential Manager access and arbitrary local
filesystem access, which is exactly what an SSH and SFTP client exists to do.

Build it with [Inno Setup 6](https://jrsoftware.org/isdl.php):

```shell
winget install --id JRSoftware.InnoSetup
```

The script finds `ISCC.exe` on `PATH` or under `Program Files`. Without it, the zips are still produced
and a warning explains what is missing.

Behaviour:

- **Per-user.** `PrivilegesRequired=lowest`, so it installs to `%LOCALAPPDATA%\Programs\RemoteFlow` with
  no elevation prompt and cannot touch another account's files.
- A single Start-menu shortcut, not a folder containing one shortcut. A desktop shortcut only if the user
  ticks the box, which is unticked (`/TASKS=desktopicon` for an unattended install that wants one).
- Upgrades in place, recognised by a fixed `AppId` that must never change.

### Uninstall keeps your data

Connections, settings, trusted host keys, and credential references live in `%APPDATA%\RemoteFlow`.
Uninstalling removes the program and leaves that directory alone. Losing a connection list because of a
reinstall would be silent data loss caused by a routine action, so it takes an explicit choice:

- Interactive uninstall asks whether to delete the data as well. The default answer is **No**.
- Silent uninstall (`/SILENT`, `/VERYSILENT`) never deletes it unless `/PURGEDATA` is also passed.

## Signing

There is no code-signing certificate yet, so every path works unsigned. `scripts/sign-windows.ps1` is the
only place that knows the difference:

| `REMOTEFLOW_SIGN_THUMBPRINT` | Result |
| --- | --- |
| unset | Prints that signing was skipped, and succeeds. Artefacts are unsigned. |
| set, `signtool.exe` available | Signs the app and the installer, SHA-256, RFC 3161 timestamped. |
| set, `signtool.exe` missing | **Fails.** Shipping unsigned because a tool was absent is the outcome worth preventing. |

`REMOTEFLOW_SIGN_TIMESTAMP_URL` overrides the timestamp server. Timestamping is what keeps a signature
valid after the certificate expires.

## SmartScreen, for the user-facing docs

An unsigned build has no reputation with Microsoft Defender SmartScreen, and users will meet it:

- Running the **installer** shows "Windows protected your PC". Getting past it takes **More info →
  Run anyway** — two clicks that are not obvious, and worth a screenshot in the user documentation.
- Running `RemoteFlow.exe` from the **portable zip** can show the same warning, and Windows may also mark
  files downloaded from the internet: right-click the zip → Properties → **Unblock** before extracting
  avoids per-file prompts.
- Both warnings say the publisher is unknown. That is accurate for an unsigned build and is not a sign of
  a corrupted download.

Signing removes the "unknown publisher" claim but not necessarily the warning: a standard OV certificate
accrues reputation over installs and time, so early signed releases can still be flagged. An EV
certificate or Microsoft's attestation signing gets trust immediately. Choosing one is out of scope here.

Publish artefacts with a SHA-256 checksum alongside them so a user can verify a download that Windows has
warned them about.

## Verifying a release candidate

Pushing a `v*` tag builds these artefacts, launches each one, and drafts a release — see
[releasing.md](releasing.md). What follows is what the automation cannot prove and a human has to do
before publishing that draft.

1. `pwsh ./scripts/publish-windows.ps1` — both architectures, zips and installers.
2. Extract the x64 zip on a machine with **no .NET runtime installed** and launch it.
3. Install with the installer, launch from the Start menu, then uninstall.
4. Confirm `%APPDATA%\RemoteFlow` still exists after uninstalling, and that answering Yes to the prompt
   removes it.
5. Check `RemoteFlow.exe` in Explorer: the icon appears, and Properties → Details shows the product,
   version, and copyright.
