# ADR-0026: Docker is driven through its CLI over SSH

- Status: Accepted
- Date: 2026-10-03

## Context

Many of the servers RemoteFlow connects to run every application in a container. Before this change, the
only way to see those containers was a terminal and `docker ps`. A Docker page has to list containers,
start, stop, restart and remove them, show their logs (including following them), and open a shell inside
one.

There are three ways to reach a server's Docker engine from a desktop application:

1. **The Engine API over TCP**: the daemon listening on `tcp://host:2376`. Hardly any server has this
   enabled, and enabling it safely needs TLS client certificates. Asking people to reconfigure their
   daemons in order to use a client is backwards.
2. **The Engine API through the Unix socket, tunnelled over SSH**: forward `/var/run/docker.sock` through
   the SSH connection and speak HTTP to it, or use `Docker.DotNet`. This needs streamlocal forwarding,
   which neither transport exposes today, and which `AllowStreamLocalForwarding no` turns off on many
   hardened servers. It also brings a new HTTP client and the Engine API's version negotiation.
3. **The `docker` CLI, run over an SSH exec channel**: the same thing the user would type, with the same
   permissions, on a connection RemoteFlow already knows how to open.

## Decision

**RemoteFlow runs the `docker` CLI over SSH exec channels.** Nothing is installed on the server, the
daemon is not reconfigured, and the account's existing Docker rights are exactly the rights the page has.
A server where the user can type `docker ps` is a server the page works on.

### One held connection, one channel per command

The page holds one SSH connection per attached server (`DockerWorkspaceSession`), modelled on the SFTP
page's session. Each list, action and log is its own exec channel on that connection, so the five-second
refresh never pays for a handshake. The connection is opened with the same credentials, host-key policy
and keep-alive as every other SSH session.

### Output is asked for in a shape that can be parsed

- `docker ps --all --format '<template>'` uses a template that writes one JSON object per row. The
  template asks for the compose labels by name with `.Label "com.docker.compose.project"` rather than
  splitting the `Labels` column, which is a comma-joined string that cannot be split safely once a value
  contains a comma.
- `docker stats --no-stream --format '{{json .}}'` returns one sample, not a feed. It takes Docker a second
  or two to measure CPU, so it is requested after the list and the list is shown without waiting for it.
- Exit codes and the daemon's stable error messages are mapped to `DockerError`: not installed, permission
  denied on the socket, daemon not running, no such container. Anything unrecognised is passed through
  verbatim rather than guessed at.

### A container reference never reaches the shell unchecked

Every command that names a container goes through `DockerCli`, and two independent rules apply:

1. The reference must match Docker's own name grammar, `^[a-zA-Z0-9][a-zA-Z0-9_.-]*\z`, which every name
   and every full or short ID satisfies. Anything else is refused before a command is built, and the page
   reports that nothing was sent. The anchor is `\z`, not `$`: in .NET `$` also matches just before a
   trailing newline.
2. The reference is single-quoted anyway.

Either rule alone would stop injection; both cost nothing. Actions use the container's **ID**, not its
name. The `Names` column can hold several comma-joined names for containers started with legacy links,
and an ID is never ambiguous.

### Streaming exec is a transport contract

`docker logs --follow` never finishes, so `ISshConnection.ExecuteAsync`, which collects the output once
the command exits and is bound by the operation timeout, cannot carry it. `ISshConnection` gains
`StartCommandAsync`, returning an `ISshRunningCommand`:

- `ReadLinesAsync` yields standard output and standard error as tagged lines, in arrival order, while
  the command runs.
- The operation timeout bounds only opening the channel, not how long the command runs.
- **Disposing the command closes the channel.** That is what stops a followed log on the server.
- `ExitCode` is set when the command exits. `Failure` is set when the channel broke underneath it. A
  non-zero exit is not a failure: `docker logs` for a missing container exits 1 and says why on standard
  error, and that message is the useful part.

Both transports implement it. Tmds.Ssh already interleaves the two streams (`RemoteProcess.ReadLineAsync`).
SSH.NET exposes two pipe streams that end only when the channel closes, so each gets a pump and the two
meet in one channel. The parity tests in `SshTransportParityTests` hold the two to the same behaviour.

The log pane reads off the channel on the thread pool and hands lines to the list in batches, at most a
few times a second. It keeps the newest 5,000 lines.

### A container shell is an ordinary terminal session

"Open shell" opens a normal SSH terminal tab whose shell, once up, is replaced by
`exec docker exec -it '<id>' sh -c 'command -v bash … && exec bash || exec sh'`. It goes through a new
`SessionOpenOptions.StartupCommand` on `ISessionManager.OpenAsync`, typed after the connection's own
startup directory and initial command. Because the command is stored on the session, it is typed again on
reconnect, so a reconnected tab goes back into the container. The tab therefore gets the terminal page's
grid, search, keymap and reconnect without any new terminal code. `exec` makes leaving the container end
the tab, instead of dropping back to the host's shell without saying so.

### Confirmation follows the cost of a mistake

Removing a container always asks. Stopping and restarting ask only on a connection whose environment is
**Production**. Starting never asks. RemoteFlow never sends `--force`: removing a running container is a
deliberate two-step job of stopping it and then removing it.

### Polling stops when nobody is looking

The list refreshes every five seconds only while the page is on screen, and the refresh can be switched
off. A followed log keeps running while the page is hidden, because it is a single idle channel and
stopping it would leave a gap in the tail.

## Consequences

- No server-side setup. The flip side is that the page can do only what the account can do. An account
  outside the `docker` group gets an explanation naming the `usermod` command, not a page of failures.
  Escalating with `sudo` is deliberately not attempted: it would need a password prompt per command, or
  a sudoers rule the user did not write for RemoteFlow.
- The CLI's output formats are part of the contract. The ones used here (`--format` templates,
  `{{json .}}`, `.State`, `.Label`) have been stable since Docker 20.10. Podman's Docker-compatible CLI
  is not tested.
- Images, volumes, networks and compose up/down are out of scope for this decision. They would use the
  same `DockerCli`/`IDockerHost` seam.
- `ISshConnection` gained a member, so every implementation, including the test double
  `FakeSshConnection` and its `FakeSshRunningCommand`, implements streaming.
