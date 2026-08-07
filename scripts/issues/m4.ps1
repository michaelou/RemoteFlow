@(
@{ Number=28; Milestone='4 - SSH'
   Title='ISshTransport abstraction and FakeSshTransport test double'
   Labels=@('model:opus-5','effort:xhigh','area:ssh','type:feature','risk:load-bearing')
   Body=@'
```yaml
model: claude-opus-5
effort: xhigh
risk: load-bearing
depends_on: [19]
blocks: [29, 30, 31, 36]
read_first:
  - docs/adr/0004-ssh-transport-abstraction.md
  - src/RemoteFlow.Application/Abstractions/ITerminalChannel.cs
touches:
  - src/RemoteFlow.Application/Abstractions/Ssh/**
  - tests/RemoteFlow.TestSupport/FakeSshTransport.cs
  - tests/RemoteFlow.Application.Tests/**
verify: dotnet build; dotnet test tests/RemoteFlow.Application.Tests
```

## Goal
A seam that lets Tmds.Ssh and SSH.NET swap places, and a fake that makes every layer above SSH
unit-testable without a network.

## Do not implement a transport in this issue
This is deliberately abstraction-only. **Do not batch this with #31.** If you write the abstraction and
its first real consumer in the same session, you will bend the abstraction to fit that consumer and lose
the design pressure that makes the seam real - which is the entire point of having one. #36 later proves
the seam by running the same test suite against both implementations.

## Decisions already made - do not re-litigate
- Tmds.Ssh is primary; SSH.NET is the fallback, selected by the `SshTransport` setting.
- The adapter can be thin because both libraries now expose the same three primitives: open a shell with
  a PTY, read/write a stream, and resize. (SSH.NET regained `ChangeWindowSize` on `ShellStream` in
  2025.1.0 - older StackOverflow answers claiming resize is impossible are out of date.)
- `ISshShell` extends `ITerminalChannel` from #19 - **no new channel contract.**

## Scope
```
ISshTransport   ConnectAsync(SshConnectRequest, CancellationToken) -> ISshConnection
ISshConnection : IAsyncDisposable
    Task<ISshShell> OpenShellAsync(TerminalSpec, CancellationToken)
    Task<SshExecResult> ExecuteAsync(string command, CancellationToken)
    ISftpService OpenSftp()                       // consumed by #37
    event EventHandler<SshDisconnectedEventArgs>? Disconnected
ISshShell : ITerminalChannel
```
Supporting types: `SshConnectRequest` (host, port, username, auth material, host-key policy, timeouts,
keepalive), `TerminalSpec` (term type, cols, rows), `HostKeyInfo`, `SshExecResult`, and an `SshError`
taxonomy: `DnsFailure`, `ConnectionRefused`, `Timeout`, `AuthFailed`, `HostKeyUnknown`,
`HostKeyMismatch`, `HostKeyRevoked`, `ChannelClosed`, `NetworkChanged`, `Cancelled`.

`FakeSshTransport` in TestSupport: scripted responses, injectable failures, a controllable shell channel.

## Acceptance criteria
- [ ] The contract covers shell + PTY + resize + SFTP + exec with **no Tmds.Ssh or SSH.NET type leaking
      into Application** - the #2 architecture fitness test is extended to assert this.
- [ ] Every `SshError` case is representable and distinguishable.
- [ ] `FakeSshTransport` can simulate: successful connect, auth failure, unknown host key, mismatched
      host key, mid-session disconnect, and a shell that echoes input.
- [ ] A view-model-level test drives a full connect/type/disconnect cycle through the fake with no network.
- [ ] Cancellation is threaded through every async member.
- [ ] `dotnet build` passes with no reference to either SSH library from `Application`.

## Out of scope
Any real implementation (#31, #36). Host key verification logic (#30). The test container (#29).
'@ },

@{ Number=29; Milestone='4 - SSH'
   Title='SSH integration test harness with Testcontainers'
   Labels=@('model:sonnet-5','effort:medium','area:ssh','type:test','risk:contained')
   Body=@'
```yaml
model: claude-sonnet-5
effort: medium
risk: contained
depends_on: [28]
blocks: [31, 36, 37]
read_first:
  - src/RemoteFlow.Application/Abstractions/Ssh/ISshTransport.cs
  - CONTRIBUTING.md
touches:
  - tests/RemoteFlow.Ssh.IntegrationTests/**
  - tests/RemoteFlow.TestSupport/**
  - .runsettings
  - CONTRIBUTING.md
verify: dotnet test tests/RemoteFlow.Ssh.IntegrationTests --filter Category=Integration
```

## Goal
A real sshd to test against, that a contributor can run locally and that stays out of the default
`dotnet test` run.

## Why this matters more than usual here
CI does not arrive until Milestone 8. For all of M1-M7, the **default `dotnet test` must stay fast and
Docker-free** or nobody will run it - and then the "no CI yet" decision costs real quality rather than
just deferring a workflow file. Trait-gate from day one.

## Scope
- A Testcontainers 4.13 fixture running an sshd image seeded with: a **fixed host key**, a
  password user, an `authorized_keys` user, a keyboard-interactive user, and a tree of fixture files
  and directories for SFTP tests.
- **The ability to swap the server's host key on demand** - #30's mismatch test cannot be written without it.
- `[Trait("Category","Integration")]` on every test; a `.runsettings` that excludes that category by default.
- A `scripts/run-integration.ps1` convenience script, plus a CONTRIBUTING section on running them
  without CI.

## Acceptance criteria
- [ ] The fixture starts and tears down reliably on Windows (Docker Desktop) and Linux.
- [ ] **`dotnet test` with no arguments excludes integration tests** and requires no Docker.
- [ ] `dotnet test --filter Category=Integration` runs them green locally.
- [ ] The harness can swap the server host key mid-suite, so #30 can assert a mismatch.
- [ ] Each auth-method user authenticates successfully through a smoke test.
- [ ] **No `Thread.Sleep` anywhere** - poll with timeouts, so the suite is deterministic.
- [ ] Each test cleans up its own remote state, so tests pass in any order.
- [ ] Container startup is reused across tests in a class (collection fixture), not per test.

## Out of scope
The CI job that runs these (#53).
'@ },

@{ Number=30; Milestone='4 - SSH'
   Title='Host key store, verifier and trust-on-first-use policy'
   Labels=@('model:opus-5','effort:xhigh','area:security','type:feature','risk:load-bearing')
   Body=@'
```yaml
model: claude-opus-5
effort: xhigh
risk: load-bearing
depends_on: [5, 28]
blocks: [31, 32]
read_first:
  - docs/adr/0006-host-key-verification.md
  - src/RemoteFlow.Domain/Entities/HostKey.cs
touches:
  - src/RemoteFlow.Application/Services/HostKeyVerifier.cs
  - src/RemoteFlow.Persistence/Repositories/HostKeyStore.cs
  - tests/RemoteFlow.Application.Tests/**
  - tests/RemoteFlow.Ssh.IntegrationTests/**
verify: dotnet test tests/RemoteFlow.Application.Tests
```

## Goal
**This issue closes the single largest gap in the original requirements**, which never mentioned host key
verification at all. Without it the app is trivially MITM-able: an attacker on the path presents their own
key, the client accepts it, and the user types a password straight into the attacker's session.

## It must land before #31, not after
Tmds.Ssh takes a host-key callback **at connect time**. If the transport is written first, the natural
shortcut is a permissive callback "for now" - and that is exactly how "temporarily accept any key" ships
to users. Build the verifier first, then wire the transport into it.

## Decisions already made - do not re-litigate
- **Own the trust store in SQLite. Read `~/.ssh/known_hosts` on import only; NEVER write to it.**
  Clobbering a user's OpenSSH configuration is unacceptable, and our store needs richer state (revoked,
  source, comment) than known_hosts can express.
- Fingerprints are formatted exactly as OpenSSH does: `SHA256:` + unpadded base64 of the SHA-256 of the
  public key blob.
- A **new algorithm** for an already-known host is *not* a MITM - it is normal key rotation/negotiation.
  Only a changed key **for the same algorithm** is a mismatch.

## Scope
`IHostKeyStore` over the `HostKeys` table. `HostKeyVerifier` implementing three policies:

| Policy | Unknown host | Changed key (same algorithm) | Revoked |
|---|---|---|---|
| `Strict` | reject | reject | reject |
| `TrustOnFirstUse` | prompt once, then persist | **hard fail, never auto-accept** | reject |
| `AcceptAny` | accept + flag | accept + flag | reject |

## Acceptance criteria
- [ ] **Fingerprints match `ssh-keygen -lf` byte-for-byte** - golden test over several key types
      (ed25519, rsa-sha2-512, ecdsa).
- [ ] Under TOFU, an unknown host prompts once and is auto-trusted thereafter.
- [ ] **A changed key for a known algorithm hard-fails and is NEVER auto-accepted**, under every policy
      except `AcceptAny` - integration test using #29's key-swap capability.
- [ ] A *new algorithm* for a known host is accepted as rotation, not reported as a mismatch.
- [ ] `Strict` rejects an unknown host outright.
- [ ] `AcceptAny` requires per-connection opt-in and marks the resulting record so it is visibly flagged.
- [ ] A **revoked** key is refused under every policy, including `AcceptAny`.
- [ ] `LastSeenUtc` updates on each successful verification; `FirstSeenUtc` never changes.
- [ ] The unique constraint on (Host, Port, KeyAlgorithm) is respected - re-verifying does not insert a
      duplicate row.

## Out of scope
The trust dialogs and management screen (#32). Certificate authorities.
'@ },

@{ Number=31; Milestone='4 - SSH'
   Title='TmdsSshTransport implementation'
   Labels=@('model:opus-5','effort:xhigh','area:ssh','type:feature','risk:load-bearing')
   Body=@'
```yaml
model: claude-opus-5
effort: xhigh
risk: load-bearing
depends_on: [28, 29, 30]
blocks: [33, 34, 37]
read_first:
  - src/RemoteFlow.Application/Abstractions/Ssh/ISshTransport.cs
  - src/RemoteFlow.Application/Services/HostKeyVerifier.cs
touches:
  - src/RemoteFlow.Infrastructure/Ssh/TmdsSshTransport.cs
  - src/RemoteFlow.Infrastructure/Ssh/SshShellChannel.cs
  - tests/RemoteFlow.Ssh.IntegrationTests/**
verify: dotnet test tests/RemoteFlow.Ssh.IntegrationTests --filter Category=Integration
```

## Goal
A real SSH connection with a real PTY, bridged into `ITerminalChannel` so the terminal control from #20
works unchanged over the network.

## Decisions already made - do not re-litigate
- `Tmds.Ssh` 0.23.0: `ExecuteShellAsync`, `AllocateTerminal = true`, `TerminalWidth`/`TerminalHeight`/
  `TerminalType`, `SetTerminalSize(int, int)`, and `SftpClient` for #37.
- **The host-key callback delegates to `IHostKeyVerifier` from #30.** There is no permissive path, not even
  behind a debug flag.
- **Each session opens its own `ISshConnection`** rather than multiplexing channels - failure isolation is
  worth the extra handshake. Revisit only if repeated 2FA prompts become a real complaint, and then as
  opt-in channel reuse.

## Scope
`TmdsSshTransport` + `SshShellChannel : ISshShell`. Connect with timeout and cancellation; bridge the
Tmds stream to a `PipeReader`/write path; propagate resize via `SetTerminalSize`; `ExecuteAsync` for
one-shot commands; disconnect events; correct disposal ordering.

## Acceptance criteria
- [ ] An interactive shell against the #29 container works: type, run commands, read output.
- [ ] Resize propagates - `stty size` inside the session reports the new dimensions.
- [ ] **vim, tmux and htop are usable over SSH** - re-run the relevant rows of
      `docs/manual-test-terminal.md` over the network, since latency and chunking differ from local PTY.
- [ ] Cancelling mid-handshake leaves **no sockets and no threads** behind (assert socket count / task
      completion).
- [ ] `Disconnected` raises exactly once, even when both the peer closes and we dispose.
- [ ] An authentication failure returns a typed `Result` with `SshError.AuthFailed` - **not** a thrown
      exception.
- [ ] An unknown or mismatched host key surfaces as the corresponding `SshError`, and the connection is
      **not** established.
- [ ] Disposal order is correct: channel before connection, with no `ObjectDisposedException` on a
      pending read.
- [ ] No Tmds.Ssh type escapes into `Application` (architecture test still green).

## Out of scope
SFTP operations (#37). Auth UI (#33). Session management (#34). Reconnect (#35).
'@ },

@{ Number=32; Milestone='4 - SSH'
   Title='Host key trust UI and known_hosts import'
   Labels=@('model:opus-5','effort:high','area:security','type:feature','risk:contained')
   Body=@'
```yaml
model: claude-opus-5
effort: high
risk: contained
depends_on: [8, 30]
blocks: []
read_first:
  - src/RemoteFlow.Application/Services/HostKeyVerifier.cs
  - docs/adr/0006-host-key-verification.md
touches:
  - src/RemoteFlow.UI/Views/Security/**
  - src/RemoteFlow.Application/Abstractions/IHostKeyPrompt.cs
  - tests/RemoteFlow.UI.Tests/**
verify: dotnet test tests/RemoteFlow.UI.Tests
```

## Goal
The human side of #30: a first-connect trust prompt, an unmissable mismatch warning, a management screen,
and one-time import from OpenSSH.

## Why the UI details are security-critical
A mismatch dialog whose default button is "Accept" converts a detected MITM into an accepted one. The
dialog design *is* the control here, not decoration.

## Decisions already made - do not re-litigate
- `IHostKeyPrompt` is **declared in Application, implemented in UI** - so `HostKeyVerifier` can prompt
  without knowing Avalonia exists.
- **Import from `~/.ssh/known_hosts` is preview-then-apply, and never writes back.**
- Hashed known_hosts entries can be imported but cannot be displayed as plaintext hostnames - show them
  as hashed and say so.

## Scope
- **First-connect dialog**: host, port, algorithm, `SHA256:` fingerprint, optional randomart.
  Actions: Accept once / Accept and save / Reject.
- **Mismatch dialog**: styled as a hard warning; shows stored vs offered fingerprint side by side;
  explains what a mismatch means.
- **Trusted Keys management screen**: list, search, revoke, delete.
- **known_hosts import**: parse, preview what would be added, apply on confirm.

## Acceptance criteria
- [ ] **The mismatch dialog's primary/default button is Reject**, and accepting requires an explicit
      second confirmation.
- [ ] Enter or Escape on the mismatch dialog results in **rejection**, never acceptance.
- [ ] The stored and offered fingerprints are both shown, and visually differentiated.
- [ ] Revoking a key from the management screen causes the next connection to that host to be refused.
- [ ] Import shows a preview and applies only on confirm.
- [ ] **Import never writes to `~/.ssh/known_hosts`** - assert the file's mtime and content are unchanged
      after an import.
- [ ] Hashed known_hosts entries import correctly and are labelled as hashed rather than shown as a host.
- [ ] Both dialogs are fully keyboard-accessible and screen-reader labelled.

## Out of scope
Automatic key-rotation trust. Certificate authorities.
'@ },

@{ Number=33; Milestone='4 - SSH'
   Title='SSH authentication flows and key management UI'
   Labels=@('model:sonnet-5','effort:high','area:ssh','type:feature','risk:contained')
   Body=@'
```yaml
model: claude-sonnet-5
effort: high
risk: contained
depends_on: [18, 31]
blocks: []
read_first:
  - src/RemoteFlow.Infrastructure/Ssh/TmdsSshTransport.cs
  - src/RemoteFlow.Application/Abstractions/ICredentialProvider.cs
touches:
  - src/RemoteFlow.Infrastructure/Ssh/Auth/**
  - src/RemoteFlow.UI/Views/Connections/SshKeyPicker.axaml
  - tests/RemoteFlow.Ssh.IntegrationTests/**
verify: dotnet test tests/RemoteFlow.Ssh.IntegrationTests --filter Category=Integration
```

## Goal
Every authentication method the requirements list, plus the key-file UI that feeds them.

## Scope
**Auth methods**, with ordering and fallback:
- Password - from `ICredentialProvider`; prompt-and-optionally-save on a miss.
- Private key + passphrase - encrypted keys prompt, with an option to store the passphrase.
- Agent - Windows OpenSSH pipe, Pageant, `SSH_AUTH_SOCK`, 1Password socket.
- Keyboard-interactive - a **dynamic multi-prompt dialog** rendering server-provided text and echo flags,
  which is what makes 2FA work.
- Retry limits, and a clear error rather than an infinite prompt loop.

**Key management UI**: file picker with a recent-keys list; format detection (OpenSSH, PKCS#8, PEM, PuTTY
`.ppk`); encrypted-key detection; fingerprint + comment + type shown **before** saving; copy public key
to clipboard; optional Ed25519 keypair generation written `0600`.

## Decisions already made - do not re-litigate
- **`.ppk` is detected and refused** with an instruction to run
  `puttygen key.ppk -O private-openssh -o key` - no conversion in v1.
- Kerberos/GSSAPI: the `AuthMethod` value is reserved but marked unsupported in v1.

## Acceptance criteria
- [ ] Each method authenticates against the correspondingly-configured user in #29's container.
- [ ] A wrong password shows a clear, retryable error - **no stack trace reaches the UI**.
- [ ] Keyboard-interactive renders server-supplied prompt text and honours echo flags (password fields
      masked, informational prompts not).
- [ ] A missing agent degrades to the next method with an explanatory log line, not a hard failure.
- [ ] **No secret is ever logged** - extend #9's redaction test to cover passphrases and agent responses.
- [ ] Picking a key shows its type and fingerprint before it is saved.
- [ ] A `.ppk` file is detected and refused with the `puttygen` instruction.
- [ ] An encrypted key prompts for its passphrase and offers to store it.
- [ ] A generated Ed25519 key pair is written with mode `0600` and a correct `.pub` file.
- [ ] Retry limit is enforced and surfaces a terminal error rather than looping.

## Out of scope
PPK conversion. `ssh-copy-id`-style key deployment (a good v2 issue). Kerberos.
'@ },

@{ Number=34; Milestone='4 - SSH'
   Title='SSH terminal sessions end-to-end and ISessionManager'
   Labels=@('model:sonnet-5','effort:high','area:ssh','type:feature','risk:contained')
   Body=@'
```yaml
model: claude-sonnet-5
effort: high
risk: contained
depends_on: [21, 31]
blocks: [35, 39]
read_first:
  - src/RemoteFlow.UI/ViewModels/Terminal/TerminalWorkspaceViewModel.cs
  - src/RemoteFlow.Infrastructure/Ssh/SshShellChannel.cs
touches:
  - src/RemoteFlow.Application/Services/SessionManager.cs
  - src/RemoteFlow.UI/ViewModels/Terminal/**
  - tests/RemoteFlow.Application.Tests/**
verify: dotnet test tests/RemoteFlow.Application.Tests
```

## Goal
Double-clicking an SSH connection opens a working remote shell in a tab - the moment the app delivers
its core promise.

## Decisions already made - do not re-litigate
- **`ISessionManager` keys on `SessionId` (Guid), never on `ConnectionId`** - because multiple simultaneous
  sessions to the same connection are explicitly allowed (gap A9). Tabs disambiguate as `web-01 (2)`.
- Each session gets its own connection (see #31).
- Recent-connections is updated **only on a successful** connect.

## Scope
- Wire `SshShellChannel` into a terminal tab via the existing `TerminalSessionViewModel` - **no new view
  model**; if a new one seems necessary, #19's contract is wrong.
- Connect from the explorer (double-click / Enter), the details pane, and the `Ctrl+K` palette.
- Tab states: connecting / connected / failed, with the failure reason in-tab and a Retry button.
- `SshOptions.InitialCommand` and `StartupDirectory` applied on connect.
- `ISessionManager`: registry of live sessions, per-connection lookup, a legal-transitions-only state
  machine, change events for the UI, cancellation, and ordered shutdown on app exit.

## Acceptance criteria
- [ ] Double-clicking an SSH connection opens a working tab.
- [ ] **Three simultaneous sessions to the same host are independent** - killing one leaves the others
      alive, and tab titles disambiguate them.
- [ ] A connect failure shows the reason in-tab with a working Retry, not an empty terminal.
- [ ] `InitialCommand` runs and `StartupDirectory` is honoured.
- [ ] Session state transitions are legal-only; an illegal transition throws in tests.
- [ ] Exactly one event fires per transition.
- [ ] **App exit leaves no orphan processes or sockets** - asserted, and bounded so shutdown cannot hang.
- [ ] Recent updates on success only.
- [ ] vim/tmux/htop usable over SSH (the manual checklist rows from #31 re-run in the real workspace).

## Out of scope
Reconnect policy (#35). Session persistence across restarts (not in v1).
'@ },

@{ Number=35; Milestone='4 - SSH'
   Title='Keepalive, timeouts, error taxonomy and manual reconnect'
   Labels=@('model:sonnet-5','effort:high','area:ssh','type:feature','risk:contained')
   Body=@'
```yaml
model: claude-sonnet-5
effort: high
risk: contained
depends_on: [34]
blocks: []
read_first:
  - src/RemoteFlow.Application/Abstractions/Ssh/SshError.cs
touches:
  - src/RemoteFlow.Infrastructure/Ssh/**
  - src/RemoteFlow.UI/ViewModels/Terminal/**
  - tests/RemoteFlow.Application.Tests/**
verify: dotnet test tests/RemoteFlow.Application.Tests
```

## Goal
When a session dies, the user learns why in plain language and can get back with one click.

## Decisions already made - do not re-litigate
- **Manual reconnect only in v1.** Silent automatic reconnection to a host whose key may have changed is
  both a security hazard and a confusion hazard - the user should know a new connection was made.

## Scope
- Keepalive interval from `SshOptions.KeepAliveSeconds`; connect / auth / idle timeouts.
- An `SshError` -> user-message mapping table: DNS failure, connection refused, timeout, auth failed,
  host key mismatch, host key revoked, channel closed, network changed, cancelled.
- One-click **Reconnect** in the same tab, reusing stored credentials and preserving the tab title.
- Network-change awareness (`NetworkChange.NetworkAddressChanged`) so a suspend/resume produces a
  specific message rather than a generic timeout.

## Acceptance criteria
- [ ] Pulling the network surfaces a **specific** disconnect reason within the configured timeout.
- [ ] Reconnect restores a working session **in the same tab**, preserving the title.
- [ ] **Every `SshError` case maps to a distinct, actionable message** - table-driven test over the enum,
      so adding a case without a message fails the build.
- [ ] No message is a raw exception string or an enum name.
- [ ] A keepalive keeps an idle session alive through a 5-minute NAT idle window (integration test).
- [ ] A connect timeout fires at the configured value, not at the OS default.
- [ ] Reconnect after a host-key mismatch does **not** silently re-trust - it goes through #30's verifier.
- [ ] No automatic reconnect loop exists anywhere in the code.

## Out of scope
Automatic reconnection. Session resumption (tmux is the user-side answer).
'@ },

@{ Number=36; Milestone='4 - SSH'
   Title='SshNetTransport fallback implementation'
   Labels=@('model:sonnet-5','effort:medium','area:ssh','type:feature','risk:contained','stretch')
   Body=@'
```yaml
model: claude-sonnet-5
effort: medium
risk: contained
depends_on: [28, 29]
blocks: []
read_first:
  - src/RemoteFlow.Application/Abstractions/Ssh/ISshTransport.cs
  - src/RemoteFlow.Infrastructure/Ssh/TmdsSshTransport.cs
touches:
  - src/RemoteFlow.Infrastructure/Ssh/SshNetTransport.cs
  - tests/RemoteFlow.Ssh.IntegrationTests/**
verify: dotnet test tests/RemoteFlow.Ssh.IntegrationTests --filter Category=Integration
```

## Goal
A second `ISshTransport` implementation - which is simultaneously a hedge against Tmds.Ssh 0.x API churn
**and the only real proof that the abstraction from #28 is not leaky.**

## Why this is `stretch` but still worth doing
The requirements name SSH.NET in the technology stack, and it has a 311M-download install base, so some
users will hit environments where it behaves better. But the deeper value is the test: if the same
integration suite passes against both implementations, the seam is real. If it doesn't, #28 needs
rework - and it is much cheaper to learn that here than in v2.

## Decisions already made - do not re-litigate
- SSH.NET 2025.1.0. It **can** resize a live terminal: `ShellStream.ChangeWindowSize(columns, rows,
  width, height)` was re-added in PR #1646 (merged 2025-05-25) and shipped in 2025.1.0, closing the
  decade-old issue #40. Older documentation and StackOverflow answers say this is impossible; they are
  out of date.
- Its `net8.0` assets run fine on .NET 10 (there is no `net10.0` TFM).
- Selected by the `SshTransport` setting; takes effect on **new** sessions only.

## Scope
`SshNetTransport` implementing `ISshTransport`: shell + PTY, `ChangeWindowSize` for resize, host-key
callback into **the same `IHostKeyVerifier`** from #30, and `SftpClient` for `ISftpService`. Parameterise
the integration suite over both transports.

## Acceptance criteria
- [ ] **The same integration `[Theory]` set passes against both `TmdsSshTransport` and `SshNetTransport`** -
      this is the acceptance test for #28, not just for this issue.
- [ ] Switching the `SshTransport` setting takes effect on new sessions.
- [ ] The settings UI labels it *"fallback - please report why you needed it"*.
- [ ] Host-key verification goes through the same verifier - no second policy implementation.
- [ ] Resize works via `ChangeWindowSize` (`stty size` confirms).
- [ ] Any feature the seam cannot express identically is **documented as a known delta**, not silently
      papered over.
- [ ] Known advisory noted in the ADR: CVE-2022-29245 (weak PRNG in X25519, 5.9 MEDIUM) affected only
      2020.0.0/2020.0.1 and is long fixed - it is not a reason to avoid the library.

## Out of scope
Feature parity for anything only Tmds.Ssh supports - document the delta instead.
'@ }
)
