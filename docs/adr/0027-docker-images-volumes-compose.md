# ADR-0027: Images, volumes and compose on the Docker page

- Status: Accepted
- Date: 2026-10-03

## Context

[ADR-0026](0026-docker-over-ssh-cli.md) added a Docker page for containers. It left images, volumes and
compose projects out of scope, and said they would use the same `DockerCli`/`IDockerHost` seam. This
change adds them. Three things about them did not fit the container work and needed decisions:

- **Some of these commands take minutes.** `docker compose up` may pull every image in a project, and a
  pull or a prune on a busy host can run far past the 30-second operation timeout that bounds
  `ExecuteAsync`.
- **`docker compose ls` forgets a project once it is down.** It lists projects that have containers. After
  `compose down` removes them, the project and the path of its compose file are gone from everything
  Docker reports, and bringing a project up needs exactly that path.
- **Removal depends on use.** Docker refuses to remove an image or a volume a container still uses, unless
  forced. The page has to know what is in use to say so before anyone clicks.

## Decision

### Long operations run in the output pane, as a stream

Compose up and down, image pull, image prune and volume prune are `DockerOperation`s. They start through
`IDockerHost.StartOperationAsync` on the streamed exec channel ADR-0026 introduced, and run in the pane
that shows logs. That gives them no timeout, live progress, and a place for the output to stay once they
end.

- **Exit code 0 is success.** Every list on the page is refreshed when an operation ends, because an up, a
  down or a prune changes containers, images and volumes at once.
- **Standard error is not marked as an error in operation output.** Compose and pull write their normal
  progress there, so the exit code is the only signal. A container's own standard error is still marked in
  its log.
- **A running operation can be neither closed nor displaced.** Closing the pane closes the channel, and a
  `compose up` cut off halfway leaves a project half up. While one runs, the close button is disabled.
  Opening a container's logs, starting another operation, or switching servers is refused with a message.

Removing one image or one volume is quick and runs through `ExecuteAsync`, like a container action.

### Compose projects are remembered, per connection, with their files

`IDockerComposeProjectMemory` keeps, for each connection, the name and compose files of every project the
page has listed. It is stored in the settings store under one key (`DockerComposeProjects`), so the whole
list is read and written together.

- On each refresh, the listed projects replace their remembered entries. Remembered projects that are not
  listed appear as **down**, with **Up** still available.
- **Forget** drops a down project from the list. Nothing on the server changes.
- A project listed without config files cannot be brought up from here and is not remembered. A project
  started from stdin is one example.
- The memory travels in backup archives with the other settings. A path to a compose file on a server is
  not a secret.

**Up** passes every remembered file with its own `-f`, in the order compose reported them, plus
`-p <project>`. The first file's folder is therefore the project directory, which is where compose looks
for `.env`, matching what a user typing in that folder would get. **Down** uses `-p` alone, because compose
finds a project's containers and networks by label without its files. **Down and delete volumes** adds
`--volumes` and has its own, harder confirmation.

A project RemoteFlow has never seen can be brought up by typing the path of its compose file. No `-p` is
passed, so compose names the project as it would on the command line, and the next refresh remembers it.

### What is in use comes from the container list

The container query now passes `--no-trunc` and asks for `.Mounts`. Without `--no-trunc` the mounts column
is shortened, and a shortened anonymous volume name matches nothing. Container IDs are therefore full
length; actions and stats matching already worked with either.

- **A volume is in use** when any container, running or stopped, mounts it. That is the rule Docker
  enforces.
- **An image is in use** when the engine reports a container count above zero, or when a container names
  it by reference, by repository alone (for `:latest`), or by ID. Engines before Docker 25 report
  `N/A` for the count outside `docker system df`, so the name match is the fallback.

Rows that are in use cannot be removed from the page, and **`--force` is never sent**. An image row is
removed by `repository:tag` when it has one, so an image carrying several tags loses only that one. An
untagged image is removed by ID.

### Validation extends to every new argument

The rules of ADR-0026 apply to every new argument:

| Argument | Rule |
| --- | --- |
| Image reference | `^[a-zA-Z0-9][a-zA-Z0-9_.:/@-]*\z` |
| Volume name | Docker's name grammar |
| Project name | Compose's own: `^[a-z0-9][a-z0-9_-]*\z` |
| Compose file path | Free text without control characters |

Every one is also single-quoted. `DockerCli.FindInvalidArgument` names the problem before a command is
built, and the page reports that nothing was sent.

### Prune says what it does

Image prune is `docker image prune --force`, which removes dangling images only. Volume prune is
`docker volume prune --force`. Docker 23 and later remove only anonymous volumes; older engines remove
named ones too. The confirmation says exactly that, instead of promising the newer behaviour.

### A missing Compose plugin is the Compose tab's problem

`docker compose` is a plugin. A server can have Docker without it. That failure is
`DockerError.ComposeNotInstalled`, and it is shown on the Compose tab. The page-level error stays clear,
and the other tabs keep working.

## Consequences

- Only the tab on screen is refreshed with the containers. The Images tab does not cost a
  `docker image ls` every five seconds while nobody is looking at it.
- `docker compose` v1 (`docker-compose`) is not supported: it has no `ls`.
- The command lines were exercised against a real Docker 29 engine, not only against fakes. That run found
  Docker 29's new wording for an unreachable daemon ("failed to connect to the docker API"), which is now
  recognised alongside the old one.
