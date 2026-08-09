# RemoteFlow.RdpSpike

A throwaway harness for [#78](https://github.com/michaelou/RemoteFlow/issues/78): can the Microsoft Remote
Desktop ActiveX control be hosted inside an Avalonia window, and by what mechanism. Nothing under `src/`
references it, and nothing here is a production abstraction — it is deliberately direct wiring, in the
shape of `tools/RemoteFlow.TerminalSpike`.

The decisions it produced are in [docs/adr/0017-embedded-rdp-hosting.md](../../docs/adr/0017-embedded-rdp-hosting.md).

## Running it

```shell
dotnet run --project tools/RemoteFlow.RdpSpike -- --host your-windows-host --user you
```

Type the host into the toolbar if you would rather not pass it. **There is no password option, by
design** — a password on a command line is readable by every process running as the same user, which is
the exposure [ADR-0015](../../docs/adr/0015-rdp-launch-and-credential-handover.md) exists to avoid. The
control puts up its own credential prompt.

| Option | What it does |
| --- | --- |
| `--host <host[:port]>` | The server. A port here wins over `--port`. |
| `--port <n>` | RDP port, default 3389. |
| `--user`, `--domain` | Prefilled on the control before Connect. |
| `--container ole\|axhost` | Which ActiveX container to use. Default `ole`. |
| `--class <8..12>` | Pin one MsRdpClient generation instead of taking the newest that works. |
| `--size <WxH>` | Fallback desktop size when the view has not been laid out yet. |
| `--smart-sizing` | Turn on `SmartSizing` instead of resizing the remote desktop. |
| `--no-credssp`, `--no-prompt` | Turn off CredSSP, or the control's own credential prompt. |
| `--auto` | Run the scripted sequence below instead of waiting for clicks. |
| `--exit-when-done` | Close when `--auto` finishes. |

Every run writes `artifacts/rdp-spike/session-<timestamp>.log` alongside the on-screen log, and
**Export evidence** writes a JSON snapshot next to it. Both are gitignored; copy what a decision rests on
into the ADR rather than leaving it in `artifacts/`.

### The scripted run

`--auto` walks the sequence the ADR's answers came from: connect, switch pages with the host kept
attached, switch pages by detaching it, switch tabs, resize both ways, focus the control, disconnect,
reconnect **the same instance**, then destroy it and build a fresh one. It is the same set of actions the
buttons perform, so a manual run and a scripted run are comparable.

Against an unreachable host — `--host 127.0.0.1:1` — everything except the acceptance criteria that need a
live desktop still gets exercised, and it needs no credentials.

## What is in here

| Path | What it is |
| --- | --- |
| `Interop/MsTscInterop.cs` | Generated. `[ComImport]` declarations transcribed from the MSTSCLib type library. |
| `Interop/Ole.cs` | The OLE embedding interfaces, hand-written. Small and stable, unlike the client surface. |
| `Interop/Win32.cs` | Window, hook and COM activation entry points. |
| `Hosting/OleSiteContainer.cs` | Container A: a plain Win32 window plus a hand-rolled OLE site. |
| `Hosting/WinFormsAxContainer.cs` | Container B: WinForms `AxHost`, subclassed rather than `aximp`-generated. |
| `Hosting/RdpNativeHost.cs` | The `NativeControlHost`, which reparents instead of destroying on detach. |
| `Hosting/OffscreenHolder.cs` | The hidden window that owns a session's HWND while no view is showing it. |
| `Rdp/RdpClassChain.cs` | The activation fallback chain, and the probe that reports what a machine offers. |
| `Rdp/RdpEventSink.cs` | `IMsTscAxEvents` received through a connection point as a raw IDispatch. |
| `Diagnostics/KeyboardProbe.cs` | A thread-local `WH_GETMESSAGE` hook, to see where keystrokes actually land. |

## Regenerating `Interop/MsTscInterop.cs`

It is transcribed from `C:\Windows\System32\mstscax.dll`, not hand-typed, because a vtable declaration
that is one slot out fails as memory corruption rather than as a compiler error. The generator is not
checked in — it was a scratch `ITypeLib` walker — and it does not need to be: the file only changes if
Microsoft changes the type library, which would be a breaking change to every ActiveX consumer on Windows
and is not something to plan around. If it ever does need regenerating, the shape is:

1. `LoadTypeLibEx("mstscax.dll")`, find the `ITypeInfo` for the interface by name.
2. Walk `GetFuncDesc` for every function, and for a plain `IUnknown`-derived interface also walk
   `GetRefTypeOfImplType` into the bases — a **dual** interface arrives already flattened, a plain one
   does not, and its `oVft` values are absolute.
3. Emit in ascending `oVft` order, all `[PreserveSig] int`, with `[retval]` moved from the return type
   back to a trailing `out` parameter.

Anything whose type is not worth naming stays `IntPtr`: the slot still lines up, which is the only thing
that has to be right for a member nobody calls.
