@(
@{ Number=10; Milestone='2 - Connection Management'
   Title='ICredentialProvider abstraction and Windows Credential Manager implementation'
   Labels=@('model:opus-5','effort:xhigh','area:security','type:feature','risk:load-bearing')
   Body=@'
```yaml
model: claude-opus-5
effort: xhigh
risk: load-bearing
depends_on: [9]
blocks: [11, 12, 18, 45]
read_first:
  - docs/adr/0007-credential-storage.md
touches:
  - src/RemoteFlow.Application/Abstractions/ICredentialProvider.cs
  - src/RemoteFlow.Infrastructure/Security/**
  - tests/RemoteFlow.Infrastructure.Tests/**
verify: dotnet test tests/RemoteFlow.Infrastructure.Tests
```

## Goal
Secrets are stored in the OS credential store and never in SQLite, behind a port that three platform
implementations satisfy.

## Why this is in Milestone 2 rather than Milestone 4
The connection editor (#18) needs to store a password the moment it exists, and #52's export crypto
reuses #12's Argon2id/AES-GCM primitives. Deferring credentials to the SSH milestone forces a plaintext
placeholder that would then have to be ripped out.

## Decisions already made - do not re-litigate
- SQLite stores only the opaque `StoreKey` plus `StoreProvider`; the secret lives in the OS store.
- Key naming: `remoteflow/connection/{connectionId}/{kind}` - opaque to callers.
- `StoreProvider` records *which* store wrote the secret (`windows-credman`, `macos-keychain`,
  `libsecret`, `file-vault`) so a machine migration can report "N secrets unavailable on this machine"
  rather than silently failing authentication.

## Scope
```csharp
ICredentialProvider
    string Name { get; }                          // matches CredentialRef.StoreProvider
    bool IsAvailable { get; }
    Task<SecretHandle?> GetAsync(string storeKey, CancellationToken ct);
    Task SetAsync(string storeKey, ReadOnlyMemory<char> secret, string displayName, CancellationToken ct);
    Task DeleteAsync(string storeKey, CancellationToken ct);
```
- `SecretHandle : IDisposable` - zeroes its buffer on dispose.
- `CredentialProviderSelector` - picks the provider for the current OS, honours the `ForceFileVault` setting.
- `WindowsCredentialProvider` - `CredWrite`/`CredRead`/`CredDelete` P/Invoke, `CRED_TYPE_GENERIC`,
  `CRED_PERSIST_LOCAL_MACHINE`. DPAPI-encrypted file as the fallback when Credential Manager is unavailable.

## Acceptance criteria
- [ ] Store, retrieve and delete round-trip on Windows.
- [ ] Retrieving a missing key returns `null` - it does **not** throw.
- [ ] `SecretHandle` zeroes its backing buffer on dispose (assert the buffer contents).
- [ ] No secret value appears in any log line or exception message (extend #9's redaction test).
- [ ] The selector chooses the right provider per OS and respects `ForceFileVault`.
- [ ] A secret longer than 2560 bytes (the CredMan blob limit) fails with a clear, typed error rather
      than silent truncation.
- [ ] Deleting a connection's credentials removes every `kind` under that connection's key prefix.

## Out of scope
macOS (#11), Linux (#12), editor UI (#18).
'@ },

@{ Number=11; Milestone='2 - Connection Management'
   Title='macOS Keychain credential provider'
   Labels=@('model:opus-5','effort:high','area:security','type:feature','risk:contained')
   Body=@'
```yaml
model: claude-opus-5
effort: high
risk: contained
depends_on: [10]
blocks: []
read_first:
  - src/RemoteFlow.Application/Abstractions/ICredentialProvider.cs
  - src/RemoteFlow.Infrastructure/Security/WindowsCredentialProvider.cs
touches:
  - src/RemoteFlow.Infrastructure/Security/MacOsKeychainProvider.cs
  - tests/RemoteFlow.Infrastructure.Tests/**
verify: dotnet test tests/RemoteFlow.Infrastructure.Tests --filter Platform=macOS
```

## Goal
`ICredentialProvider` backed by the macOS Keychain via Security.framework.

## Decisions already made - do not re-litigate
- **P/Invoke `SecItemAdd`/`SecItemCopyMatching`/`SecItemUpdate`/`SecItemDelete` directly. Do NOT shell out
  to `/usr/bin/security`** - that puts the secret in `argv`, where any user on the box can read it from
  `ps`. This is the single most important constraint in this issue.
- Generic-password items, service `io.remoteflow`, account = the `storeKey`.

## Scope
`MacOsKeychainProvider` implementing the port from #10, using `[DllImport("/System/Library/Frameworks/Security.framework/Security")]`.
Handle `errSecItemNotFound` (-25300) as "missing", `errSecDuplicateItem` (-25299) as "update instead of add",
and `errSecUserCanceled`/`errSecAuthFailed` as a typed "user declined" result.

## Acceptance criteria
- [ ] Store, retrieve, update and delete round-trip on macOS 14+.
- [ ] **No secret is ever passed as a process argument** - verified by inspecting the implementation for
      any `Process`/`ProcessStartInfo` usage (there must be none).
- [ ] The first-access authorisation prompt does not block or deadlock the UI thread (call on a
      background thread and await).
- [ ] `errSecItemNotFound` returns `null`, not an exception.
- [ ] `CFRelease` is called on every created CF object - no leaks under repeated calls (loop 1000x and
      assert stable memory).
- [ ] Tests are `SkippableFact` gated on `OSPlatform.OSX` so the suite stays green on Windows and Linux.

## Out of scope
iCloud Keychain sync - never, the app has no cloud dependency.
'@ },

@{ Number=12; Milestone='2 - Connection Management'
   Title='Linux libsecret provider and encrypted file vault fallback'
   Labels=@('model:opus-5','effort:xhigh','area:security','type:feature','risk:load-bearing')
   Body=@'
```yaml
model: claude-opus-5
effort: xhigh
risk: load-bearing
depends_on: [10]
blocks: [52]
read_first:
  - src/RemoteFlow.Application/Abstractions/ICredentialProvider.cs
  - docs/adr/0007-credential-storage.md
touches:
  - src/RemoteFlow.Infrastructure/Security/LibSecretProvider.cs
  - src/RemoteFlow.Infrastructure/Security/EncryptedFileVaultProvider.cs
  - src/RemoteFlow.Infrastructure/Security/Crypto/**
  - tests/RemoteFlow.Infrastructure.Tests/**
verify: dotnet test tests/RemoteFlow.Infrastructure.Tests
```

## Goal
Linux credential storage that works on a GNOME or KDE desktop **and** on a headless or minimal box with
no keyring at all - closing the gap the requirements left open.

## Decisions already made - do not re-litigate
- libsecret via `dlopen` P/Invoke (`secret_password_store_sync` / `lookup` / `clear`), schema
  `io.remoteflow.Secret`. `dlopen` rather than a hard `DllImport` so a missing library is a *detectable*
  condition, not a startup crash.
- **The fallback is a passphrase-derived encrypted vault, and it is NEVER silent.** When libsecret is
  absent the app shows a persistent banner reading "OS keyring unavailable - using passphrase vault".
  A silent downgrade in credential security is unacceptable.
- `ForceFileVault` lets a user opt into the vault deliberately (useful on WSL, headless, minimal WMs).
- **The crypto primitives built here are reused by #52's encrypted credential export** - put them in
  `Security/Crypto/` (`IPassphraseKdf`, `IAuthenticatedCipher`), not inline in the provider.

## Scope
`EncryptedFileVaultProvider`:
- Vault at `{ConfigDir}/vault.rfv`, file mode `0600`.
- Key = Argon2id(passphrase, **m=64 MiB, t=3, p=1**, 32-byte random salt).
- AES-256-GCM per record, random 96-bit nonce, AAD binding the record to its `storeKey`.
- Unlocked once per app run; the derived key is held pinned in memory and zeroed on shutdown.
- Header carries the KDF parameters so future hardening stays backward-compatible.

## Acceptance criteria
- [ ] Round-trips through libsecret on GNOME and on KDE.
- [ ] With libsecret absent, falls back to the vault **and surfaces the banner** - assert the
      "keyring unavailable" state is observable, not just logged.
- [ ] A wrong passphrase is rejected **without revealing** whether the vault is otherwise valid or how
      many records it holds.
- [ ] Flipping any single byte of the vault file fails GCM tag verification.
- [ ] The vault file's mode is exactly `0600` after creation (assert via `File.GetUnixFileMode`).
- [ ] KDF parameters are read from the header, so a vault written with different parameters still opens.
- [ ] **Known-answer tests for Argon2id and AES-GCM run on all three OSes** - the crypto must be
      verifiable on the dev machine even though libsecret is Linux-only.
- [ ] The derived key buffer is zeroed on shutdown.

## Out of scope
FIDO/YubiKey unlock. The backup export format (#52 - this issue only provides the primitives).
'@ },

@{ Number=13; Milestone='2 - Connection Management'
   Title='Connection and tag CRUD services with validation'
   Labels=@('model:sonnet-5','effort:medium','area:core','type:feature','risk:contained')
   Body=@'
```yaml
model: claude-sonnet-5
effort: medium
risk: contained
depends_on: [6]
blocks: [15, 18, 45]
read_first:
  - src/RemoteFlow.Domain/Entities/Connection.cs
  - src/RemoteFlow.Application/Abstractions/IConnectionRepository.cs
touches:
  - src/RemoteFlow.Application/Services/ConnectionService.cs
  - src/RemoteFlow.Application/Services/TagService.cs
  - src/RemoteFlow.Application/Validation/ConnectionValidator.cs
  - tests/RemoteFlow.Application.Tests/**
verify: dotnet test tests/RemoteFlow.Application.Tests
```

## Goal
Create / edit / delete / duplicate / move / favorite for connections, plus tag management, with
protocol-conditional validation - all unit-testable with no infrastructure.

## Decisions already made - do not re-litigate
- Default ports: SSH 22, SFTP 22, RDP 3389.
- Protocol-specific validation lives here, **not** in the domain type system (see #4).
- Tags are case-insensitively de-duplicated: creating "Prod" when "prod" exists reuses the existing tag.

## Scope
`IConnectionService`: `CreateAsync`, `UpdateAsync`, `DeleteAsync`, `DuplicateAsync`, `MoveToFolderAsync`,
`ToggleFavoriteAsync`. Maintains `ModifiedUtc`. Deleting a connection also deletes its credential
entries (via `ICredentialProvider`) and its `RecentConnections` row.

`ITagService`: create, rename, delete, **merge**, assign/unassign, orphan cleanup, usage counts.

`ConnectionValidator`: name and host required; port in range; username required for SSH/SFTP when
`AuthMethod != None`; private key path required when `AuthMethod == PrivateKey`; user-ready messages.

## Acceptance criteria
- [ ] `DuplicateAsync` produces `"Name (copy)"` with a **new Id**, copies tags, and copies **no credential**.
- [ ] Deleting a connection deletes its credentials and its recent row.
- [ ] Changing protocol resets the port **only if** it still held the previous protocol's default -
      a user-chosen port survives a protocol change.
- [ ] Validation messages are user-ready strings, not exception text or enum names.
- [ ] Creating tag "Prod" when "prod" exists reuses the existing tag rather than creating a duplicate.
- [ ] `TagService.MergeAsync` moves join rows, de-duplicates them, and deletes the source tag.
- [ ] Deleting a tag removes its join rows but **no connections**.
- [ ] Usage counts are correct after add, remove and merge.

## Out of scope
Folders (#14). Query and search (#15). Any UI.
'@ },

@{ Number=14; Milestone='2 - Connection Management'
   Title='Folder service with path and depth maintenance and cycle rejection'
   Labels=@('model:sonnet-5','effort:high','area:core','type:feature','risk:contained')
   Body=@'
```yaml
model: claude-sonnet-5
effort: high
risk: contained
depends_on: [6]
blocks: [15, 18]
read_first:
  - src/RemoteFlow.Domain/Entities/Folder.cs
touches:
  - src/RemoteFlow.Application/Services/FolderService.cs
  - tests/RemoteFlow.Application.Tests/**
verify: dotnet test tests/RemoteFlow.Application.Tests
```

## Goal
A folder tree whose derived `Path`/`Depth` columns never drift from `ParentId`, and which cannot be
corrupted into a cycle.

## Decisions already made - do not re-litigate
- `ParentId` is the source of truth; `Path` and `Depth` are derived and app-maintained (see #4).
- Cycle detection is `target.Path.StartsWith(source.Path)` - O(1), no recursive query.
- **Default delete behaviour is reparent children to the parent** - the least destructive action that
  still succeeds. Delete-subtree is offered explicitly; a non-empty folder is never silently destroyed.
- Depth cap of 16, with a clear error rather than a stack overflow later.

## Scope
`IFolderService`: `CreateAsync`, `RenameAsync`, `MoveAsync`, `DeleteAsync(id, FolderDeleteMode)`.
Rename and move recompute `Path`/`Depth` across the whole affected subtree **in one transaction**.

## Acceptance criteria
- [ ] Renaming a folder rewrites every descendant's `Path` in a single transaction (assert with a
      3-level tree).
- [ ] Moving a folder into its own descendant is **rejected** with a typed error.
- [ ] Moving a folder onto a sibling name collision is rejected.
- [ ] Deleting a non-empty folder with `Reparent` moves children to the parent and deletes only the folder.
- [ ] Deleting a non-empty folder with `DeleteSubtree` removes the subtree and reassigns or deletes its
      connections per the chosen mode - and this mode is never the default.
- [ ] Creating a folder at depth 17 is rejected with a clear error.
- [ ] After every operation, a consistency check asserts `Path`/`Depth` match `ParentId` for all rows.
- [ ] A failed move leaves the tree **exactly** as it was (inject a fault mid-transaction and assert rollback).

## Out of scope
Drag-and-drop UI (#16). Connection CRUD (#13).
'@ },

@{ Number=15; Milestone='2 - Connection Management'
   Title='Connection query service: search, filter, sort and projections'
   Labels=@('model:sonnet-5','effort:medium','area:data','type:feature','risk:contained')
   Body=@'
```yaml
model: claude-sonnet-5
effort: medium
risk: contained
depends_on: [13, 14]
blocks: [16, 17]
read_first:
  - src/RemoteFlow.Persistence/RemoteFlowDbContext.cs
touches:
  - src/RemoteFlow.Application/Queries/**
  - tests/RemoteFlow.Persistence.Tests/**
verify: dotnet test tests/RemoteFlow.Persistence.Tests
```

## Goal
One query surface that both the explorer tree and the quick-connect palette read from, without
over-fetching.

## Scope
`IConnectionQueryService` taking a `ConnectionFilter`:
`Text`, `Protocols`, `Environments`, `Tags` + `TagMatch (And|Or)`, `FolderId` + `IncludeDescendants`,
`FavoritesOnly`. Sort by name / host / last-opened / sort-order.

Returns `ConnectionListItem` **projections** (Id, Name, Host, Port, Protocol, Environment, IsFavorite,
FolderPath, TagNames, LastOpenedUtc) - not full entities.

Plus a relevance ranking for the `Ctrl+K` palette: prefix match > substring > fuzzy, with a recency boost.

## Decisions already made - do not re-litigate
- `LIKE` + `NOCASE`, no FTS5 in v1 (see #5). Rank in memory after a broad `LIKE`.
- Subtree filtering uses the `Path LIKE '/Prod/%'` index from #5, not a recursive query.

## Acceptance criteria
- [ ] Text search spans Name, Host, Username, Notes **and** tag names.
- [ ] `IncludeDescendants` uses the `Path` index - assert via query plan, or a >1000-row test completing
      in <50 ms.
- [ ] Tag `And` requires all tags; `Or` requires any - both verified.
- [ ] Projections do not load owned option groups or navigation collections (assert the generated SQL
      selects only projected columns).
- [ ] Palette ranking puts a prefix match above a substring match, and a recently-opened match above an
      equally-scoring stale one.
- [ ] Filters compose: protocol + environment + tag + subtree together.
- [ ] An empty filter returns everything, ordered by the default sort.

## Out of scope
Any UI (#16, #17).
'@ },

@{ Number=16; Milestone='2 - Connection Management'
   Title='Connection explorer UI with favorites, recent, environment badges and drag-drop'
   Labels=@('model:sonnet-5','effort:medium','area:ui','type:feature','risk:contained')
   Body=@'
```yaml
model: claude-sonnet-5
effort: medium
risk: contained
depends_on: [8, 15]
blocks: [17, 18, 59]
read_first:
  - src/RemoteFlow.UI/Styles/DesignTokens.axaml
  - src/RemoteFlow.Application/Queries/IConnectionQueryService.cs
touches:
  - src/RemoteFlow.UI/Views/Connections/**
  - src/RemoteFlow.UI/ViewModels/Connections/**
  - tests/RemoteFlow.UI.Tests/**
verify: dotnet test tests/RemoteFlow.UI.Tests
```

## Goal
The primary navigation surface: a folder tree of connections with virtual Favorites and Recent nodes,
environment badges, and drag-drop reorganisation.

## Decisions already made - do not re-litigate
- `EnvironmentKind` badges use the #8 token palette and **always pair colour with an icon or text
  label** - never colour alone. Production must be unmistakable at a glance among ten entries.
- `IsExpanded` persists on the `Folder` entity - this is a single-user local app, so a separate UI-state
  store would be architectural theatre.
- Recent shows only **successful** connections, capped at `RecentLimit`.

## Scope
- `TreeDataGrid` (or `TreeView`) of folders + connections, virtualised.
- Virtual root nodes: **Favorites** and **Recent**.
- Drag-drop: reorder within a folder, reparent across folders, reject invalid targets with visible feedback.
- Context menus: Connect, Open SFTP, Launch RDP, Edit, Duplicate, Delete, New Folder, Rename.
- Multi-select, inline rename (F2), Delete key, Enter to connect.
- Empty state with a "create your first connection" affordance.
- `IRecentConnectionStore` updated on successful session open; clear-history action.

## Acceptance criteria
- [ ] 1000 connections across 100 folders scroll smoothly (virtualisation confirmed - assert realised
      row count stays bounded).
- [ ] Drag-drop persists through `IFolderService`/`IConnectionService` and rejects invalid targets with
      feedback rather than silently reverting.
- [ ] Folder expansion state survives an app restart.
- [ ] **A Production connection is visually unmistakable, and its badge carries an icon or text cue as
      well as colour** (colour-blind safe).
- [ ] `ColorOverrideHex` is respected where set.
- [ ] Fully keyboard-navigable: arrows to move, Enter to connect, F2 to rename, Delete to delete.
- [ ] Recent updates on a **successful** open only; a failed connection never appears.
- [ ] Recent is capped at `RecentLimit`; deleting a connection removes its recent entry.
- [ ] The details of an entry edited elsewhere update live in the tree.

## Out of scope
Search box and palette (#17). Editor form (#18). Session opening internals (#34, #39, #45).
'@ },

@{ Number=17; Milestone='2 - Connection Management'
   Title='Search, filter chips and Ctrl+K quick-connect palette'
   Labels=@('model:sonnet-5','effort:medium','area:ui','type:feature','risk:contained')
   Body=@'
```yaml
model: claude-sonnet-5
effort: medium
risk: contained
depends_on: [15, 16]
blocks: []
read_first:
  - src/RemoteFlow.Application/Queries/IConnectionQueryService.cs
touches:
  - src/RemoteFlow.UI/Views/Connections/SearchBar.axaml
  - src/RemoteFlow.UI/Views/CommandPalette/**
  - tests/RemoteFlow.UI.Tests/**
verify: dotnet test tests/RemoteFlow.UI.Tests
```

## Goal
Find-and-connect in a couple of keystrokes, from anywhere in the app.

## Scope
- A debounced search box (150 ms) filtering the explorer tree.
- Filter chips: protocol, environment, tags, favorites-only; an active-filter summary with clear-all.
- `Ctrl+K` command palette over any page: fuzzy ranking with recency boost (from #15), Enter connects,
  Escape closes and restores focus to wherever it was.

## Acceptance criteria
- [ ] Typing filters the tree **without blocking input** - the 150 ms debounce is observable, and
      keystrokes are never dropped.
- [ ] Chips compose with each other and with the text query.
- [ ] Clear-all resets every chip and the text box in one action.
- [ ] `Ctrl+K` opens over any page, including the terminal workspace, and does **not** reach the PTY
      (coordinate with #22's reserved-shortcut set).
- [ ] Enter on a palette result opens the connection; Escape restores focus to the previously focused
      control.
- [ ] An empty result set shows a helpful state, not a blank list.
- [ ] Palette results show enough to disambiguate two same-named hosts (folder path + host).

## Out of scope
Saved searches. The ranking algorithm itself (#15).
'@ },

@{ Number=18; Milestone='2 - Connection Management'
   Title='Connection editor and details pane with credential capture'
   Labels=@('model:sonnet-5','effort:high','area:ui','type:feature','risk:contained')
   Body=@'
```yaml
model: claude-sonnet-5
effort: high
risk: contained
depends_on: [10, 13, 14]
blocks: [33, 47, 59]
read_first:
  - src/RemoteFlow.Application/Validation/ConnectionValidator.cs
  - src/RemoteFlow.Application/Abstractions/ICredentialProvider.cs
touches:
  - src/RemoteFlow.UI/Views/Connections/ConnectionEditor.axaml
  - src/RemoteFlow.UI/Views/Connections/ConnectionDetails.axaml
  - src/RemoteFlow.UI/ViewModels/Connections/**
  - tests/RemoteFlow.UI.Tests/**
verify: dotnet test tests/RemoteFlow.UI.Tests
```

## Goal
Create and edit a connection - including storing its secret - and read it back in a details pane, with
protocol-conditional sections and no way to lose unsaved work silently.

## Decisions already made - do not re-litigate
- **A password must never be bound to a plain-text view-model property that could be serialised into
  window-layout state.** Use a write-only capture path straight into `ICredentialProvider`.
- SQLite receives only the `StoreKey`; the secret goes to the OS store.
- Creating and editing use the **same** view.

## Scope
- Master-detail: form on the right, protocol-conditional sections (SSH / SFTP / RDP).
- Fields: name, host, port, protocol, username, auth method, notes, folder picker, tag input,
  environment picker with live colour preview, favorite toggle.
- Credential capture: password field, plus private-key passphrase when `AuthMethod == PrivateKey`.
  Three observable states: **stored** / **not stored** / **unavailable on this machine** (the
  `StoreProvider` from #10 doesn't match the current machine), with a re-enter action for the third.
  Rotate and clear actions. A provider indicator showing which store holds it.
- Read-only details pane: host, port, protocol, auth, environment badge, tags, folder path, notes,
  last connected - plus primary actions Connect / Open SFTP / Launch RDP / Edit / Duplicate / Delete.
- Dirty tracking with an unsaved-changes guard.

## Acceptance criteria
- [ ] Switching protocol shows and hides the right sections and adjusts the default port per #13's rule.
- [ ] Validation errors appear **inline on the offending field** and block save.
- [ ] Escape or navigating away with unsaved changes prompts.
- [ ] Saving stores the secret via `ICredentialProvider` and persists only the `StoreKey` to SQLite -
      assert by reading the DB row and confirming no secret material is present.
- [ ] A row whose secret is missing shows **"unavailable on this machine"** with a re-enter action.
- [ ] No view-model property exposes the password as readable plain text (assert by reflection over the
      view model's public surface).
- [ ] Details-pane actions enable per **capability**, not per primary protocol: Open SFTP is offered for
      both `Ssh` and `Sftp`.
- [ ] Delete confirms and names the connection.
- [ ] Tab order is sensible and the whole form is completable by keyboard.

## Out of scope
SSH key file picking and generation (#33). RDP-specific options (#47). Terminal or SFTP sessions.
'@ }
)
