# ADR-0023: Automatic backup

- Status: Accepted
- Date: 2026-08-24

## Context

Backup was manual only: open the Backup page, click Export, choose a folder. The archive a user has is
therefore the one they last remembered to make, which in practice is the one from before the change they
now want to undo. A backup taken on a schedule nobody has to remember is worth more than a better archive
format.

The trigger is available for free. `IConnectionChangeNotifier` already announces every connection mutation,
raised after commit by `ConnectionService` and `ConnectionCredentialService`. `IBackupService.ExportAsync`
already writes a complete, optionally credential-encrypted archive. `TransferEngine` and
`ObjectStorageTransferEngine` already upload files. What was missing was a debounce, a destination, and a
retention rule.

## Decision

### Folder and tag edits get their own notifier

`FolderService` and `TagService` announced nothing, yet both write data that lives in a backup archive —
`folders.json`, `tags.json`, `connection-tags.json`. Backups would have gone stale after a folder rename
with no sign that anything was wrong.

They raise a new `IWorkspaceChangeNotifier` rather than new members on `ConnectionChangeKind`. Two reasons:
`ConnectionChangedEventArgs.ConnectionId` would end up carrying a folder's ID, which is a field name that
lies; and `ConnectionsPageViewModel` falls through to a full `RefreshAsync()` for any kind it does not
recognise, so every tag assignment would force a connection-list reload it does not do today.

Both services signal after `unitOfWork.ExecuteAsync` returns and only on success, copying
`ConnectionService.NotifyAfterAsync`. `TagService.CreateAsync` needed care: it returns
`Result<Tag>.Success(existing)` when the name is taken, having written nothing, so it signals only when a
row was actually added.

### A fixed 30-second quiet period, not a schedule

One save of a connection raises three or four separate signals; a multi-select drag-reorder raises two per
item. Without coalescing, one edit would produce a fistful of archives. The wait is deliberately not
configurable — it is a debounce, not a policy, and there is nothing useful for a user to decide about it.

`Schedule()` is called synchronously from a domain event, sometimes with a SQLite write transaction still
open — `FolderService.DeleteAsync(DeleteSubtreeAndConnections)` calls `connectionService.DeleteAsync`
inside the ambient unit of work. So it does no I/O, holds no lock a run holds, takes no repository, and
swallows everything: a backup going wrong must never surface as a connection that failed to save. The run
itself is always safe to start from committed state, because the last signal of that burst is the
post-commit folder one and each signal re-arms the timer.

### Credentials are always included, and a missing passphrase blocks the run

An archive without credentials restores a list of hostnames. The user believes they hold a backup; they
hold half of one. So automatic archives always set `IncludeCredentials: true` with
`AllowWeakPassphrase: false`, and the passphrase is stored once in the OS keychain under
`remoteflow/auto-backup/passphrase` — outside the `remoteflow/connection/{id}/...` namespace, because it is
not a connection credential and should not look like one to anything that later walks those keys.

When no passphrase is found the run records `Blocked` and writes nothing. It never quietly downgrades to a
credential-free archive.

This also settles a problem created elsewhere. `EfBackupDataSource` exports every settings row with no
opt-out, so the automatic-backup configuration travels inside archives and a `Replace` import installs a
foreign one — including `IsEnabled = true` pointing at somebody else's server. The passphrase is the thing
that cannot travel, so an imported "enabled" lands on a machine that cannot produce an archive and says so.
**Do not add a passphrase fallback.** It is load-bearing.

The runner also re-reads and re-validates its configuration on every run and never caches it: imports write
through `IBackupImportStore` directly, so `ISettingsStore.SettingChanged` never fires for them.

### The archive name is the retention safety boundary

```
remoteflow-auto-20260824T131500Z-9f3a01bc.rfbak.zip
```

Retention deletes files from a folder the user chose, which may hold anything else they keep there. The
entire guarantee that it will not touch those files is one strict parser, `AutoBackupNaming.TryParse`:
exact prefix, exact suffix, exactly sixteen timestamp characters, a hyphen, exactly eight lowercase hex
digits, all compared ordinally. **Anything it rejects is never a deletion candidate** — including a manual
export, which `BackupExportViewModel` names `RemoteFlow-backup-{timestamp}.zip`, and including a `.part`
file from a torn upload.

The UTC timestamp sorts lexicographically in the same order it sorts chronologically, so retention orders
by name and never trusts a timestamp reported by the destination: S3 exposes no modification time you
control, and an SFTP server's clock can be wrong. The random nonce keeps two changes in the same second —
or two machines sharing one destination — from colliding.

Pruning also refuses to act when the archive it just wrote is absent from the listing. If our view of the
destination does not include our own file, this is not the destination we think it is, and deleting on that
assumption is how every backup gets lost at once. A failed delete is reported alongside a successful run
rather than turning it into a failed one: a bucket that denies deletes should still receive backups.

### One destination port, three implementations

`IAutoBackupDestination` covers publish, list and delete. Local staging happens inside the destination
folder so publishing is an atomic same-volume move; remote destinations stage under the cache directory and
publish through the existing transfer engines, which already do the temporary-name-then-rename dance for
SFTP. Object storage has no atomic publish and the engine's own documentation says so; a torn multipart
upload is aborted, and anything it does leave behind fails to parse and is therefore invisible to retention.

Both engines return `TransferItemStatus.Conflict` rather than transferring when given no conflict resolver,
so an always-overwrite resolver is supplied. Collisions are effectively impossible given the nonce; the
resolver exists so that a Conflict never masquerades as a transport failure.

### Status lives in a file, not a settings row

`auto-backup-status.json` sits beside the database in the data directory, written aside and moved into
place. It is not a settings row for the same reason the configuration being one is a hazard: settings are
exported, so a `Replace` import would install another machine's "last run succeeded" — the one claim this
feature cannot afford to get wrong. The data directory rather than the cache, so clearing caches does not
make the page report that automatic backup has never run.

`PendingChanges` is written *before* the quiet period rather than after it. That is what lets the next
launch know a backup is owed when a quit or a crash cuts the wait short, and it is why there is no retry
timer: an unreachable destination is retried at the next change and at the next launch, and nowhere else.

### No scheduler, no hosted service

`RemoteFlow.Application` may reference only Domain, DI abstractions and logging abstractions — enforced by
`DependencyDirectionTests`. So the runner is started from `App.StartupAction` and stopped through
`IDisposable`. It implements `IDisposable` rather than only `IAsyncDisposable` because `Program.cs` does
`using var host`, a synchronous `ServiceProvider.Dispose()`, which throws for a singleton that offers only
the async form.

A run in flight at shutdown is cancelled rather than awaited. The archive is a full snapshot and the next
launch will make another; holding process exit open on an SSH handshake is the worse trade.

## Consequences

- The backup format is unchanged. An automatic archive is byte-identical in structure to a manual export —
  same manifest, same entries, `formatVersion: 1`. Only the file name differs, and `docs/backup-format.md`
  says nothing about file names. Resist stamping an "automatic" flag into the manifest: that is a format
  change needing a compatibility story, and it buys nothing.
- `settings.json` inside every archive gains one `AutoBackup` row. Settings are opaque key/value pairs to
  the reader, so the existing additive-compatibility rules already cover it. The value holds a connection
  ID and possibly a local path — no secret — so the documented security boundary is unchanged.
- Two machines pointed at one destination will prune each other's archives; the naming is shared and
  retention counts what it can see. The manifest records the machine name, and the Backup page says so.
- Changing the passphrase does not re-encrypt archives already written. Each is decryptable only with the
  passphrase in force when it was made. This is the most likely support question, and it is said on the
  page as well as here.
- **A credential store that will not open is reported, not thrown.** `AutoBackupPassphraseStore` catches
  broadly — `Exception when (not OperationCanceledException)`, the same shape `ConnectionCredentialService`
  uses — because providers throw types declared in the infrastructure layer that Application cannot name.
  `InspectAsync` distinguishes "no passphrase set" from "the store will not open": only the first is fixed
  by typing a new one, and offering that to somebody with a locked vault wastes their time. That distinction
  is read from the selected provider's own state — `ICredentialVault.IsUnlocked` — and never inferred from a
  failed lookup. Inferring it was a bug: the passphrase is searched for across every provider, so a Windows
  machine with a perfectly good credential manager was told the idle, permanently locked file vault further
  down the list was its problem. A provider that fails a read is now skipped silently, and a locked vault is
  never read at all. This matters on
  Linux without libsecret, where the selector falls back to `EncryptedFileVaultProvider` — which reports
  itself available and then refuses every read until something opens it. [ADR-0024](0024-credential-vault-unlock.md)
  adds the flow that does; automatic backup still reports the situation rather than assuming it.
- Remote destination paths are typed, not browsed. There is no `IFileBrowserSource` over SFTP, and even the
  object-storage one needs a live session — so a picker on a settings page would have to connect, which
  means a credential or host-key prompt, which the no-dialogs decision rules out. A "Back up now" button
  verifies a typed path immediately instead.
