# ADR-0015: RDP launch and credential handover

- Status: Accepted
- Date: 2026-08-09

## Context

RemoteFlow launches the platform-native RDP client; embedding RDP is an explicit v1 non-goal. On Windows that means generating a `.rdp` file and starting `mstsc.exe`.

The `.rdp` format has a `password 51:b:` field. It is not plaintext — it holds a DPAPI blob tied to the user profile — which makes it tempting to treat as safe. It is not: a generated file sits on disk, is copied by backup and sync tools, and survives a crash. A file that leaks is a credential that leaks, and the blob is decryptable by anything running as that user.

## Decision

Never write a password into the `.rdp` file, and provide no code path that could. The file carries the endpoint and display options only.

When a connection has a stored RDP password, hand it to Windows immediately before launch with `cmdkey /generic:TERMSRV/{host}` and remove the entry once the client has started. The default is to store no password at all and let Windows prompt.

The generated `.rdp` goes in a per-launch temporary directory under the cache directory and is deleted after the client has read it. Directories left by a crash are swept at startup. All process execution goes through `IProcessRunner`, so the exact file contents and the exact argv are assertable in tests.

macOS and Linux are not supported. `UnsupportedRdpLauncher` reports a typed `UnsupportedPlatform` result naming a client to use instead, rather than failing further down.

## Consequences

The credential is exposed for the length of the handover window rather than for the life of a file, and only through the Windows Credential Manager entry the client is meant to read. `cmdkey` takes the password on its command line, which is readable by other processes running as the same user — a smaller and shorter-lived exposure than a file at rest, and the reason the entry is revoked straight after launch.

A fixed handover window is a guess at how long `mstsc` takes to read both the file and the credential. Too short and the launch fails; too long and the credential lingers. The launch awaits that window before returning, so the caller's command stays busy for a few seconds after the client appears.

Someone who wants Windows to remember the password permanently must set that up in Windows, not in RemoteFlow.
