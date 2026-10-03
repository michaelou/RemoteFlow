# ADR-0028: Compose file editing and container addresses on the Docker page

- Status: Accepted
- Date: 2026-10-03

## Context

[ADR-0027](0027-docker-images-volumes-compose.md) let the Compose tab bring projects up and down, but their
compose files could only be changed outside RemoteFlow, and a new project needed a file someone had already
put on the server. The Containers tab showed no addresses: `docker ps` reports a container's networks by
name only, and its ports only in a long form that repeats each one for IPv4 and IPv6.

Editing a file on a server from the page needed decisions about how it travels, what stops one person's
save from overwriting another's, and how a broken file is caught.

## Decision

### Compose files travel over SFTP, not through a shell

`IDockerComposeFiles` reads and writes compose files. Its one implementation, `SftpDockerComposeFiles`,
uses an SFTP channel on the Docker page's own connection, opened the first time a file is.

Writing through `ExecuteAsync` was the alternative, because everything else on the page goes through the
`docker` CLI (ADR-0026). It would have meant passing the whole file inside the command line. Linux caps one
argument at 128 KB, the text would need encoding to survive quoting, and the exec channel has no standard
input to stream it through instead. SFTP moves the file as bytes, with no size limit worth the name and
nothing a shell could read as a command.

- **Only a full path is accepted.** SFTP would resolve a relative path against the home folder, and
  `docker compose -f` against wherever the command runs. Paths must start with `/`, and the control-character
  rule of ADR-0027 still applies.
- **A save replaces the file in one step.** The text goes to a temporary file beside the real one, which
  `SftpPublisher` then moves into place, keeping the old file's permissions. This is the same route the SFTP
  page's remote editing takes, and for the same reasons: a dropped connection leaves the old file or the new
  one, never half of each. SSH.NET's open-for-write also does not truncate, so writing a shorter text over
  the file in place would leave the old tail behind.
- **Files over 1 MB are refused for editing.** No hand-written compose file is that large.
- **Line endings are kept.** The editor types `\n`. A file that used `\r\n` is written back with `\r\n`, so
  a save does not change every line.

### A save never overwrites blindly

The editor remembers the text it opened. Before writing, the store reads the file again and refuses with
`DockerError.FileChanged` if it no longer matches, or if the file is gone. The message says to reload, and
to copy the changes first. A new file must not exist yet. Its missing folders are created, one at a time,
because SFTP has no `mkdir -p`.

There is a short window between the check and the write. Closing it would need locking that SFTP does not
offer, and the check exists to catch an edit made while the file sat open in the page, not a race of a few
milliseconds.

### Every save is checked by compose

After a save, `IDockerHost.ValidateComposeAsync` runs `docker compose -f … config --quiet`. A project's file
is checked together with the project's other files, in their order, because an override file alone is
rarely a valid project. A file that fails the check is still saved: the person may be mid-way through a
change, and losing the text would be worse. The page reports compose's message. **Save and up** does not
bring the project up after a failed check.

A new file brought up with **Save and up** passes no `-p`, as typing its path in the bring-up box does
(ADR-0027). Compose names the project. Once the refresh lists it, the editor belongs to that project.

### Addresses come from one `docker inspect` per refresh

After `docker ps`, `ListContainersAsync` runs one `docker inspect --type container` for all running
containers together and reads `.NetworkSettings.Networks`. Stopped containers have no address and are not
asked about. The addresses are a detail of the list, not the list itself: if the inspect fails, for
example because a container was removed between the two commands, the containers are listed with what
inspect did answer.

The row shows the first IPv4 address, or IPv6 if there is none, or `host` for a container on the host
network. The tooltip lists every network. `DockerCli.SummarizePorts` shortens the ports column. A port
published on every address shows as `8080→80/tcp`, the IPv6 twin is dropped, and a port bound to one
address keeps it.

## Consequences

- Editing needs the server's SFTP subsystem. Almost every OpenSSH server has it, but a server with it turned
  off can still be managed from the page; only opening a file fails, with the server's reason.
- Each refresh with running containers costs one more exec channel, every five seconds while the page is
  open. That is the same price the stats sample already pays.
- The editor is a plain text box with no YAML highlighting. Compose's own check is what catches mistakes,
  and it knows compose's rules, not only YAML's.
- `Tab` moves focus rather than typing a tab. YAML forbids tabs for indentation, and a text box that keeps
  `Tab` would trap keyboard users.
