# Linux packaging

This page describes what [`scripts/publish-linux.sh`](../scripts/publish-linux.sh) produces, and is the
counterpart to [packaging-windows.md](packaging-windows.md).

A release's Linux assets are built by CI, on a runner of each architecture, and attached to the draft with
everything else — see [releasing.md](releasing.md). This page is about running the script yourself, which
is what a local build or a fork does; nothing here is a step in cutting a release.

## Building the artefacts

```shell
./scripts/publish-linux.sh
```

That builds both architectures. Pass `--runtime linux-x64` or `--runtime linux-arm64` for one, and
`--keep-publish-output` to leave the intermediate trees in place for inspection. Everything lands in
`artifacts/`, which is git-ignored.

| Artefact | Contents |
| --- | --- |
| `RemoteFlow-<version>-linux-x64.tar.gz` | Portable build for Intel and AMD machines. |
| `RemoteFlow-<version>-linux-arm64.tar.gz` | Portable build for ARM machines. |
| `remoteflow_<version>_amd64.deb` | Debian/Ubuntu package for x64. |
| `remoteflow_<version>_arm64.deb` | Debian/Ubuntu package for ARM64. |

The packages follow Debian's `name_version_arch.deb` convention rather than the
`RemoteFlow-<version>-<rid>` pattern the Windows artefacts use, because `apt` and `dpkg` expect it.

Versions come from MinVer, and like the Windows script this one reads the version back out of the binary it
just built rather than being told what it is — on Linux that means running `RemoteFlow --version` and
parsing the result, since ELF has no equivalent of the PE `ProductVersion` field. A cross-architecture
build cannot run its own output, so it falls back to asking MSBuild and reports the smoke test as
`skipped (cross-architecture)`. Building both architectures on one machine therefore leaves one of them
merely compiled — which is why the release workflow gives each its own runner and then asserts that the
names the script chose are the ones the tag implies.

A MinVer prerelease version such as `0.2.6-alpha.0.5` becomes `0.2.6~alpha.0.5` in the package. Debian
sorts `~` before everything, including the empty string, so without that substitution `dpkg` would rank a
prerelease *above* the release it precedes.

### What is in the tarball

The self-contained publish: around 300 files and roughly 124 MB unpacked, carrying the .NET runtime, Skia,
HarfBuzz, and the PTY library. Trimming and AOT are off deliberately — EF Core and Avalonia's XAML loader
both rely on reflection a trimmer cannot follow — so the output is large and correct rather than small and
surprising. `RemoteFlow.Rdp.Windows.dll` is absent by design; the embedded RDP control is Windows-only.

The tarball has no icon and no desktop entry in it. Those are packaging concerns, and the `.deb` is where
they get applied.

## The package

| Path | Contents |
| --- | --- |
| `/opt/remoteflow/` | the publish tree |
| `/usr/bin/remoteflow` | symlink to `/opt/remoteflow/RemoteFlow` |
| `/usr/share/applications/remoteflow.desktop` | launcher entry |
| `/usr/share/icons/hicolor/<size>x<size>/apps/remoteflow.png` | icons, 16 through 256 px |
| `/usr/share/doc/remoteflow/copyright` | the MIT licence text |

Install it with `apt`, not `dpkg -i`, so dependencies are resolved rather than merely reported:

```shell
sudo apt install ./artifacts/remoteflow_<version>_amd64.deb
```

There are no `postinst` or `postrm` scripts. `desktop-file-utils` and `hicolor-icon-theme` ship dpkg
triggers that refresh the desktop and icon caches on their own, so the package has nothing to do at install
time beyond unpacking.

The icons are committed at [`build/linux/icons/`](../build/linux/icons), extracted once from
`src/RemoteFlow.UI/Assets/remoteflow.ico`. Every entry in that `.ico` is already PNG-compressed, so
extraction is a byte-slice at the offsets in the ICO directory and no `icoutils` or ImageMagick build
dependency is needed. The `.ico` itself is an `AvaloniaResource`, embedded in the assembly, so it never
reaches the publish output — which is why the PNGs are committed separately rather than copied at package
time.

### Dependencies

```
Depends: libc6, libgcc-s1, libstdc++6, libfontconfig1, libx11-6, libice6, libsm6,
         libsecret-1-0, fonts-dejavu-core,
         libicu78 | libicu76 | libicu74 | libicu72 | libicu70,
         libssl3t64 | libssl3
Recommends: gnome-keyring, xdg-utils
```

Most of that list comes from the `NEEDED` entries of the native libraries in the publish tree. Two entries
do not, and are the reason this list is maintained by hand in
[`build/linux/control.in`](../build/linux/control.in) rather than generated:

- **ICU and OpenSSL are loaded with `dlopen`**, so no dependency scanner can see them. ICU has no virtual
  package and its soname is versioned per distribution release, hence the alternation — `dpkg` is satisfied
  if any one of the listed versions is present. This is the packaging cost of
  `InvariantGlobalization=false`.
- `liblttng-ust.so.0` *does* appear in the `NEEDED` list, but only for
  `libcoreclrtraceptprovider.so`, which is loaded solely when tracing is enabled. It is deliberately not a
  dependency.

`gnome-keyring` is a recommendation rather than a requirement because RemoteFlow degrades honestly without
it, falling back to an Argon2id + AES-GCM passphrase vault; see
[the libsecret entry in troubleshooting](troubleshooting.md#linux-says-os-keyring-unavailable---using-passphrase-vault).
`xdg-utils` provides `xdg-open`, used for revealing files and opening them in an external editor; without
it those actions report a failure in the UI instead of silently doing nothing.

### Uninstall keeps your data

`apt remove` and `apt purge` both leave your connections, host keys, settings and logs alone. This needs no
maintainer script: everything RemoteFlow writes lives under `$HOME`, following the XDG base directory
spec, and `dpkg` only ever removes files it installed.

| Data | Location |
| --- | --- |
| Connections, folders, settings | `~/.config/remoteflow` |
| Database and host keys | `~/.local/share/remoteflow` |
| Cache | `~/.cache/remoteflow` |
| Logs | `~/.local/state/remoteflow/logs` |

Saved passwords are held by the OS keyring, not by RemoteFlow, and removing the package does not revoke
them. Delete them from Seahorse ("Passwords and Keys") if you want them gone.

To remove everything:

```shell
sudo apt purge remoteflow && rm -rf ~/.config/remoteflow ~/.local/share/remoteflow ~/.cache/remoteflow ~/.local/state/remoteflow
```

## Signing

Nothing is signed. There is no Linux equivalent of the Windows signing step, no detached signature, and no
apt repository with a signed `Release` file. What a downloader gets is the SHA-256 in the release's
`checksums.txt`, which proves the file arrived intact and says nothing about who built it — the same
distinction [ADR-0018](adr/0018-self-update.md) draws for the Windows installer.

This is weaker than it sounds for a package installed with `sudo`, and it is worth fixing. Hosting a signed
apt repository, or at minimum shipping a detached GPG signature next to each `.deb`, is the obvious next
step and is not done yet.

## What is not covered

- **AppImage, flatpak, and snap.** The `.deb` covers Debian and Ubuntu; other distributions use the
  portable tarball and the manual desktop entry described in
  [building.md](building.md#linux).
- **`arm64` verification when you build both locally.** One `publish-linux.sh` run can only launch the
  architecture it is running on; the other's version check is skipped exactly as the Windows script skips
  it. Release builds do not have this gap, because each architecture gets a runner of its own.
- **Installing.** Nothing on Linux corresponds to `smoke-test-artifacts.ps1`, so no automation installs
  the package or launches what it installed. That pass is on the manual list in
  [releasing.md](releasing.md#cutting-a-release).
- **Remote Desktop.** Windows-only. An RDP connection on Linux points at FreeRDP or Remmina; see
  [troubleshooting.md](troubleshooting.md).
