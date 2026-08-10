# Troubleshooting

Every entry below is a symptom someone actually meets, and what to do about it.

If you are filing a bug, include the version and commit. **Settings → About** shows both, and so does:

```shell
RemoteFlow.exe --version
```

The same page names your log folder and opens it. Logs are written locally and never sent anywhere; they
are redacted, so passwords and passphrases do not appear in them.

## "Windows protected your PC" when I run the installer

**Symptom.** A blue full-screen dialog from Microsoft Defender SmartScreen says it prevented an
unrecognised app from starting, and names the publisher as unknown.

**Why.** RemoteFlow releases are not code-signed yet, so the download has no reputation with SmartScreen.
This is expected, and it says nothing about the file being corrupt.

**Fix.** Click **More info**, then **Run anyway**. Before you do, verify the download: the release page
publishes a `checksums.txt`, and

```shell
certutil -hashfile RemoteFlow-1.0.0-win-x64-setup.exe SHA256
```

must print the hash listed there for that file.

With the **portable zip**, Windows also marks the downloaded file itself. Right-click the zip →
Properties → tick **Unblock** → OK, *before* extracting. Otherwise every extracted file inherits the mark
and you get the warning again per file.

## "Remote Desktop Connection (mstsc.exe) was not found on this machine"

**Symptom.** Launching an RDP connection on Windows fails immediately with that message.

**Why.** This message comes from the external RDP path. RemoteFlow could not find `mstsc.exe` on `PATH` or
in the usual location. The Windows-only embedded path uses `mstscax.dll` instead.

**Fix.** Check that **Remote Desktop Connection** is present under Windows Tools. If it is missing,
reinstall it from **Settings → System → Optional features**. RemoteFlow reports which client it found on
the connection's RDP section, so you can confirm the fix without starting a session.

## "The embedded RDP control could not be activated"

**Symptom.** Opening an RDP connection inside RemoteFlow fails immediately with **"The embedded RDP
control could not be activated"**, **"Embedded RDP is unavailable on this Windows installation"**, or
**"The embedded RDP session could not start"**.

**Why.** Embedded RDP is Windows-only and loads Microsoft's Remote Desktop ActiveX control from
`mstscax.dll` inside the RemoteFlow process. The control may be missing, unregistered, blocked by local
policy, or incompatible with this Windows installation.

**Fix.** The connection is still usable. On its details page click **Open in external RDP client**, or set
**Settings → RDP → System RDP client** and open it again. That launches Remote Desktop Connection in a
separate process. Repair or reinstall **Remote Desktop Connection** under **Settings → System → Optional
features**, reboot, and then try **Inside RemoteFlow** again. Include the RemoteFlow version, Windows build,
architecture, and the exact error from the local log when filing a bug; never include a password.

## "mstscax.dll does not match the version of the client shell"

**Symptom.** Embedded activation fails with text containing **"mstscax.dll does not match the version of
the client shell"** or **"The version of Remote Desktop Connection does not match the version of
mstscax.dll"**.

**Why.** Windows has mismatched Remote Desktop components, commonly after an incomplete Optional Features
or Windows update. RemoteFlow cannot safely combine those binaries and does not ship replacements for
either one.

**Fix.** Use **Open in external RDP client** immediately. Finish pending Windows updates, then remove and
reinstall Remote Desktop Connection from Optional Features and reboot. If the external client reports the
same mismatch, repair Windows components before retrying embedded mode.

## RDP stays connected but does not change remote resolution when resized

**Symptom.** The desktop continues to fill the embedded tab, but the remote resolution does not change
with the RemoteFlow window. Text can look slightly soft. The log says dynamic RDP resize failed and that
the **SmartSizing fallback was enabled**.

**Why.** The host or session does not support the display-control channel used for dynamic resolution.
RemoteFlow keeps the session connected and scales its existing bitmap with SmartSizing instead of
reconnecting or adding scrollbars.

**Fix.** This is usable fallback behavior. Update the remote host and its RDP services if true dynamic
resolution is required, or click **Open in external RDP client** and use that client's display options.
Do not repeatedly reconnect: it does not add display-control support.

## Embedded RDP looks softer at 125% or 150% display scaling

**Symptom.** Local UI is sharp but the remote desktop is a little softer, especially at 125%, while the
pointer still lands correctly.

**Why.** RDP accepts only 100%, 140%, and 180% for `DesktopScaleFactor` and `DeviceScaleFactor` in this
path. RemoteFlow quantises down to a supported value and lets the hosted surface scale the result: 125%
uses 100%, 150% uses 140%, and 200% uses 180%. It can look like a DPI bug, but avoids sending an invalid
factor or reconnecting when a window crosses monitors.

**Fix.** No setting is required. If the softness is unacceptable, use a display scale matching one of
those factors or **Open in external RDP client**. A displaced pointer, clipped desktop, or reconnect on a
monitor move is not this limitation and should be reported.

## RemoteFlow closes when the embedded RDP component crashes

**Symptom.** RemoteFlow itself exits at the same moment an embedded desktop fails, and Windows Error
Reporting names `mstscax.dll`.

**Why.** The Microsoft ActiveX control is in-process. Unlike `mstsc.exe`, it has no process boundary that
can keep RemoteFlow alive after a native crash.

**Fix.** Reopen RemoteFlow and use **Open in external RDP client**, which runs the client separately and is
the isolation fallback. Include the Windows Error Reporting fault details and RemoteFlow version in a bug
report, but do not attach credentials or a memory dump containing session secrets publicly.

## RDP says RemoteFlow does not launch it on this platform

**Symptom.** On macOS or Linux, an RDP connection reports that RemoteFlow does not launch RDP here.

**Why.** Embedded RDP is Windows-only. macOS and Linux keep the external-client workflow; when RemoteFlow
cannot launch a supported native client itself, it stops with guidance instead of attempting Windows COM
or failing later with something less useful.

**Fix.** Use a native client for now and connect to the same host from there.

- macOS: **Windows App**, from the Mac App Store.
- Linux: FreeRDP (`apt install freerdp3-x11`, `dnf install freerdp`, `pacman -S freerdp`) or Remmina.

SSH, SFTP, and the terminal work normally on both platforms.

## Linux says "OS keyring unavailable - using passphrase vault"

**Symptom.** A banner with that text on Linux, and saving a credential fails.

**Why.** RemoteFlow stores secrets in the Secret Service through libsecret — GNOME Keyring, KWallet, or
whatever your desktop provides. It could not load `libsecret-1.so.0` and `libglib-2.0.so.0`, or no keyring
daemon is running. Minimal, server, and container installs commonly have neither.

RemoteFlow then falls back to its own **passphrase vault**: a single file, `vault.rfv`, in
`$XDG_CONFIG_HOME/remoteflow` (usually `~/.config/remoteflow`), where a passphrase you choose is stretched
with Argon2id and each secret is sealed under the resulting key with AES-GCM. It is the same design a
password manager uses — and it means that if you forget the passphrase, the stored secrets are gone.

**Fix.** Install libsecret and a keyring daemon, then log out and back in so the daemon starts with your
session:

```shell
sudo apt install libsecret-1-0 gnome-keyring
```

`dnf install libsecret gnome-keyring` and `pacman -S libsecret gnome-keyring` are the equivalents.

**Until you do**, be aware of the current limitation: the vault file format and its cryptography are
implemented and tested, but **v1 has no prompt to unlock it**, so nothing can be written to it yet. On a
machine with no keyring, leave credentials unsaved and let RemoteFlow ask you for the password or
passphrase when you connect — that path works and keeps the secret in memory only.

## macOS keeps asking for permission to use the keychain

**Symptom.** A macOS dialog asks whether RemoteFlow may use your login keychain, sometimes repeatedly.

**Why.** Keychain access is granted per binary signature. A locally built RemoteFlow is unsigned, so
rebuilding it produces something macOS treats as a different application.

**Fix.** Choose **Always Allow** for a given build. Expect to be asked again after you rebuild. Signed
releases would end this; they are not part of v1.

## Long lines keep their old wrapping after I resize the window

**Symptom.** Shrink or widen the terminal, and lines that were already on screen stay wrapped for the old
width. Everything printed afterwards wraps correctly.

**Why.** This is deliberate. RemoteFlow sets `ReflowOnResize = false`, because XTerm.NET's normal-buffer
reflow can corrupt full-screen TUIs — vim, tmux, htop — on resize
([XTerm.NET issue #12](https://github.com/tomlm/XTerm.NET/issues/12)). Wrong history is a cosmetic
problem; a corrupted editor is a real one, so the trade goes that way.

**Fix.** There is nothing to fix and no setting to change. The PTY *is* resized — the remote program is
told the new size, and a full-screen application redraws itself correctly. To tidy the scrollback, clear
it (`clear`, or Ctrl+L in most shells).

## Integration tests fail or hang, or Docker errors on `dotnet test`

**Symptom.** `dotnet test --filter Category=Integration` fails to start containers, or the SSH tests
hang.

**Why.** The SSH integration suite runs a real OpenSSH server in a Linux container. It needs a running
Docker engine — Docker Desktop on Windows and macOS — and it is opt-in for exactly that reason: a plain
`dotnet test` excludes it and never contacts Docker.

**Fix.** Start Docker, confirm with `docker info`, and run the harness from the repository root:

```shell
pwsh ./scripts/run-integration.ps1
```

The first run builds a local Ubuntu/OpenSSH image, which takes a few minutes; later runs reuse it. On
Windows, Docker Desktop must be in **Linux containers** mode. These tests do not run in CI, so a green CI
run does not mean they passed — see [CONTRIBUTING.md](../CONTRIBUTING.md).

## The host key changed and RemoteFlow refuses to continue

**Symptom.** A warning shows two fingerprints — the one RemoteFlow stored and the one the server just
presented — and asks you to decide.

**Why.** The host is not proving the identity it proved last time. That happens innocently when a server
is rebuilt or its keys are rotated, and it happens for real when traffic is being intercepted.

**Fix.** Do not click through it. Confirm the new fingerprint out of band — from the machine's console,
your provisioning system, or whoever runs it — and only then accept. If it checks out, accepting stores
the new key. If it does not, reject and find out why. Trusted keys are listed and can be removed under
**Settings → Trusted keys**.

## A connection with the Strict policy will not connect

**Symptom.** "No trusted host key is stored for *host*:*port*, and this connection uses the Strict host
key policy, which never prompts."

**Why.** Strict is doing its job: it refuses to be the moment you decide whether a key is genuine.

**Fix.** Either import the key from a `known_hosts` file you trust, or change that connection's host key
policy to **Trust on first use** and verify the fingerprint when prompted.

## My connections vanished after reinstalling

**Symptom.** RemoteFlow starts with an empty connection list after an uninstall and reinstall.

**Why.** Uninstalling keeps your data by default, but the interactive uninstaller *offers* to delete it,
and `/VERYSILENT /PURGEDATA` deletes it without asking.

**Fix.** If you have a backup ZIP, restore it from the **Backup** page. If you do not, check for
`remoteflow.db` still sitting in `%APPDATA%\RemoteFlow`. Going forward: export a backup before
uninstalling — it is one button, and it also carries your folders, tags, settings, and trusted host keys.

## RemoteFlow will not start, and says the database could not be initialised

**Symptom.** An error at startup naming `remoteflow.db`, sometimes mentioning a backup file beside it.

**Why.** A schema migration failed, or the file is corrupt or locked — a synced folder or a backup agent
holding it open will do this.

**Fix.** The message names both paths. RemoteFlow takes a `.bak` copy before migrating, so the
pre-migration database is still there. Close anything that might be holding the file, then try again. If
it still fails, move `remoteflow.db` aside and restore from a backup ZIP. A database written by a *newer*
RemoteFlow is refused deliberately rather than downgraded — install the newer version again to read it.
