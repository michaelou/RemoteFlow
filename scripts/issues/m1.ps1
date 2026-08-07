@(
@{ Number=1; Milestone='1 - Foundation'
   Title='Repo governance, MIT license, ADR log and docs skeleton'
   Labels=@('model:haiku-4.5','area:build','type:docs','risk:contained')
   Body=@'
```yaml
model: claude-haiku-4-5
risk: contained
depends_on: []
blocks: [2, 4, 19, 28, 30, 48]
read_first:
  - docs/requirements-v1.md
touches:
  - LICENSE
  - README.md
  - CONTRIBUTING.md
  - CODE_OF_CONDUCT.md
  - SECURITY.md
  - .github/ISSUE_TEMPLATE/**
  - docs/adr/**
verify: every file below exists and renders as valid Markdown
```

## Goal
The legal, contribution and decision-record baseline exists, so no later issue has to invent a
convention or re-derive a settled decision.

## Decisions already made - do not re-litigate
- License is **MIT** (matches every dependency in the stack: Avalonia, XTerm.NET, Porta.Pty, Tmds.Ssh, SSH.NET).
- Copyright holder: `michaelou`. Year: 2026.
- CI is introduced only in Milestone 8 - do **not** add any `.github/workflows/*` file here.
- Migrations may be squashed until the `v0.1.0` tag; append-only forever after. State this in CONTRIBUTING.

## Scope
- `LICENSE` - MIT, full text.
- `README.md` - name, one-paragraph description, tech stack, build/test commands, status "pre-alpha".
- `CONTRIBUTING.md` - build (`dotnet build`), test (`dotnet test`), the integration-test opt-in
  (`dotnet test --filter Category=Integration`), the migration squash rule, and the "no CI until M8" convention.
- `CODE_OF_CONDUCT.md` (Contributor Covenant 2.1), `SECURITY.md` (private disclosure via GitHub
  Security Advisories, no bug bounty).
- `.github/ISSUE_TEMPLATE/bug_report.md`, `feature_request.md`, and `.github/pull_request_template.md`.
- `docs/adr/` with ADRs 0001-0010 in Nygard format (Context / Decision / Consequences), ~1 page each:

| ADR | Decision |
|---|---|
| 0001 | Clean Architecture layering and project reference direction |
| 0002 | Domain model shape; sessions are independent; `EnvironmentKind` is a first-class enum |
| 0003 | EF Core + SQLite; `IDbContextFactory` per operation; migration strategy |
| 0004 | `ISshTransport`: Tmds.Ssh primary, SSH.NET fallback |
| 0005 | Terminal stack: SvcSystems.UI.Terminal over XTerm.NET + Porta.Pty - **mark PROVISIONAL, pending issue #3**. Include what vendoring XTerm.NET would cost, so that decision is pre-made |
| 0006 | Host key verification: TOFU + own trust store; never write to `~/.ssh/known_hosts` |
| 0007 | Credential storage per platform + Linux encrypted-file vault fallback |
| 0008 | Backup format v1 + Argon2id/AES-GCM envelope |
| 0009 | Keybinding policy: `Ctrl+C` is always SIGINT; copy is `Ctrl+Shift+C` |
| 0010 | Dark-mode-first theming with design tokens |

## Acceptance criteria
- [ ] `LICENSE` contains the MIT text with the correct holder and year.
- [ ] `CONTRIBUTING.md` states the exact build and test commands, the integration-test filter, and the migration squash rule.
- [ ] `SECURITY.md` gives a private disclosure channel and explicitly says there is no bug bounty.
- [ ] Issue and PR templates render correctly on GitHub.
- [ ] All ten ADRs exist, each with Context / Decision / Consequences sections.
- [ ] ADR-0005 is explicitly marked **Provisional - pending the terminal spike (#3)**.
- [ ] No file under `.github/workflows/` is created.

## Out of scope
- User-facing documentation (#58). CLA and funding files. Any code.

## Notes
`docs/requirements-v1.md` is already committed - reference it, do not restate it.
'@ },

@{ Number=2; Milestone='1 - Foundation'
   Title='Solution skeleton, build configuration and architecture fitness tests'
   Labels=@('model:opus-5','effort:xhigh','area:build','type:infra','risk:load-bearing')
   Body=@'
```yaml
model: claude-opus-5
effort: xhigh
risk: load-bearing
depends_on: []
blocks: [4, 8, 19, 53]
read_first:
  - docs/adr/0001-clean-architecture-layering.md
touches:
  - RemoteFlow.slnx
  - Directory.Build.props
  - Directory.Packages.props
  - global.json
  - .editorconfig
  - .gitattributes
  - .gitignore
  - NuGet.config
  - .config/dotnet-tools.json
  - src/**
  - tests/**
verify: dotnet build; dotnet test; dotnet format --verify-no-changes
```

## Goal
The full Clean Architecture skeleton compiles with zero warnings, dependency direction is enforced by
a test rather than by convention, and every package version is pinned in one place.

## Decisions already made - do not re-litigate
- `.slnx` (SDK 10.0.300 supports it natively) - cleaner diffs than `.sln`.
- **6 src projects**, not 4 and not 9. `UI` is an Avalonia **class library**; `Desktop` is the only
  project that references both `UI` and `Infrastructure`/`Persistence`. This is what lets `App` and the
  views load under `Avalonia.Headless` for tests, and keeps RID-specific packaging assets out of view code.
- `Persistence` is split from `Infrastructure` so EF design-time tooling and the migrations folder stay
  isolated, and `Infrastructure.Tests` can run with no EF dependency at all.

```
src/RemoteFlow.Domain/          entities, enums, value objects, Result<T>. ZERO PackageReferences.
src/RemoteFlow.Application/     ports (interfaces), app services, validators, DTOs.
                                refs: Domain + Extensions.{DependencyInjection,Logging}.Abstractions only
src/RemoteFlow.Persistence/     DbContext, IEntityTypeConfiguration, migrations, repositories
src/RemoteFlow.Infrastructure/  Ssh/ Sftp/ Pty/ Security/ Platform/ Backup/ Process/
src/RemoteFlow.UI/              Avalonia LIBRARY: App.axaml, Views, ViewModels, Controls, Styles
                                refs: Application + Avalonia. NOT Infrastructure, NOT Persistence
src/RemoteFlow.Desktop/         the EXE. Program.cs, DI composition root, RID/packaging assets
tests/RemoteFlow.TestSupport/            builders, fakes, temp-dir and temp-db fixtures
tests/RemoteFlow.Domain.Tests/
tests/RemoteFlow.Application.Tests/
tests/RemoteFlow.Persistence.Tests/      real SQLite temp files
tests/RemoteFlow.Infrastructure.Tests/
tests/RemoteFlow.Ssh.IntegrationTests/   Testcontainers sshd - Category=Integration
tests/RemoteFlow.UI.Tests/               Avalonia.Headless.XUnit
tests/RemoteFlow.Architecture.Tests/     dependency-direction fitness tests
tools/RemoteFlow.TerminalSpike/          created by #3, kept as a manual smoke app
```

## Scope
`Directory.Build.props`:

| Property | Value | Reason |
|---|---|---|
| `TargetFramework` | `net10.0` | Not `net10.0-windows`. RIDs applied at publish only |
| `Nullable` / `ImplicitUsings` | `enable` | |
| `TreatWarningsAsErrors` | `true` | Cheap now, impossible later. `WarningsNotAsErrors` is the escape hatch |
| `EnforceCodeStyleInBuild` | `true` | `.editorconfig` actually bites |
| `AnalysisLevel` | `latest-recommended` | |
| `InvariantGlobalization` | **`false`** | The terminal needs real locale/encoding behaviour |
| `ManagePackageVersionsCentrally` | `true` | Single place to pin Avalonia |
| `PublishTrimmed` / `PublishAot` | **`false`** | EF Core + Avalonia XAML reflection. Non-negotiable for v1 |
| `Deterministic` | `true`; `ContinuousIntegrationBuild` when `$(CI)` | |
| `NoWarn` | `CS1591` | Doc comments on, missing-comment warnings off |

Test projects get `IsPackable=false` and relaxed `CA1707`, keyed off `$(MSBuildProjectName.EndsWith('Tests'))`.

`Directory.Packages.props` - pin exactly:
`Avalonia 12.1.1`, `Avalonia.Themes.Fluent 12.1.1`, `Avalonia.Fonts.Inter 12.1.1`,
`SvcSystems.UI.Terminal 1.0.3`, `XTerm.NET 1.0.15`, `Porta.Pty 1.0.7`, `Tmds.Ssh 0.23.0`,
`SSH.NET 2025.1.0`, `Microsoft.EntityFrameworkCore.Sqlite 10.0.10`, `CommunityToolkit.Mvvm 8.4.2`,
`Konscious.Security.Cryptography.Argon2 1.3.1`, `Serilog.Sinks.File`, `xunit.v3 3.2.2`,
`Avalonia.Headless.XUnit 12.1.1`, `NSubstitute 6.0.0`, `AwesomeAssertions 9.5.0`, `Testcontainers 4.13.0`.

`global.json`: `{ sdk: { version: "10.0.300", rollForward: "latestFeature", allowPrerelease: false } }`
- accepts 10.0.4xx but refuses .NET 11, so an accidental SDK jump can't silently change behaviour.

`.editorconfig`: 4-space C#, 2-space `.axaml`/json/yml/xml, `end_of_line = lf`, file-scoped namespaces
required, `this.` never, `_camelCase` private fields, `System` usings first, naming rules at `error`,
style rules at `warning`, `CA1848` off. `.gitattributes`: `* text=auto eol=lf`.

`.config/dotnet-tools.json`: `dotnet-ef 10.0.10`.

`tests/RemoteFlow.Architecture.Tests` - hand-rolled reflection over loaded assemblies (~80 lines).

## Acceptance criteria
- [ ] `dotnet build` succeeds with **zero warnings** and `TreatWarningsAsErrors` on.
- [ ] `RemoteFlow.Domain.csproj` declares **zero** `PackageReference` elements.
- [ ] `RemoteFlow.UI.csproj` references neither `Persistence` nor `Infrastructure`.
- [ ] No project declares an inline package version (all central).
- [ ] `dotnet test` runs green (zero tests is fine).
- [ ] `dotnet format --verify-no-changes` passes.
- [ ] Architecture test **fails** if Domain gains a non-BCL assembly reference.
- [ ] Architecture test **fails** if Application references EF Core or Avalonia.
- [ ] Architecture test **fails** if UI references Infrastructure or Persistence.
- [ ] Architecture test **fails** if Infrastructure references UI.
- [ ] A comment in `Directory.Packages.props` records that `SvcSystems.UI.Terminal 1.0.3` requires
      Avalonia >= 12.1.1 while 12.1.1 is the current latest - zero headroom, so Avalonia bumps must be
      gated on the manual terminal checklist.

## Out of scope
- Any feature code, any entity, any view. CI workflow (#53). The spike harness (#3 creates `tools/`).

## Notes
Prefer hand-rolled reflection over NetArchTest/ArchUnitNET - no Cecil version lag on .NET 10.
`Avalonia.Diagnostics` has no 12.x release (stops at 11.3.19); do not add it here.
'@ },

@{ Number=3; Milestone='1 - Foundation'
   Title='SPIKE: validate the terminal stack against vim, nano, tmux and htop'
   Labels=@('model:opus-5','effort:xhigh','area:terminal','type:spike','risk:load-bearing')
   Body=@'
```yaml
model: claude-opus-5
effort: xhigh
risk: load-bearing
depends_on: []
blocks: [19, 20]
read_first:
  - docs/adr/0005-terminal-stack.md
touches:
  - tools/RemoteFlow.TerminalSpike/**
  - docs/adr/0005-terminal-stack.md
  - docs/manual-test-terminal.md
verify: run the harness and complete every row of the exit-criteria table below
```

## Goal
A go/no-go verdict on the terminal stack, backed by evidence, before any production code depends on it.

## Why this is issue #3 and not the first issue of Milestone 3
This is the highest-risk unknown in the project. **There is no Avalonia precedent for an embedded
terminal** - `awesome-avalonia` lists no terminal control at all - and there is **no published
evidence** that XTerm.NET has been tested against vim, nano, tmux or htop: those names appear nowhere
in its repo or in either Avalonia control's repo. A negative result invalidates the entire Milestone 3
issue set and part of Milestone 4's stream wiring. Discovering that in week eight costs the project;
discovering it in week one costs three days. It needs nothing from the codebase, so it runs at t0 in
parallel with everything else.

## Scope
A throwaway Avalonia 12 app in `tools/RemoteFlow.TerminalSpike` wiring `SvcSystems.UI.Terminal 1.0.3`
to `Porta.Pty 1.0.7`. Not production code - do not abstract it, do not add DI, do not write unit tests
for it. Keep it afterwards as a manual smoke app.

## Exit criteria - record pass/fail per row in ADR-0005

**TUI correctness (the unverified claim)**
- [ ] `vim`: open a 500-line file, navigate, `:set number`, visual-block select, `:%s///`, `:wq`. No cell
      corruption, correct status line, correct syntax colour in **both** 256-colour and truecolor.
- [ ] `nano`: edit and save, `Ctrl+O`/`Ctrl+X`, help bar renders, `Ctrl+W` search.
- [ ] `tmux`: create/split/switch panes, borders render correctly, `prefix-[` copy-mode scroll, detach/attach.
- [ ] `htop`: live redraw at 1 s for 60 s with no drift or artefacts; meters and colours correct; F-keys work.
- [ ] `less` on 100 000 lines: PgUp/PgDn, `/search`, `G`, `q`.
- [ ] Alternate screen: entering and exiting restores the normal buffer exactly.

**Encoding**
- [ ] CJK wide characters, combining accents, box-drawing glyphs, emoji. *An emoji double-width
      mismatch is a known issue, not a blocker.*
- [ ] A multi-byte UTF-8 sequence split across two read boundaries renders correctly.

**Keyboard**
- [ ] `Ctrl+C` sends `0x03` and interrupts a runaway `yes`. `Ctrl+D` EOFs. `Ctrl+Z` suspends.
- [ ] Arrows / Home / End / PgUp / PgDn / Delete correct in vim insert mode.
- [ ] `Alt+<key>` as ESC-prefix; F1-F12.
- [ ] Bracketed paste: a multi-line paste into vim insert mode arrives without auto-indent mangling.
- [ ] **Does `terminal.UserInput` yield raw bytes or interpreted keys?** This decides how much of #22
      we implement ourselves - answer it explicitly.

**Resize**
- [ ] SIGWINCH propagates; `stty size` matches; TUIs redraw correctly with `ReflowOnResize = false`.
- [ ] Capture the normal-buffer reflow gap (XTerm.NET issue #12) with a screenshot and confirm it does
      not corrupt *subsequent* output - only historical lines.

**Performance**
- [ ] `cat` a 10 MiB text file and `find / -type f`: sustained output with no UI freeze. Record ms/frame
      and peak memory - **the measured number becomes #24's benchmark threshold.**
- [ ] A 10 000-line scrollback stays under a stated memory budget (propose 100 MB for the process).

**API surface needed downstream**
- [ ] Selection and clipboard hooks sufficient for #23 and the Ctrl+C policy.
- [ ] Any buffer-search API - this decides whether #26's find is feasible.
- [ ] Colour-scheme and font configurability - this decides #25's shape.

**Platforms:** Windows and Linux mandatory. macOS may be deferred with an explicit note plus a follow-up issue.

## Deliverable
ADR-0005 updated from *Provisional* to *Accepted* or *Superseded*, containing: the pass/fail table,
screenshots or asciinema captures, the measured numbers, the API answers above, and a **go/no-go**.
Plus `docs/manual-test-terminal.md` - the checklist to re-run before every release and after every
Avalonia bump, since rendering correctness is not automatable.

## Failure rule - decided in advance so it is not argued later
- **>= 2 hard criteria fail** (TUI correctness, keyboard, or throughput) -> open the xterm.js-in-WebView
  spike, replan Milestone 3, and say so in the issue. Note the known costs of that route: native-control
  airspace breaks the tab strip and overlays (Avalonia has confirmed no offscreen rendering), WPE
  runtime deps on Linux, and N browser processes for N tabs.
- **1 failure with a viable workaround** -> proceed, open a tracking issue, and file an upstream
  issue/PR against XTerm.NET (MIT, small codebase - a fix or vendored fork is realistic).

## Out of scope
Production code, `ITerminalChannel` (#19), the real control host (#20).
'@ },

@{ Number=4; Milestone='1 - Foundation'
   Title='Domain model: entities, enums, owned value objects and Result<T>'
   Labels=@('model:opus-5','effort:xhigh','area:core','type:feature','risk:load-bearing')
   Body=@'
```yaml
model: claude-opus-5
effort: xhigh
risk: load-bearing
depends_on: [2]
blocks: [5, 48]
read_first:
  - docs/adr/0002-domain-model.md
  - docs/requirements-v1.md
touches:
  - src/RemoteFlow.Domain/**
  - tests/RemoteFlow.Domain.Tests/**
verify: dotnet test tests/RemoteFlow.Domain.Tests
```

## Goal
Every entity, enum and value object the app will ever persist, with invariants guarded at construction
and no external package references.

## Decisions already made - do not re-litigate
- **One `Connection` table with owned option groups, not TPH subclasses.** Users flip protocol on an
  existing entry all the time; TPH would force delete+recreate and lose the Id, its credential and its
  history. Protocol-specific *validation* lives in `ConnectionValidator` (#13), not in the type system.
- **`Protocol` is the *primary* protocol** - it drives the default port and the default double-click
  action. It does **not** gate capability: `SupportsSftp => Protocol is Ssh or Sftp`, so an SSH
  connection opens an SFTP pane without the user duplicating the entry.
- **`EnvironmentKind` is a first-class enum with a fixed palette, never a tag.** Tags are user-defined
  and unbounded; environment drives *safety-critical* colour (you must not fat-finger a Production tab)
  and must be sortable, filterable and impossible to typo. `ColorOverrideHex` covers the long tail.
- **Enums stored as `int` with explicit values, never as strings** - renaming a member must not corrupt data.
- **Guid v7 keys generated client-side** via `IGuidProvider`, so exports sort chronologically.
- **`Folder` is a hybrid adjacency list + materialised path.** `ParentId` is the source of truth;
  `Path` and `Depth` are app-maintained derived columns giving cheap subtree filters and O(1) cycle
  detection on move. Pure adjacency needs recursive CTEs EF can't express cleanly; pure path breaks
  referential integrity. Trees here are tiny, so the denormalisation cost is a rename loop.
- **Secrets never enter the domain.** `CredentialRef` is an owned value object holding an opaque
  `StoreKey` plus `StoreProvider` - which store wrote it - so a machine migration can report
  "4 secrets unavailable on this machine" instead of silently failing auth.
- **`RecentConnection` is a 1:1 side table.** Do *not* also put `LastConnectedUtc`/`ConnectCount` on
  `Connection`; two sources of truth for one fact is a bug generator.

## Scope
`Connection`: `Id, Name(<=100, required), Host(<=255, required), Port(1..65535), Protocol, Username?,
AuthMethod, Notes?(<=4000), FolderId?, IsFavorite, Environment, ColorOverrideHex?, SortOrder?,
ConcurrencyStamp, CreatedUtc, ModifiedUtc`, plus owned `Credential: CredentialRef`,
`Ssh: SshOptions`, `Sftp: SftpOptions`, `Rdp: RdpOptions`, and `Tags: ICollection<ConnectionTag>`.

- `SshOptions`: `KeepAliveSeconds?`, `TerminalType` (default `xterm-256color`), `PrivateKeyPath?`,
  `InitialCommand?`, `StartupDirectory?`, `HostKeyPolicy`, `RequestPty` (true).
- `SftpOptions`: `RemoteRootPath?`, `LocalDownloadPath?`, `PreserveTimestamps`, `ShowHiddenFiles`.
- `RdpOptions`: `Domain?`, `FullScreen`, `Width?`, `Height?`, `Multimon`, `RedirectClipboard`, `RedirectDrives`.
- `CredentialRef`: `Kind`, `StoreKey`, `StoreProvider`, `UpdatedUtc?`.
- `Folder`: `Id, Name, ParentId?, Path (e.g. "/Prod/EU/db"), Depth, SortOrder, IsExpanded, ConcurrencyStamp, CreatedUtc, ModifiedUtc`.
- `Tag`: `Id, Name, ColorHex?, CreatedUtc`. `ConnectionTag`: `ConnectionId, TagId` (explicit join entity -
  backup merge needs to address join rows directly).
- `HostKey`: `Id, Host, Port, KeyAlgorithm, PublicKeyBase64, Sha256Fingerprint ("SHA256:..."), TrustState, Source, Comment?, FirstSeenUtc, LastSeenUtc`.
- `Setting`: `Key (PK), Value (JSON), ModifiedUtc`.
- `RecentConnection`: `ConnectionId (PK), LastOpenedUtc, OpenCount`.

Enums with explicit values:
```
ProtocolType        { Ssh=1, Sftp=2, Rdp=3 }
AuthMethod          { None=0, Password=1, PrivateKey=2, Agent=3, Certificate=4, KeyboardInteractive=5, Kerberos=6 }
EnvironmentKind     { Unspecified=0, Development=1, Staging=2, Production=3 }
HostKeyPolicy       { Strict=0, TrustOnFirstUse=1, AcceptAny=2 }
HostKeyTrust        { Trusted=1, Revoked=2 }
HostKeySource       { UserAccepted=1, ImportedKnownHosts=2, Pinned=3 }
CredentialKind      { None=0, Password=1, PrivateKeyPassphrase=2, RdpPassword=3 }
TerminalKind        { Local=1, Ssh=2 }
SessionState        { Created, Connecting, Connected, Reconnecting, Disconnected, Failed, Closed }
ConflictResolution  { Overwrite, KeepBoth, Discard, Cancel }
MergeStrategy       { Merge=1, Replace=2 }
MergeConflictPolicy { PreferLocal=1, PreferImported=2, RenameImported=3 }
```

`Result<T>` / `RemoteFlowError`: a small result type. Expected failures (auth rejected, host key
mismatch, remote conflict, permission denied) return `Result`; exceptions mean bugs. This keeps view
models free of defensive `try/catch` and makes the error taxonomy testable.

## Acceptance criteria
- [ ] `Connection.Create` rejects an empty name, an empty host, and a port outside 1..65535.
- [ ] `Folder.MoveTo` rejects a descendant target (cycle) and a sibling name collision.
- [ ] `Folder` keeps `Path` and `Depth` consistent with `ParentId` after any mutation.
- [ ] Every enum member has an explicit numeric value.
- [ ] Owned option objects are **always materialised, never null** (nullable owned reference navigations
      force every column nullable in EF and produce awkward SQL).
- [ ] `RemoteFlow.Domain.csproj` still declares zero `PackageReference` elements.
- [ ] >= 40 unit tests covering the invariants above.

## Out of scope
EF mapping and the schema (#5). UI-facing validation messages (#13). Any persistence concern.
'@ },

@{ Number=5; Milestone='1 - Foundation'
   Title='EF Core DbContext, entity configurations and initial migration'
   Labels=@('model:opus-5','effort:xhigh','area:data','type:feature','risk:load-bearing')
   Body=@'
```yaml
model: claude-opus-5
effort: xhigh
risk: load-bearing
depends_on: [4]
blocks: [6, 7, 30]
read_first:
  - docs/adr/0003-ef-core-sqlite.md
  - src/RemoteFlow.Domain/Entities/Connection.cs
touches:
  - src/RemoteFlow.Persistence/**
  - tests/RemoteFlow.Persistence.Tests/**
verify: dotnet test tests/RemoteFlow.Persistence.Tests
```

## Goal
A migrated SQLite schema with real referential integrity, and a `DbContext` lifetime that is safe for a
desktop app with hours-long sessions and background transfers.

## Decisions already made - do not re-litigate
- **`IDbContextFactory<RemoteFlowDbContext>`, one short-lived context per operation.** This is the single
  most important persistence decision in the app. A long-lived singleton context leaks tracked
  entities, serves stale reads across an hours-long session, and is not thread-safe against background
  transfer or session work. Repositories take the factory, not a context.
- **Guid storage: TEXT, lowercase dashed.** BLOB saves 20 bytes per key and is irrelevant at this scale;
  greppable JSON exports and `sqlite3` debuggability are worth far more.
- **Owned types via `OwnsOne`, never null**, navigation marked `Required()`. `ComplexProperty` is the
  tempting alternative but cannot be null at all and has thinner EF 10 support for this shape.
- **Search is `LIKE` + `NOCASE`, no FTS5 in v1.** Hundreds of rows; in-memory ranking after a broad
  `LIKE` is simpler and gives better fuzzy behaviour for the palette. Record FTS5 as a scaling escape hatch.
- **Concurrency:** SQLite has no `rowversion`. `ConcurrencyStamp: Guid` marked `IsConcurrencyToken()` on
  `Connection` and `Folder` **only** - that catches the two-windows-editing-the-same-entry case, which is
  the only realistic conflict.

## Scope
| Table | PK | Indexes | Notes |
|---|---|---|---|
| `Connections` | `Id` TEXT | `FolderId`; `Name` NOCASE; `Host` NOCASE; `Protocol`; filtered `IsFavorite WHERE IsFavorite=1` | No unique index on `Name` - Duplicate would fight it |
| `Folders` | `Id` | UNIQUE `Path` NOCASE; `ParentId` | FK to self, `ON DELETE RESTRICT` |
| `Tags` | `Id` | UNIQUE `Name` NOCASE | |
| `ConnectionTags` | (`ConnectionId`,`TagId`) | (`TagId`,`ConnectionId`) | both FKs cascade |
| `HostKeys` | `Id` | UNIQUE (`Host`,`Port`,`KeyAlgorithm`); (`Host`,`Port`) | |
| `Settings` | `Key` | - | JSON values |
| `RecentConnections` | `ConnectionId` | `LastOpenedUtc DESC` | FK cascade |

Owned groups map to prefixed columns on `Connections`: `Ssh_*`, `Sftp_*`, `Rdp_*`, `Credential_*`.

Runtime config applied on **every** connection open via a `DbConnectionInterceptor` (not once at startup):
```
Data Source={DataDir}/remoteflow.db;Cache=Shared;Foreign Keys=True
PRAGMA journal_mode=WAL;  PRAGMA foreign_keys=ON;  PRAGMA busy_timeout=5000;
```

Also: one `IEntityTypeConfiguration` per aggregate, enum-to-int conversions, `IDesignTimeDbContextFactory`
so `dotnet ef` works without the UI project, and the `InitialCreate` migration.

## Acceptance criteria
- [ ] The migration applies to an empty file DB and produces exactly the schema above.
- [ ] **FK enforcement is proved by a test asserting an orphan insert throws.** SQLite's `foreign_keys`
      pragma is per-connection and defaults **off** - forgetting the interceptor silently disables every
      FK constraint in the app, and nothing else in the test suite would notice.
- [ ] `journal_mode` is confirmed to be `wal` on an opened connection.
- [ ] `GetPendingMigrations()` is empty immediately after applying.
- [ ] A fitness test applies all migrations to an empty file DB and asserts both that no migrations are
      pending **and** that the model snapshot matches - this catches "forgot to add a migration" PRs.
- [ ] Cascade behaviour verified: deleting a `Connection` removes its `ConnectionTags` and
      `RecentConnections` rows; deleting a non-empty `Folder` is **rejected**.
- [ ] Enum values round-trip as integers.
- [ ] **No test uses the EF InMemory provider** - it enforces neither foreign keys nor unique indexes, so
      it green-lights code that real SQLite rejects.

## Out of scope
Repositories (#6). Startup migration and backup (#7). Seeding.
'@ },

@{ Number=6; Milestone='1 - Foundation'
   Title='Repositories, UnitOfWork, settings store and SQLite test fixture'
   Labels=@('model:sonnet-5','effort:high','area:data','type:feature','risk:contained')
   Body=@'
```yaml
model: claude-sonnet-5
effort: high
risk: contained
depends_on: [5]
blocks: [13, 14, 25, 27, 48, 53]
read_first:
  - src/RemoteFlow.Persistence/RemoteFlowDbContext.cs
touches:
  - src/RemoteFlow.Application/Abstractions/**
  - src/RemoteFlow.Persistence/Repositories/**
  - tests/RemoteFlow.TestSupport/**
  - tests/RemoteFlow.Persistence.Tests/**
verify: dotnet test tests/RemoteFlow.Persistence.Tests
```

## Goal
Every aggregate is reachable through a repository port, settings are typed, and tests run against real
SQLite temp files.

## Decisions already made - do not re-litigate
- Repositories take `IDbContextFactory`, never a context (see #5).
- **Settings are a KV table plus a typed facade**, not a columnar `AppSettings` row. A columnar row is
  nicer to query but demands a migration for every new setting - unacceptable friction across 8
  milestones. `ISettingsStore.Get<T>(SettingKey<T>)` plus a static `SettingKeys` registry recovers
  compile-time safety and gives one place to see every default.
- **Never the EF InMemory provider.** `SqliteTempDbFixture` creates a temp file, migrates it, and deletes
  on dispose.

## Scope
Ports in `Application.Abstractions`: `IConnectionRepository`, `IFolderRepository`, `ITagRepository`,
`IHostKeyStore`, `ISettingsStore`, `IRecentConnectionStore`, `IUnitOfWork`.

`ISettingsStore`: `Get<T>(SettingKey<T>)`, `Set<T>`, change notification, seeding on first run.
Registered keys with defaults - note `Theme=Dark`:

```
Theme=Dark                      AccentColor
TerminalFontFamily              TerminalFontSize=13
TerminalScrollback=10000        TerminalColorScheme
CursorStyle                     BellMode=None
ReflowOnResize=false            CopyOnSelect=false
CtrlCPolicy=SigintAlways        KeymapProfile=auto
ConfirmCloseActiveSession=true  DefaultShell
SystemTerminalCommand           SftpDownloadDir
RemoteEditTempDir               RemoteEditConflictDefault=Prompt
DefaultHostKeyPolicy=TrustOnFirstUse
SshTransport=Tmds               RecentLimit=20
WindowLayout                    SchemaVersion
ForceFileVault=false            CheckForUpdates=false
```

`tests/RemoteFlow.TestSupport`: `SqliteTempDbFixture`, entity builders, `IClock`/`IGuidProvider` fakes.

## Acceptance criteria
- [ ] Every repository has round-trip tests against a real temp SQLite file.
- [ ] Tag many-to-many add and remove works through `IConnectionRepository`.
- [ ] Cascade deletes verified through the repository layer.
- [ ] An unknown setting key returns its registered default.
- [ ] **`Theme` defaults to `Dark`.**
- [ ] Round-trip works for `bool`, `int`, `string`, an enum, and a record.
- [ ] Changing a setting raises exactly **one** change notification.
- [ ] `SqliteTempDbFixture` deletes its file on dispose (assert no leftovers).
- [ ] No test references the InMemory provider.

## Out of scope
The query/search service (#15). Settings UI (#25 and per-feature). App paths (#7).
'@ },

@{ Number=7; Milestone='1 - Foundation'
   Title='App paths, first-run bootstrap and backup-before-migrate'
   Labels=@('model:sonnet-5','effort:high','area:platform','type:feature','risk:contained')
   Body=@'
```yaml
model: claude-sonnet-5
effort: high
risk: contained
depends_on: [5]
blocks: [9]
read_first:
  - src/RemoteFlow.Persistence/RemoteFlowDbContext.cs
touches:
  - src/RemoteFlow.Application/Abstractions/IAppPaths.cs
  - src/RemoteFlow.Infrastructure/Platform/**
  - src/RemoteFlow.Persistence/DbInitializer.cs
  - tests/RemoteFlow.Infrastructure.Tests/**
verify: dotnet test tests/RemoteFlow.Infrastructure.Tests
```

## Goal
First launch creates its directories and a migrated database; a failed migration never loses data and
never crashes silently.

## Scope
`IAppPaths` - per-OS locations, XDG-correct on Linux:

| OS | Config | Data | Cache | Logs |
|---|---|---|---|---|
| Windows | `%APPDATA%\RemoteFlow` | same | `%LOCALAPPDATA%\RemoteFlow\Cache` | `...\Logs` |
| macOS | `~/Library/Application Support/RemoteFlow` | same | `~/Library/Caches/RemoteFlow` | `~/Library/Logs/RemoteFlow` |
| Linux | `$XDG_CONFIG_HOME/remoteflow` | `$XDG_DATA_HOME/remoteflow` | `$XDG_CACHE_HOME/remoteflow` | `$XDG_STATE_HOME/remoteflow/logs` |

`IDbInitializer.InitializeAsync()` in strict order:
1. If `GetPendingMigrations()` is non-empty, copy `remoteflow.db` to `remoteflow.{yyyyMMdd-HHmmss}.bak`.
2. `MigrateAsync()`.
3. Seed default settings.
4. On failure, surface a startup error dialog **naming the backup path** - never crash silently, never auto-delete.

**Forward-compat guard:** a `SchemaVersion` setting. If the DB's value exceeds the app's, refuse to open
read-write and tell the user to upgrade. This prevents an older build corrupting a newer schema - the
one failure mode a backup does not protect against.

Also `IClock`, `IGuidProvider` (v7), `ISecureRandom` - trivial implementations, all faked in tests.

## Acceptance criteria
- [ ] A fresh launch creates every directory and a migrated database.
- [ ] A `.bak` appears **only** when migrations were pending, and its name carries a timestamp.
- [ ] A DB whose `SchemaVersion` is higher than the app's refuses to open, with a clear message.
- [ ] A corrupt DB file produces a dialog naming the file, not an unhandled exception.
- [ ] Path resolution is correct on all three OSes (`SkippableFact` gated on `OSPlatform`).
- [ ] Guid generation is v7 (assert the version nibble and that sequential calls sort ascending).

## Out of scope
The backup/restore *feature* (Milestone 7 - this is startup safety only). Logging sinks (#9).
'@ },

@{ Number=8; Milestone='1 - Foundation'
   Title='Avalonia 12 shell, dark-by-default theming, design tokens and navigation'
   Labels=@('model:sonnet-5','effort:medium','area:ui','type:feature','risk:contained')
   Body=@'
```yaml
model: claude-sonnet-5
effort: medium
risk: contained
depends_on: [2]
blocks: [9, 16, 21, 32, 39, 49]
read_first:
  - docs/adr/0010-dark-first-theming.md
touches:
  - src/RemoteFlow.UI/App.axaml
  - src/RemoteFlow.UI/Styles/**
  - src/RemoteFlow.UI/Views/MainWindow.axaml
  - src/RemoteFlow.UI/Navigation/**
  - tests/RemoteFlow.UI.Tests/**
verify: dotnet test tests/RemoteFlow.UI.Tests
```

## Goal
The app launches **dark**, with a design-token dictionary every later view styles against, and a
navigation shell whose page state and window geometry survive a restart.

## Decisions already made - do not re-litigate
- **Dark is the default** (user amendment), via `RequestedThemeVariant="Dark"`. Light tokens are defined
  but not default.
- `Avalonia.Themes.Fluent` + `Avalonia.Fonts.Inter`. **Skip Semi.Avalonia in v1** - one less dependency
  with a version floor to chase, which matters because the terminal control already pins Avalonia exactly.
- MVVM via `CommunityToolkit.Mvvm` source generators, not ReactiveUI.

## Scope
- `App.axaml`: FluentTheme, Inter, `RequestedThemeVariant="Dark"`.
- A design-token resource dictionary: surfaces (0/1/2), borders, text primary/secondary/disabled, accent,
  semantic success/warning/danger, and the **`EnvironmentKind` palette** (Dev green / Staging amber /
  Prod red) - consumed later by #16 and #21.
- `MainWindow` + navigation sidebar: Connections / Terminals / Transfers / Settings.
- `INavigationService` with a page registry and placeholder pages that **preserve their view-model state**
  across navigation.
- Window size/position/maximised state persisted via `ISettingsStore`, clamped to a visible monitor on restore.

## Acceptance criteria
- [ ] The app launches dark on Windows, macOS and Linux with **no flash of light** during startup.
- [ ] Switching theme variant at runtime restyles the whole window without a restart.
- [ ] Every colour used by a view comes from a token, not a literal hex.
- [ ] The `EnvironmentKind` palette is defined and passes >= 4.5:1 contrast on the dark surface.
- [ ] Sidebar navigation preserves each page's view-model state.
- [ ] Window geometry survives a restart; geometry saved off-screen is clamped back onto a visible monitor.
- [ ] The sidebar is fully keyboard-navigable (arrows + Enter).
- [ ] A headless test asserts the resolved theme variant is `Dark` with no settings present.
- [ ] **Verify how DevTools ships in Avalonia 12 and record the finding in a comment or ADR note** -
      `Avalonia.Diagnostics` has no 12.x release on NuGet (it stops at 11.3.19). Do not block on this.

## Out of scope
Page content (#16, #21, #39). DI wiring (#9).
'@ },

@{ Number=9; Milestone='1 - Foundation'
   Title='DI composition root and logging with credential redaction'
   Labels=@('model:opus-5','effort:high','area:core','type:infra','risk:load-bearing')
   Body=@'
```yaml
model: claude-opus-5
effort: high
risk: load-bearing
depends_on: [8]
blocks: [10, 57]
read_first:
  - docs/adr/0001-clean-architecture-layering.md
  - src/RemoteFlow.UI/App.axaml.cs
touches:
  - src/RemoteFlow.Desktop/Program.cs
  - src/RemoteFlow.Desktop/DependencyInjection/**
  - src/RemoteFlow.*/DependencyInjection/*Extensions.cs
  - tests/RemoteFlow.Infrastructure.Tests/**
verify: dotnet build; dotnet test
```

## Goal
One composition root that every later service registers into, and a logger that cannot leak a secret.

## Why this is load-bearing
Roughly 50 later issues will copy whatever registration pattern this issue establishes, and `Desktop`
is the only project allowed to see both `UI` and `Infrastructure`/`Persistence`. Getting the seam wrong
here means moving nearly everything later.

## Scope
- `Program.cs` using `HostApplicationBuilder`; Avalonia app built from the container.
- One `AddRemoteFlowX()` extension **per assembly** (`AddRemoteFlowApplication`,
  `AddRemoteFlowPersistence`, `AddRemoteFlowInfrastructure`, `AddRemoteFlowUI`), composed in `Desktop`.
- View models resolved from the container, not `new`-ed in XAML.
- `ILogger<T>` with a rolling file sink under `IAppPaths.LogDirectory`, 7 files retained.
- A **redacting enricher**: never log credential values, private-key material, passphrases, or SFTP file
  contents. Redact by known property names *and* by scanning for registered secret markers.
- Global unhandled-exception and `TaskScheduler.UnobservedTaskException` handlers that log and show a
  dialog rather than dying silently.

## Acceptance criteria
- [ ] `MainWindow` and its view model resolve from DI.
- [ ] A DI validation test asserts **every registered service can be constructed** (walk the
      `IServiceCollection` and resolve each descriptor).
- [ ] Logs are written under `IAppPaths.LogDirectory` and roll at the configured size.
- [ ] A test writes a known secret through the logger and asserts it does **not** appear in the output file.
- [ ] An unhandled exception on a background task is logged rather than terminating the process silently.
- [ ] The architecture fitness test from #2 still passes - `UI` gained no reference to Infrastructure.

## Out of scope
Any feature service. Log viewer UI (#57).
'@ }
)
