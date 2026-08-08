# ADR-0004: SSH transport abstraction

- Status: Accepted
- Date: 2026-08-07

## Context

RemoteFlow requires modern SSH features and a way to handle incompatibilities found in real servers. Direct use of a single library throughout the application would make compatibility changes invasive.

## Decision

Define an `ISshTransport` abstraction in the application boundary. Use Tmds.Ssh as the primary implementation because it is the preferred modern transport. Provide SSH.NET as a fallback implementation for server or authentication scenarios that the primary transport cannot support adequately.

The abstraction covers connection establishment, host-key handling integration, command channels, shell channels, streams, resize, cancellation, and normalized errors. Selection of the fallback is explicit and observable rather than silently changing security behavior.

## Consequences

Transport-specific types remain outside use cases and compatibility can improve incrementally. The adapter surface must be kept small enough to preserve library capabilities. Both implementations require security updates and focused integration tests against representative servers.

## Fallback behavior and known deltas

The `SshTransport` setting is read when a connection is opened, so changing it affects new sessions only. The settings UI describes SSH.NET as a fallback and asks users to report the compatibility reason that required it. Both adapters send presented keys through the same `IHostKeyVerifier`; the fallback never bypasses or duplicates host-key policy.

The shared integration theories cover connection/authentication results, host-key rejection, exec output, interactive PTY I/O, and live resize. SSH.NET 2025.1.0 uses `ShellStream.ChangeWindowSize`, including `stty size` verification against the test server.

Known adapter deltas are explicit:

- SSH.NET has no public cross-platform ssh-agent authentication adapter. Agent material is skipped when another configured method exists; an agent-only request fails authentication. Tmds.Ssh retains native agent support.
- SSH.NET raises keyboard-interactive and host-key callbacks synchronously. The adapter bridges those callbacks to the application async contracts without changing prompt or trust behavior.
- `ShellStream` does not expose an interactive shell's remote exit status. An orderly EOF is reported as zero and a locally terminated/error path as unknown; exec channels return the real status on both transports.
- SSH.NET SFTP uses a separately authenticated SSH connection and verifies that connection through the same host-key verifier. The Tmds.Ssh SFTP adapter remains deferred to issue #37 and currently reports that operation as unsupported.

## Security advisory history

CVE-2022-29245 described weak random generation in X25519 key exchange in SSH.NET 2020.0.0 and 2020.0.1 (CVSS 5.9, MEDIUM). It was fixed long before the pinned 2025.1.0 version and is not a reason to avoid the fallback. SSH.NET remains subject to normal dependency monitoring and upgrade gates.
