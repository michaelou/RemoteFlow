# Building from source

Windows is the only platform RemoteFlow ships prebuilt artefacts for in v1. macOS and Linux are supported
and build from source. This page is the whole path from a clean machine to a running application.

## Prerequisites

- **.NET SDK 10.0.300 or newer.** [`global.json`](../global.json) pins the feature band and rolls forward
  within it, so a 10.0.3xx SDK works and a 10.0.1xx SDK does not. Check with `dotnet --version`.
  Install from <https://dotnet.microsoft.com/download/dotnet/10.0>, or with your platform's package
  manager (`winget install Microsoft.DotNet.SDK.10`, `brew install --cask dotnet-sdk`,
  `sudo dnf install dotnet-sdk-10.0`).
- **Git**, to clone the repository.
- **A desktop session.** RemoteFlow is a GUI application: X11 or Wayland on Linux, a normal login on macOS
  and Windows.

Docker is needed only for the SSH integration tests, which are opt-in and are not part of a normal build.

## Build and run

```shell
git clone https://github.com/michaelou/RemoteFlow.git
```

```shell
cd RemoteFlow && dotnet build
```

```shell
dotnet run --project src/RemoteFlow.Desktop
```

That is enough to use the application. `dotnet test` runs the unit suite; see
[CONTRIBUTING.md](../CONTRIBUTING.md) for the integration suite and the rest of the local checks, and
[docs/manual-test-terminal.md](manual-test-terminal.md) for the manual terminal pass to run when the
terminal stack changes.

The embedded RDP control is the one platform-specific part of the source tree. On Windows the normal
solution build includes `RemoteFlow.Rdp.Windows` and its tests. On Linux and macOS use the solution's
cross-platform configuration, which builds and tests everything except those two Windows-only projects:

```shell
dotnet build RemoteFlow.slnx -p:Platform=CrossPlatform
dotnet test RemoteFlow.slnx -p:Platform=CrossPlatform --no-build
```

The desktop project itself still targets plain `net10.0` on every platform. Its reference to the native
RDP assembly and the corresponding DI registration are enabled only when MSBuild is running on Windows.
Linux and macOS builds therefore keep the external-client behavior and never load Windows code.

To check what a build claims to be:

```shell
dotnet run --project src/RemoteFlow.Desktop -- --version
```

That prints `RemoteFlow <version> (commit <sha>)` and exits without opening a window. A tree built without
`.git` reports `commit unknown`, which is expected.

## A self-contained build you can keep

`dotnet run` needs the SDK. To produce a folder you can move somewhere and run without .NET installed,
publish it for your runtime identifier:

```shell
dotnet publish src/RemoteFlow.Desktop -c Release -r linux-x64 --self-contained true -o ./artifacts/linux-x64
```

Substitute the runtime identifier for your machine: `linux-x64`, `linux-arm64`, `osx-arm64` (Apple
silicon), `osx-x64` (Intel Macs), `win-x64`, or `win-arm64`. The result is a directory of around 300 files
containing the .NET runtime, the native PTY library, and Skia. Trimming and AOT are deliberately off — EF
Core and Avalonia's XAML loader both rely on reflection a trimmer cannot follow — so the output is large
and correct rather than small and surprising.

Launch it with `./RemoteFlow` (`RemoteFlow.exe` on Windows).

Publishing for a different operating system than the one you are on works, but the executable bit is not
carried across from a Windows host. If you copy such a build to macOS or Linux, run
`chmod +x RemoteFlow` first.

## Linux

The self-contained publish carries the .NET runtime, Skia, HarfBuzz, and the PTY library with it. What it
expects to find on the machine is the ordinary desktop stack:

| Need | Debian/Ubuntu | Fedora | Arch |
| --- | --- | --- | --- |
| X11 client libraries (Avalonia's X11 backend, also used under Wayland's XWayland) | `libx11-6 libice6 libsm6` | `libX11 libICE libSM` | `libx11 libice libsm` |
| Fonts and font configuration | `libfontconfig1 fonts-dejavu-core` | `fontconfig dejavu-sans-fonts` | `fontconfig ttf-dejavu` |
| ICU, for globalisation | `libicu-dev` (or the runtime `libicuXX`) | `libicu` | `icu` |
| Credential storage — see below | `libsecret-1-0 gnome-keyring` | `libsecret gnome-keyring` | `libsecret gnome-keyring` |

On a normal GNOME or KDE install, everything above is already present. On a minimal or server install,
`libsecret` and a running keyring daemon are the two that are usually missing, and they are the ones you
notice: without them RemoteFlow has nowhere to put a saved password. See
[the libsecret entry in troubleshooting](troubleshooting.md#linux-says-os-keyring-unavailable---using-passphrase-vault).

RemoteFlow does not install a desktop entry for you. To get it into your application launcher, put a file
at `~/.local/share/applications/remoteflow.desktop`:

```ini
[Desktop Entry]
Type=Application
Name=RemoteFlow
Exec=/opt/remoteflow/RemoteFlow
Icon=/opt/remoteflow/remoteflow.png
Categories=Network;RemoteAccess;
Terminal=false
```

Point `Exec` at wherever you put the published folder.

## macOS

Building and running needs nothing beyond the .NET SDK:

```shell
dotnet publish src/RemoteFlow.Desktop -c Release -r osx-arm64 --self-contained true -o ./artifacts/osx-arm64
```

```shell
./artifacts/osx-arm64/RemoteFlow
```

Two things to know:

- **No `.app` bundle is produced, and nothing is signed or notarised.** The publish output is a plain
  executable in a folder. It runs from the terminal, and from Finder if you double-click the executable.
  Gatekeeper's quarantine applies to files *downloaded* from the internet, not to a binary you built
  locally, so a local build starts without argument. Nothing here is a substitute for a signed, notarised
  release; that work is not part of v1.
- **RDP does not launch from RemoteFlow on macOS.** An RDP connection reports that plainly and points you
  at Windows App from the Mac App Store. SSH, SFTP, and the terminal are unaffected.

Credentials go to your login keychain through `Security.framework`; macOS asks for permission the first
time, which is the prompt you should expect.

## Windows

```shell
dotnet publish src/RemoteFlow.Desktop -c Release -r win-x64 --self-contained true -o .\artifacts\win-x64
```

For the release-shaped artefacts — zips and Inno Setup installers, both architectures, with the version
read back out of the built binary — use the packaging script instead:

```shell
pwsh ./scripts/publish-windows.ps1
```

See [docs/packaging-windows.md](packaging-windows.md) for what it produces, what signing does when a
certificate is configured, and what the installer does and does not remove.
