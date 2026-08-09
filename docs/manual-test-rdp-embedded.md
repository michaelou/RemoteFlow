# Embedded RDP manual test

Run this playbook before every Windows release and after changing Avalonia, the RDP interop, native
hosting, session tabs, keyboard routing, resize, DPI, credentials, or disposal. Embedded RDP is
Windows-only. Run the main pass on Windows x64 and the architecture gate on native Windows ARM64
hardware; Linux and macOS retain the external-client workflow and have no embedded surface.

Every numbered check includes its expected result. Record the release version, commit, Windows build,
architecture, RDP client version, remote Windows versions, monitor scales, and pass/fail evidence. A step
that cannot be run is **not passed**: record why and block the release when the step says it is a gate.

## Requirements and test data

Prepare:

- a Windows x64 machine and a native Windows ARM64 machine for the ARM64 gate;
- the x64 and ARM64 release-candidate packages, not only Debug builds;
- two reachable Windows RDP hosts, with one account in `DOMAIN\user` form and one UPN account;
- permission to interrupt one host's network without affecting other users;
- one host that supports dynamic-resolution updates and, when available, an older host that does not;
- a 100% display and a 200% display; add 125% and 150% displays or temporarily change display scaling;
- a local text editor, a remote text editor, and distinctive clipboard strings containing Unicode;
- one SSH connection, one RDP connection with clipboard sharing on, one with it off, one saved RDP
  password, and one RDP connection with no stored password.

Never put a production password in screenshots, notes, commands, logs, or issue comments. Use disposable
test credentials.

## Build and launch

From a clean checkout on Windows x64:

```powershell
dotnet restore RemoteFlow.slnx
dotnet build RemoteFlow.slnx -c Release --no-restore
dotnet test tests/RemoteFlow.Rdp.Windows.Tests/RemoteFlow.Rdp.Windows.Tests.csproj -c Release --no-build
dotnet test tests/RemoteFlow.UI.Tests/RemoteFlow.UI.Tests.csproj -c Release --no-build
./scripts/publish-windows.ps1 -Runtime win-x64 -SkipInstaller
```

Expected: the build has no warnings or errors; both suites pass; the RDP suite runs its 20-cycle native
resource harness; and the published payload contains `RemoteFlow.Rdp.Windows.dll`. Launch the published
`RemoteFlow.exe`, not the build output. Expected: the whole application renders normally, including at
200% Windows scaling. Failure to attach the RDP child window, a startup crash, or a manifest/DPI change
elsewhere in the app fails the pass.

On a Linux or macOS checkout, build the shared solution as described in [building.md](building.md).
Expected: shared projects build with no Windows RDP assembly loaded. Opening an RDP connection offers or
names the external-client path; it never shows an embedded option or a Windows COM error.

## Configure the open mode

1. Open **Settings -> RDP** and select **Inside RemoteFlow**.
   Expected: the description says a live RDP tab opens in the terminal workspace.
2. Open an RDP connection.
   Expected: RemoteFlow navigates to the terminal workspace and adds an `RDP` tab; no external process
   opens.
3. Close it, select **System RDP client**, and open the same connection.
   Expected: Remote Desktop Connection opens as a separate process and no embedded tab is added.
4. Return to **Inside RemoteFlow**. On the connection details, click **Open in external RDP client**.
   Expected: the explicit action always opens the separate client without changing the saved default.

## Connect and credentials

1. Open the saved-password connection with a valid `DOMAIN\user` account.
   Expected: the tab says **Connecting** until the remote desktop is usable, then **Connected**. It must
   not claim Connected while showing a black pre-login rectangle, and it must not prompt for the saved
   password.
2. Repeat with a valid UPN such as `user@example.test`.
   Expected: the UPN reaches Windows unchanged and connects.
3. Use a deliberately wrong password.
   Expected: the tab reaches **Failed** and says the credentials were rejected. The password appears
   nowhere in the UI or logs.
4. Connect to an unreachable host and to a host that refuses RDP.
   Expected: RemoteFlow stays running and gives an actionable host-unreachable/refused message rather
   than only "An internal error has occurred."
5. Exercise a host with an authentication/certificate warning.
   Expected: Windows' warning is visible and the user must decide; RemoteFlow does not lower NLA,
   CredSSP, or the authentication level to get through.
6. Delete the stored RDP credential, then press **Reconnect** in the same tab.
   Expected: Windows prompts. The deleted value is not reused from the control or RemoteFlow memory.

## Multiple sessions and retained tabs

1. Open five RDP tabs across both test hosts, then open an SSH tab.
   Expected: all six tabs remain independent, have protocol/environment text cues, and only the selected
   content is visible.
2. Switch repeatedly among them by clicking tabs. Leave each RDP connection running for at least two
   minutes while hidden.
   Expected: returning shows the same live remote desktop; no reconnect, new credential prompt, black
   replacement surface, duplicate control, or moved tab appears.
3. Cause one RDP connection to fail while typing in another and while the SSH session produces output.
   Expected: only the failed RDP tab changes state. The other RDP and SSH sessions remain usable.
4. Reorder tabs, select them with the keyboard after leaving the RDP surface with F6, and close one
   non-selected tab.
   Expected: order and selection remain correct and closing one session does not detach another session's
   native window.

## Keyboard and focus

Use an application in the remote desktop that visibly reports keys.

1. Click the RDP surface and type letters, digits, punctuation, F1-F5 and F7-F12, Ctrl+C, and Ctrl+V.
   Expected: the remote application receives them; no RemoteFlow command silently runs.
2. Press Ctrl+Alt+End.
   Expected: the remote Windows secure-attention screen appears, equivalent to remote Ctrl+Alt+Del.
3. Press F6.
   Expected: the RDP surface does not receive F6; focus moves to the selected tab and its accent outline
   is visible. Tab and the normal RemoteFlow switching shortcuts work from there.
4. Return to the surface and press Shift+F6.
   Expected: the remote application receives F6 and focus stays in the session.
5. While the surface has focus, try Ctrl+Tab, Ctrl+Shift+Tab, Alt+1, Ctrl+Shift+T, Ctrl+Shift+W, and F11.
   Expected: RemoteFlow does not run them because the child HWND owns input. Press F6 first to use an app
   shortcut. This limitation must match [keybindings.md](keybindings.md).
6. Try Alt+Tab and the Windows key.
   Expected: the local Windows shell may reserve them in a windowed embedded session; do not record them
   as reliable remote task-switching keys.
7. Click another tab, then click the RDP surface.
   Expected: the tab click takes focus out; the surface click returns focus and typing immediately goes
   remote. No polling delay is acceptable.

## Resize and SmartSizing

1. On the dynamic-resolution host, drag every window edge continuously for ten seconds, move the sidebar
   splitter, maximize, restore, minimize, and restore.
   Expected: after the short debounce the remote resolution follows the physical viewport. The connection
   never reconnects, scrollbars do not appear, and pointer clicks land on the visible target.
2. Resize a hidden RDP tab by changing the RemoteFlow window, then select the tab.
   Expected: exactly the final hidden size is applied on return; intermediate sizes do not replay.
3. Repeat against the older/non-dynamic host.
   Expected: the session stays connected and the existing remote bitmap scales to the viewport. This is
   SmartSizing: it can look slightly softer than a true remote-resolution change, but it must not show an
   error overlay, reconnect, hang, or grow scrollbars. The log contains a warning that dynamic resize
   failed and SmartSizing was enabled.

## DPI and pointer alignment

The RDP control accepts only 100%, 140%, and 180% device factors. RemoteFlow uses the highest supported
factor not above the display scale: 125% uses 100% and the surface scales it up; 150% uses 140%; 200%
uses 180%. Slight softness at 125% is expected, not a resize bug.

1. Start a session on a 100% display, then drag the entire window to a 200% display and back.
   Expected: text remains legible, the desktop fills the viewport, pointer clicks land correctly, and the
   session never reconnects.
2. Repeat at 125% and 150%, comparing text and pointer alignment with local UI controls.
   Expected: the quantisation above is visible only as possible scaling softness; there is no double
   scaling, clipped desktop, displaced pointer, or scrollbar.
3. Hide the RDP tab, move RemoteFlow between the 100% and 200% displays, then select it.
   Expected: the latest physical size and 180% factor apply once when the tab returns, with no reconnect.

## Clipboard text

1. With **Share my clipboard** enabled, copy a distinctive Unicode line remotely and paste it locally;
   then copy a different line locally and paste it remotely.
   Expected: exact plain text moves in both directions.
2. Open two RDP sessions with sharing enabled. Copy in the first and paste in the second.
   Expected: text crosses through the shared local Windows clipboard regardless of which tab was opened
   or focused first.
3. Repeat both directions with sharing disabled.
   Expected: neither direction transfers text. Existing local and remote clipboards do not get replaced.
4. Change the saved setting while a session is open, without reconnecting.
   Expected: the live session does not change. After reconnect, the new value takes effect. The editor
   explicitly says this.

File, image, and rich-text transfer are outside this milestone; do not fail the pass for those formats.

## Network drop and reconnect

1. With a fully logged-in session, interrupt its network route for at least ten seconds.
   Expected: RemoteFlow remains running; the tab moves through reconnecting or to Disconnected with a
   readable reason. Other sessions are unaffected.
2. Restore the network and press **Reconnect** if automatic recovery did not complete.
   Expected: the same tab and hosted control return to Connected and show a usable desktop. The tab does
   not move and the credential is handed over under the same policy as the first connect.
3. End the session from the remote administrator side.
   Expected: the tab says an administrator ended it, rather than only showing a numeric code.
4. Exercise **Reconnect**, **Retry**, and **Close** on the recovery surface.
   Expected: each action is reachable, stays outside the native overlay, and performs only the named
   operation.

## Close, shutdown, and resource budget

1. Close a connected tab, a connecting tab, a failed tab, and the same already-closing tab twice.
   Expected: each closes promptly with no exception. The disconnect wait is bounded even when the host
   never reports a clean disconnect.
2. With RDP and terminal tabs open, close the application and confirm the close prompt once.
   Expected: every session is disposed; no RemoteFlow window, RDP child window, `mstsc.exe`, or other
   orphan created by the embedded path remains after RemoteFlow exits.
3. Rerun the RDP test project and inspect the `RDP leak harness (20 cycles)` line in its test log.
   Expected budget after warm-up: at most +8 GDI handles, +8 USER handles, +16 MiB private memory, and
   exactly 0 live controls. The recorded Windows 11 24H2 baseline was 0, 0, 2.9 MiB, and 0.
4. Manually open, connect, and close 20 tabs in the release build while watching USER/GDI objects and
   private bytes in Process Explorer.
   Expected: counts settle inside the same budget and responsiveness does not degrade. Record before and
   after values in the release evidence.

## External fallback and in-process failure boundary

1. Simulate an embedded activation failure on a test installation, or use a machine where the ActiveX
   control is unavailable.
   Expected: RemoteFlow reports that embedded RDP could not start and offers **Open in external RDP
   client**. The connection data remains usable.
2. Use that action and complete a connection in Remote Desktop Connection.
   Expected: the external client is isolated in its own process and RemoteFlow remains usable if that
   client exits or crashes.
3. Record the risk: `mstscax.dll` is loaded in-process for embedded sessions. A crash in that Windows
   component also terminates RemoteFlow; there is no process boundary to contain it. The external action
   is the mitigation and must remain one click away.

## Native Windows ARM64 release gate

This row is load-bearing. ADR-0017 has no ARM64 runtime evidence; if this is the first native ARM64 run,
say so in the release evidence and amend ADR-0017 with the result whether it passes or fails.

1. On native Windows ARM64 hardware, install or extract the `win-arm64` release candidate and run
   `RemoteFlow.exe --version`.
   Expected: it starts natively and reports the release version.
2. Run the spike against a real host:

   ```powershell
   dotnet run --project tools/RemoteFlow.RdpSpike -- --host <host> --auto
   ```

   Expected: an ARM64 mstscax class activates, the hosted desktop reaches login complete, and the spike
   can resize and disconnect without `BadImageFormatException`, class-not-registered, or COM activation
   failure.
3. In the ARM64 release build, repeat basic connect, tab switching, keyboard F6, resize, clipboard,
   reconnect, and close.
   Expected: behavior matches x64. If activation fails, block embedded ARM64 shipping or implement and
   verify the external-client fallback before release; never mark the row "not tested" and ship it.

## Result record

Record one row per section:

| Section | Platform/host/monitors | Result | Evidence or issue |
| --- | --- | --- | --- |
| Build and launch | | Pass / Fail | |
| Open mode and fallback | | Pass / Fail | |
| Connect and credentials | | Pass / Fail | |
| Multiple/mixed sessions | | Pass / Fail | |
| Keyboard and focus | | Pass / Fail | |
| Resize and SmartSizing | | Pass / Fail | |
| DPI | | Pass / Fail | |
| Clipboard | | Pass / Fail | |
| Drop and reconnect | | Pass / Fail | |
| Close and 20-cycle budget | | Pass / Fail | |
| Native ARM64 gate | | Pass / Fail | |

Any unexplained crash, reconnect, leaked HWND/control, credential reuse, wrong pointer mapping, silent key
theft, or missing ARM64 evidence fails the release. Attach logs and exact reproduction steps to a new
issue; do not weaken authentication or switch off the failing check to make the playbook pass.
