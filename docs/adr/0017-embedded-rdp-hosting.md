# ADR-0017: Embedded RDP hosting on Windows

- Status: **Provisional — the spike ran without a live remote desktop; see [Not yet evidenced](#not-yet-evidenced)**
- Date: 2026-08-09
- Related issue: [#78](https://github.com/michaelou/RemoteFlow/issues/78)
- Supported platform: Windows
- Harness: `tools/RemoteFlow.RdpSpike`
- Measured on: Windows 11 Pro, build 10.0.28000.0, x64, `mstscax.dll` 10.0.28000.2525

## Context

[ADR-0015](0015-rdp-launch-and-credential-handover.md) settled how RemoteFlow starts an RDP session: write
a `.rdp` file with no password in it, hand the credential to Windows for the length of a handover window,
and start `mstsc.exe`. That decision stands. This ADR **extends** it rather than replacing it: RemoteFlow
gains a second, Windows-only path that draws the session inside its own window, and the external launcher
remains — as the Linux and macOS answer, and as the Windows fallback when embedding will not start.

Nothing here changes the credential rule. Embedding removes the `.rdp` file from the picture entirely,
which is strictly less exposure than ADR-0015 describes, and the `cmdkey` handover is not needed at all
when the control takes the credential in memory.

The embedded path resolves the connection's existing `StoreProvider` and `StoreKey` through
`ICredentialProvider` immediately before every connect or reconnect. If a secret exists, it is assigned
once to the control's write-only `ClearTextPassword` property; no process starts, no file is written and
no Credential Manager handover entry is created. If the reference or secret is absent, the control's
password is reset and its own credential prompt remains enabled. `AllowCredentialSaving` is always false.

`SecretHandle` zeroes its mutable character buffer after the assignment. COM requires a BSTR, however,
so the assignment necessarily creates one short-lived .NET `string`; .NET strings cannot be reliably
zeroed. The implementation therefore keeps that string to the single property call and never retains,
logs, formats or includes it in an exception. Reconnect reads the provider again, so deleting a stored
credential takes effect immediately instead of reusing an earlier value.

RemoteFlow implements no RDP protocol code. FreeRDP is out of scope.

## Decisions

### 1. Which component

**The Remote Desktop ActiveX control in `mstscax.dll` — the `MSTSCLib` type library — activated as
`MsRdpClient11NotSafeForScripting`, with a probing fallback chain down to generation 8.**

The `NotSafeForScripting` classes are the ones to use. The safe-for-scripting twins exist to be embedded
in a browser and refuse the settings an application host needs.

The chain is **probed, not assumed**, because a registered class is not necessarily a creatable one. On
the measured machine `MsRdpClient12NotSafeForScripting` is registered under `HKCR\MsTscAx.MsTscAx.13` and
`CoCreateInstance` still fails:

```
MsRdpClient12NotSafeForScripting hr=0x80040111            (CLASS_E_CLASSNOTAVAILABLE)
MsRdpClient11NotSafeForScripting activated  version=10.0.28000
MsRdpClient10NotSafeForScripting activated  version=10.0.28000
MsRdpClient9NotSafeForScripting  activated  version=10.0.28000
MsRdpClient8NotSafeForScripting  activated  version=10.0.28000
```

Generation 12 is therefore tried first and expected to fail on most machines; a chain that stopped at
whatever the registry advertised would have picked a class that cannot be created.

| Generation | CLSID | ProgID |
| --- | --- | --- |
| 12 | `{3f859aa3-c2d4-4faa-b0e4-fd0c9c4e5e3a}` | `MsTscAx.MsTscAx.13` |
| 11 | `{1df7c823-b2d4-4b54-975a-f2ac5d7cf8b8}` | `MsTscAx.MsTscAx.12` |
| 10 | `{a0c63c30-f08d-4ab4-907c-34905d770c7d}` | `MsTscAx.MsTscAx.11` |
| 9 | `{8b918b82-7985-4c24-89df-c33ad2bbfbcd}` | `MsTscAx.MsTscAx.10` |
| 8 | `{a3bc03a0-041d-42e3-ad22-882b7865c9c5}` | `MsTscAx.MsTscAx.9` |

The ProgID lags the generation by one. Activate by **CLSID**, not ProgID: the off-by-one is exactly the
kind of thing that gets silently corrected into the wrong class.

Two findings shape what the chain is worth:

- **There is no `IMsRdpClient11` or `IMsRdpClient12`.** The type library gives both generations
  `IMsRdpClient10` as their default interface. Newer classes buy behaviour, not API.
- **Every generation from 8 upwards answered `QueryInterface(IMsRdpClient10)` and reported the same
  version**, `10.0.28000`. On a current Windows the classes are facets of one implementation, so falling
  back does not cost the interface. It may still cost behaviour, which is why the chain is ordered rather
  than collapsed to a single CLSID.

**The floor is generation 9, and generation 8 is a degraded last resort.** The member the design cannot do
without is `UpdateSessionDisplaySettings` — the only per-session DPI path there is — and the type library
places it on **`IMsRdpClient9`**, not on 10:

| Member | Introduced by |
| --- | --- |
| `Reconnect(width, height)` | `IMsRdpClient8` |
| `SyncSessionDisplaySettings`, `UpdateSessionDisplaySettings` | `IMsRdpClient9` |
| everything else this design uses | `IMsRdpClient8` or earlier |

So generation 8 loses DPI-aware resize and keeps everything else, and generation 9 loses nothing. Nothing
in the design requires generation 10 at all; 11 is preferred because it is the newest that activates, not
because it is needed.

In Windows terms, and taking Microsoft's generation-to-release mapping rather than measuring it: 8 is
RDP 8.0, 9 is RDP 8.1, 10 is RDP 10 and Windows 10. RemoteFlow ships and documents against Windows 11, so
no version it supports comes anywhere near the floor. The chain matters for a machine with a servicing
state nobody predicted, not for a supported one.

### 2. Interop mechanism

**Hand-written `[ComImport]` declarations, checked in as source, transcribed from the type library by a
throwaway generator. No `<COMReference>`.**

`<COMReference>` fails the repo on three counts, and the first is fatal on its own:

- It runs `tlbimp` at build time and needs the type library present, so `dotnet build` would only work on
  Windows. Everything under `src/` is `net10.0` today and `docs/building.md` documents building on Linux
  and macOS. A Windows-only build step in the solution is a bigger change than embedded RDP is worth.
- The generated interop assembly is an opaque build output. `Deterministic` and
  `ContinuousIntegrationBuild` are set repo-wide, and a generated assembly whose contents depend on the
  build machine's `mstscax.dll` is not the kind of thing those settings are meant to admit.
- The generated wrapper carries the whole type library, and `TreatWarningsAsErrors` is on. Suppressing
  analyzer output from code nobody wrote is a permanent tax.

Hand-writing it looked worse than it is. The type library reports a **dual** interface already flattened,
so `IMsRdpClient10` is one declaration of 73 vtable slots covering every inherited member from `IMsTscAx`
onwards — not a chain of six interfaces to redeclare. The four interfaces the design needs come to about
370 lines:

| Interface | IID | Why |
| --- | --- | --- |
| `IMsRdpClient10` | `{7ed92c39-eb38-4927-a70a-708ac5a59321}` | Server, credentials, size, Connect/Disconnect, `Reconnect`, `UpdateSessionDisplaySettings`. |
| `IMsRdpClientAdvancedSettings8` | `{89acb528-2557-4d16-8625-226a30e97e9a}` | Port, authentication level, CredSSP, SmartSizing, auto-reconnect. |
| `IMsRdpClientNonScriptable5` | `{4f6996d5-d7b1-412c-b0ff-063718566907}` | `ClearTextPassword`, `AllowPromptingForCredentials`. |
| `IMsRdpExtendedSettings` | `{302d8188-0052-4807-806a-362b628f9ac5}` | The named property bag; the only pre-connect route to the scale factors. |

Only `IMsRdpClient10` is declared, even though section 1 puts the floor at generation 9. That is the right
trade *and it is a real limit*: on a machine where generation 10 does not exist, `QueryInterface` fails and
embedding does not start, whatever the class chain found. Declaring `IMsRdpClient9` as well is a second
generated block of the same shape and costs nothing but lines — do it if a supported Windows ever turns up
without generation 10, and not before. Until then the external launcher is the answer on such a machine,
which is what it is for.

Three rules make this safe rather than reckless, and they are the ones to keep in production:

- **Transcribe, do not type.** The declarations are generated from the type library. A vtable that is one
  slot out is memory corruption, not a compile error. `tools/RemoteFlow.RdpSpike/README.md` records how.
- **Every member is `[PreserveSig] int`.** The caller sees the HRESULT. A control that answers
  `E_UNEXPECTED` because the session is not up yet is normal, and should not arrive as an exception.
- **Untyped slots stay `IntPtr`.** For a member nobody calls, the slot lining up is the only requirement.

A dual interface flattens; a plain `IUnknown`-derived one does not, and its `oVft` values are absolute —
`IMsRdpClientNonScriptable5` starts at slot 53. Regenerating without walking the bases would produce a
declaration that compiles and then calls the wrong function.

`SYSLIB1096` — the suggestion to move to source-generated COM interop — is suppressed. `[GeneratedComInterface]`
cannot express `VARIANT` or `BSTR` out-parameters, which the property bag and every string getter need.

### 3. ActiveX container

**A hand-rolled `IOleClientSite`/`IOleInPlaceSite`/`IOleInPlaceFrame`/`IOleControlSite` container over a
plain Win32 window. Not WinForms `AxHost`.**

Both were built and both were run through the same scripted sequence. They behave identically for
everything the spike could measure: the control activates, the event sink advises, the window parents,
focus lands inside it, and detach/reattach survives. The tiebreaker is what each costs.

The hand-rolled site is about 380 lines, most of it methods that return `E_NOTIMPL`, and it is transparent:
in-place activation is visible step by step in the log, and the site counts the calls the control makes
back into it.

```
QueryInterface: IOleObject=yes, IOleControl=yes, IOleInPlaceObject=yes, IOleInPlaceActiveObject=yes
IOleObject.SetClientSite -> 0x00000000
IOleObject.GetMiscStatus -> 0x00020191
site: OnInPlaceActivate
site: OnUIActivate
IOleObject.DoVerb(OLEIVERB_INPLACEACTIVATE) -> 0x00000000
control HWND = 0x9078A, parented under host = True
```

`AxHost` costs `UseWindowsForms` — a second UI framework in the shipped tree, on a project that otherwise
has one — and buys nothing measurable, because **the WinForms message loop is not running**:

```
Application.MessageLoop = False
```

Avalonia runs its own Win32 pump and never enters `Application.Run`. `Control.PreProcessMessage`,
`Application.AddMessageFilter` and WinForms' dialog-key routing are all reached from that loop, so under
Avalonia they are dead code. `AxHost`'s main advantage over a hand-rolled site is precisely the machinery
that is switched off here. Taking the dependency to get a container whose distinguishing feature does not
run is the wrong trade.

That WinForms is not shipped is also the reason `Interop/` can stay small: the spike proves the site is
writable, and the site is the only part of `AxHost` that was doing any work.

### 4. Avalonia hosting

**`NativeControlHost` returning `PlatformHandle(hwnd, "HWND")`, over a container window that the host
never destroys — on detach it reparents to a hidden holder window.**

The two documented constraints are accepted, not worked around:

- Native content draws **above** all Avalonia content. Nothing floats over the remote desktop. Status, the
  environment colour and the disconnected message live in chrome *around* the surface — which is what
  [#84](https://github.com/michaelou/RemoteFlow/issues/84) and [#85](https://github.com/michaelou/RemoteFlow/issues/85)
  already assume.
- `NativeControlHost` destroys and recreates the native control on detach from the visual tree, which is
  what a `TabControl` does on every tab switch.

**Both mitigations are used, because they solve different halves of the problem.**

*Keep every session view attached and toggle `IsVisible`* is the primary pattern. It is what
[#84](https://github.com/michaelou/RemoteFlow/issues/84) specifies, and in the spike it produced no
detach at all.

*Reparent to an offscreen holder* is the safety net, and it is not optional. A view can leave the tree for
reasons the workspace does not control — a real `TabControl`, a window closing to the tray, a layout
change. `DestroyNativeControlCore` is overridden to `SetParent` the container onto a hidden window instead
of destroying it. Measured across two detach/reattach round trips, one by removing the host from the tree
and one by selecting another `TabItem`:

```
detach #1: parked on the offscreen holder, not destroyed
attach #2: reparented to 0x3A03A0
detach #2: parked on the offscreen holder, not destroyed
attach #3: reparented to 0x3B03A0
after tab round trip: state=connecting, attach/detach=3/2, control window alive=True
```

The session and its HWND survived both. The container window is the thing that gets reparented — never the
control's own window — so the control is not told anything happened.

The holder is a normal hidden `WS_POPUP` window, not a message-only window. A message-only window has no
desktop lineage, and moving a live RDP control's window tree off the desktop is a larger gamble than a
window that is simply never shown.

**Embedded sessions always occupy one RemoteFlow viewport.** The stored `FullScreen` and `Multimon`
options remain external-client requests: the mapper does not apply either to the ActiveX control, and it
retains both requests in `IgnoredExternalRdpDisplayOptions` so the editor can explain the limitation.
Embedded fullscreen continues to mean making the RemoteFlow window fullscreen, and multi-monitor RDP is
out of scope for this milestone.

**`RemoteFlow.Desktop` must gain an application manifest with a `supportedOS` list.** This is not optional
and it is not currently there. Without it, Avalonia's Win32 `NativeControlHost` throws on the first
attach:

```
Unable to create child window for native control host.
Application manifest with supported OS list might be required.
```

Windows reports a shimmed 6.2 version to a process with no compatibility manifest, and Avalonia declines
to guess. The manifest deliberately carries **no** `<dpiAware>`/`<dpiAwareness>` element: Avalonia sets
process DPI awareness itself at startup, and a manifest entry would win over it and change behaviour for
every window in the app, not just the RDP one.

### 5. Focus and keyboard

**Keystrokes reach the control, and while it has focus Avalonia sees none of them. RemoteFlow needs an
explicit way out of the surface, and it is the only binding guaranteed to work.**

Focus lands inside the control when asked for, and the control reports focus changes back through the
site:

```
focus landed inside the control
keyboard: keys inside control=0, elsewhere=0; site TranslateAccelerator calls=0, site OnFocus calls=3
```

`OnFocus` firing three times is useful — RemoteFlow can know when the surface took focus without polling.
`TranslateAccelerator` never firing is the important half. Nothing in the stack preprocesses accelerators:
Avalonia does not, and neither container does. Keystrokes go straight to the focused window, which is
inside the control, so:

- **Every RemoteFlow keybinding stops working while the surface has focus.** Not some of them. Avalonia's
  input pipeline never sees the key.
- The way out cannot itself be an Avalonia binding. It has to be either a key the control does not
  consume, or a hook that runs before dispatch.

Follow [ADR-0009](0009-keybinding-policy.md) rather than making an exception to it, and mirror the
terminal's existing F6 convention for leaving a text surface rather than inventing a second one. That is
[#88](https://github.com/michaelou/RemoteFlow/issues/88)'s work; what this spike settles is that #88
cannot assume any binding survives, and that a `WH_GETMESSAGE` hook on the UI thread is a workable way to
see keys before the control does — `tools/RemoteFlow.RdpSpike/Diagnostics/KeyboardProbe.cs` is a working
one, thread-local, injecting into no other process.

### 6. DPI

**Set the scale factors through `IMsRdpExtendedSettings`' property bag before connecting, and through
`UpdateSessionDisplaySettings` afterwards. There is no property for them on any `IMsRdpClient` interface.**

This was worth checking rather than assuming: neither `DesktopScaleFactor` nor `DeviceScaleFactor` appears
as a member of `IMsRdpClient10` or `IMsRdpClientAdvancedSettings8` anywhere in the type library. The only
two routes are the named property bag and the seven-argument `UpdateSessionDisplaySettings`. The property
bag accepts them and remembers them:

```
IMsRdpExtendedSettings[DesktopScaleFactor] = 100: put -> 0x00000000, get -> 0x00000000 value=100
IMsRdpExtendedSettings[DeviceScaleFactor]  = 100: put -> 0x00000000, get -> 0x00000000 value=100
```

The protocol accepts only 100, 140 and 180 for either factor, so Avalonia's `RenderScaling` is quantised
rather than passed through. RemoteFlow chooses the nearest value: a monitor at 125% is sent as 140, while
an exact tie at 120% or 160% stays on the lower factor.

On a monitor change, Avalonia raises `RenderScaling`; the response is `UpdateSessionDisplaySettings` with
the new size and the new quantised factors. What that does to a live session is untested — see below.

### 7. Reuse after disconnect

**A control instance can be reused. `Connect` after `OnDisconnected` succeeds on the same object.**

```
event: OnDisconnected(1) — An internal error has occurred. (reason 1, extended 0)
reuse: Connect -> True
event: OnConnecting
```

So reconnect does not have to destroy the control, and
[#82](https://github.com/michaelou/RemoteFlow/issues/82)'s fallback — destroy and recreate behind the same
session identity — is a contingency rather than the design. Creating a fresh instance also works and costs
about 30 ms, so it stays available for the case where a reused instance turns out to hold bad state.

The caveat is real: the disconnect measured here was from the *connecting* state, not from an established
session. Reuse after a fully established session drops is on the list below.

**Teardown order is load-bearing.** `IMsRdpClientNonScriptable5` and `IMsRdpExtendedSettings` are
`QueryInterface` casts of the control's *own* RCW, not separate objects. Calling `Marshal.ReleaseComObject`
on either separates the RCW that the container is still holding, and the container's own teardown then
fails with `InvalidComObjectException`. The spike hit this exactly once and it is written down so #82 does
not. Release the object that `get_AdvancedSettings9` hands out if you like — it is a distinct COM identity
— but let one owner release the control.

### 8. win-arm64

**Unresolved. The spike compiles for `win-arm64` and was not run on ARM64 hardware.**

Release builds ship `win-arm64` (`.github/workflows/release.yml`), so this has to be settled before
embedded RDP ships, not after. What is known:

- `dotnet build -r win-arm64 --self-contained` succeeds. Nothing in the interop is architecture-specific:
  the vtable declarations are slot-based, and `IntPtr` sizing is handled — the one place it matters, the
  `VARIANT` stride when reading event arguments, is computed from `IntPtr.Size`.
- `System32` on Windows on ARM holds native ARM64 binaries, so `mstscax.dll` there should be an ARM64
  in-process server and should activate in an ARM64 process. That is an expectation from how Windows on ARM
  is built, **not** a measurement.

The measurement is one command on an ARM64 machine, and its result belongs in this ADR:

```shell
dotnet run --project tools/RemoteFlow.RdpSpike -- --host <a host> --auto
```

If it does not activate there, the answer is not to emulate: it is to fall back to the external `mstsc`
launcher on ARM64, which ADR-0015 keeps working and which needs no new code. Treat that as the known
mitigation rather than a crisis.

## Not yet evidenced

The spike ran against an unreachable host — `--host 127.0.0.1:1`, which needs no credentials — so
everything up to and including the transport is exercised, and nothing past a completed logon is. These
are open, and each one names how to close it:

| Question | Status | How to settle it |
| --- | --- | --- |
| A remote desktop renders inside the Avalonia window | **Unverified** | `--auto` against a real host; the log should show `OnConnected` then `OnLoginComplete`. |
| Resize without reconnecting | **Unverified** | Both buttons returned `E_UNEXPECTED` / `ControlReconnectBlocked` with no session, which is correct and says nothing. Retry on a live session. |
| Whether the host honours dynamic resolution | **Unverified** | Needs a host with Dynamic Virtual Channel resizing; `--smart-sizing` is the fallback to compare against. |
| DPI change on moving between monitors | **Unverified** | Drag a live session across monitors of different scaling. |
| Reuse after an *established* session disconnects | **Unverified** | Connect, log in, drop the network, then Connect on the same instance. |
| Which keys the remote actually receives | **Unverified** | Type into a live session; the keyboard probe reports where each keystroke landed. |
| `win-arm64` activation | **Unverified** | Run the spike on ARM64 hardware. |

Until the first row is closed this ADR is **Provisional**, and
[#82](https://github.com/michaelou/RemoteFlow/issues/82) and
[#84](https://github.com/michaelou/RemoteFlow/issues/84) should treat its untested rows as assumptions to
check rather than as settled ground.

## Risks and limitations

Ordered by what they cost, not by how likely they are.

1. **`win-arm64` is unverified and ships.** Question 8. Mitigation is the external launcher, which already
   exists.
2. **No RemoteFlow keybinding works while the surface has focus.** Structural, not a bug to fix. The way
   out of the surface has to be built deliberately — [#88](https://github.com/michaelou/RemoteFlow/issues/88).
3. **Nothing can be drawn over the session.** Avalonia's native content is always on top. Every affordance
   lives in chrome around the surface — [#84](https://github.com/michaelou/RemoteFlow/issues/84),
   [#85](https://github.com/michaelou/RemoteFlow/issues/85).
4. **`RemoteFlow.Desktop` needs an application manifest it does not have.** Adding one changes how Windows
   reports its version to the *whole* process, not just to the RDP path. It should land with the project
   wiring in [#79](https://github.com/michaelou/RemoteFlow/issues/79) and be smoke-tested against the rest
   of the app, not slipped in with the view.
5. **DPI is quantised to 100/140/180.** A user at 125% gets a session rendered at 100% and scaled. Visible,
   not fixable at this layer — [#92](https://github.com/michaelou/RemoteFlow/issues/92) should say so.
6. **A crash in `mstscax.dll` takes RemoteFlow with it.** The control is in-process. There is no equivalent
   of the external launcher's isolation. Out-of-process hosting is a much larger design and is not
   proposed; the honest mitigation is that the external launcher stays one click away.
7. **The type library reports an event the documentation does not.** Dispid 27 fires on connect and is not
   declared in `MSTSCLib`. Map events by dispid and ignore unknown ones; never map by position.
8. **`GetErrorDescription` produces Microsoft's wording, not RemoteFlow's.** "An internal error has
   occurred" for a refused connection is not a useful sentence for a user.
   [#82](https://github.com/michaelou/RemoteFlow/issues/82) already plans its own wording for the cases
   users actually hit; this is the evidence that it needs to.
9. **Hand-written interop is only as good as its transcription.** A wrong slot is memory corruption. The
   mitigation is that the file is generated and regenerating it is documented, not that it is reviewed
   carefully.

## Consequences

RemoteFlow gains a Windows-only embedded RDP path with no new package dependency, no build-time code
generation, no second UI framework, and no Windows-only build step — `src/` stays `net10.0` except for the
one project [#79](https://github.com/michaelou/RemoteFlow/issues/79) adds, and Linux and macOS keep
building the solution.

The cost is about 1,100 lines that RemoteFlow owns outright — 370 of generated interface declarations, 340
of hand-written OLE and Win32 declarations, and 375 for the container itself — and an in-process dependency
whose failure mode is the whole application. The external launcher from ADR-0015 is not deprecated by this
decision and must not be removed: it is the Linux and macOS path, the ARM64 contingency, and the answer
when embedding will not start.

## Reconsideration rule

- A hard failure in activation, hosting lifetime, or keyboard delivery on a supported Windows version
  opens a tracking issue immediately.
- If `win-arm64` cannot activate the control, this ADR is amended with that result and ARM64 falls back to
  the external launcher. It does not supersede the decision.
- If the interop transcription is ever found to be wrong in a shipped build, `<COMReference>` behind a
  Windows-only build step is reconsidered on its merits, and this ADR is superseded rather than patched.
