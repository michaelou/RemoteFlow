# Milestone 9 - Cloud Object Storage.
#
# The `Number` field only orders creation and satisfies seed-github.ps1's contiguous-from-1 check; it is no
# longer the GitHub issue number. That alignment broke during v1 when pull requests consumed numbers from the
# same sequence, so these four were created as #95-#98. The `depends_on` / `blocks` values inside the bodies
# therefore carry the *real* numbers, annotated with titles so an agent can find them either way.
#
#   Number 60 -> #95    Number 62 -> #97
#   Number 61 -> #96    Number 63 -> #98
#
# If these bodies are edited here, push them back with `gh issue edit <real number> --body-file`; the seeder
# only ever creates, and skips a title that already exists.

@(
@{ Number=60; Milestone='9 - Cloud Object Storage'
   Title='S3 and Azure Blob connections, credentials and the provider adapters'
   Labels=@('model:opus-5','effort:xhigh','area:core','area:security','area:storage','type:feature','risk:load-bearing')
   Body=@'
```yaml
model: claude-opus-5
effort: xhigh
risk: load-bearing
depends_on: []
blocks: [96, 97]        # 96 Chunked transfers for objects in the gigabytes; 97 The dual-pane Storage page
read_first:
  - src/RemoteFlow.Application/Abstractions/Sftp/SftpContracts.cs
  - src/RemoteFlow.Domain/Entities/Connection.cs
  - src/RemoteFlow.Domain/Enums/DomainEnums.cs
  - src/RemoteFlow.Application/Services/ConnectionService.cs
  - src/RemoteFlow.Persistence/Configurations/ConnectionConfiguration.cs
  - src/RemoteFlow.Infrastructure/Security/CredentialStoreKeys.cs
  - src/RemoteFlow.Infrastructure/Ssh/ConfiguredSshTransport.cs
  - docs/adr/0007-credential-storage.md
  - docs/backup-format.md
  - docs/third-party-licenses.md
touches:
  - src/RemoteFlow.Domain/**
  - src/RemoteFlow.Application/Abstractions/Storage/**
  - src/RemoteFlow.Application/Validation/ConnectionValidator.cs
  - src/RemoteFlow.Application/Services/ConnectionService.cs
  - src/RemoteFlow.Application/Services/ConnectionCredentialService.cs
  - src/RemoteFlow.Application/Abstractions/Backup/BackupArchiveContracts.cs
  - src/RemoteFlow.Infrastructure/Storage/**
  - src/RemoteFlow.Infrastructure/Security/CredentialStoreKeys.cs
  - src/RemoteFlow.Persistence/**
  - src/RemoteFlow.UI/ViewModels/Connections/**
  - src/RemoteFlow.UI/Views/Connections/**
  - src/RemoteFlow.UI/Services/SshConnectionSessionOpener.cs
  - src/RemoteFlow.UI/Styles/Icons.axaml
  - Directory.Packages.props
  - THIRD-PARTY-NOTICES.md
  - build/licenses/package-licenses.txt
  - docs/adr/0019-object-storage-provider-abstraction.md
  - tests/**
verify: dotnet build; dotnet test; pwsh ./scripts/generate-notices.ps1 -Verify
```

## Goal
An AWS S3 or Azure Blob Storage account can be saved as a connection, its secret key stored in the platform
keychain, and a provider adapter can list, stat, create a folder, delete, read a byte range and write an
object against it. No browsing UI and no transfer engine yet - this is the foundation the next two issues
stand on.

## Decisions already made - do not re-litigate
- **A new `IObjectStorageService`, not an `ISftpService` implementation.** `SftpPublisher` - the whole of
  ADR-0012's atomic-publish story - needs `RenameAsync` and `SetPermissionsAsync`. A faked `ISftpService`
  would compile against S3 and turn every upload publish into a server-side `CopyObject`, billed per byte
  and capped at 5 GiB per copy, on a feature whose premise is multi-gigabyte objects. `ISftpService` is also
  only obtainable from `ISshConnection.OpenSftp()`, so a cloud client would need a sham `ISshConnection`.
- **Reuse `SftpResult` / `SftpFailure` / `SftpError` unchanged.** The names lie; the semantics do not. They
  appear 229 times across 17 files, C# cannot alias an open generic, and a rename would collide with every
  other issue in this milestone for no behavioural gain. Record it as acknowledged debt in the ADR.
- **Add `SftpError.PreconditionFailed = 11`.** HTTP 412 on a ranged GET means "the object changed under you,
  restart the transfer" and no existing member says that honestly. Safe: there is no exhaustive switch over
  `SftpError` anywhere, and it carries no `EnumValueTests` pin. The SFTP adapters never return it.
- **`ProtocolType { S3 = 4, AzureBlob = 5 }`**, plus a `ProtocolTypeExtensions.IsObjectStorage()` predicate
  so the new branch sites do not each hand-list two members. `S3` rather than `AmazonS3` because it also
  covers the S3-compatible services.
- **`Host` and `Port` are reused as the real service endpoint**, derived by the editor but stored and true:
  `s3.{region}.amazonaws.com`, `{account}.blob.core.windows.net`, or the authority of a custom endpoint;
  port 443. Making them optional would mean relaxing validation in two layers, a nullable-column migration
  on an indexed `.IsRequired()` column, and auditing every non-null `Host` consumer - to remove a field that
  has a truthful value. Derive `Host` with the same "only overwrite while the user has not hand-edited it"
  rule already used for `Port` at `ConnectionEditorViewModel.cs:879`; hand-editing it is how a sovereign
  cloud account (`*.blob.core.chinacloudapi.cn`) is reached without a new field.
- **A custom S3 endpoint and a path-style-addressing flag are in scope.** Without them the feature does not
  work against MinIO, Ceph/RGW, Backblaze B2, Cloudflare R2 or Wasabi. One nullable string and one bool,
  defaulting to real AWS. This stays inside "an access key and a secret key": it changes where the key is
  sent, not what it is.
- **One owned value object for both providers**, `ObjectStorageOptions`, columns prefixed `Storage_`:
  `Region`, `ServiceUrl`, `UsePathStyleAddressing`, `Container`, `RootPrefix`, `LocalDownloadPath`. A
  connection is exactly one protocol; two VOs would mean two more `OwnsOne` blocks and two more positional
  `SetOptions` parameters. **Every string nullable and exactly one non-nullable bool** - an owned type whose
  every column is NULL materialises as `null`, and `Navigation(...).IsRequired()` then throws on query. The
  bool keeps it non-null and makes the migration purely additive with a single `defaultValue: false`.
- **Container-name validation is the loose rule** (`[a-z0-9.-]`, 3-63 characters, alphanumeric ends), not
  the S3-Azure intersection. Azure forbids dots and S3 allows them; the Domain must never reject a name the
  user's provider accepts. The provider is authoritative and anything stricter comes back as `InvalidPath`.
- **The identifier is plaintext, the secret is not.** The access key ID / storage account name goes in the
  existing `Username` column; the secret access key / account key goes in the single `CredentialRef` slot
  as new **`CredentialKind.StorageSecretKey = 4`**, store-key suffix `storage-secret-key`. Zero schema
  change and it satisfies ADR-0007. A 40-char S3 secret and an 88-char Azure key both sit far inside
  windows-credman's 2560-byte cap.
- **`AuthMethod` stays `None`** and its combo is hidden for storage protocols. Adding `AccessKey = 7` breaks
  `EnumValueTests.AuthMethodValuesAreStable`, adds a dead throwing arm to
  `SshAuthenticationMaterialProvider`, and - because the combo is `Enum.GetValues<AuthMethod>()` - makes
  "AccessKey" selectable on SSH connections. Reusing `Password` puts the word "Password" in the details
  pane for a thing that is not one.
- **The path model is rooted at the account.** `/` lists buckets or containers, `/mybucket` is its root,
  `/mybucket/logs/2026` is a prefix. When `ObjectStorageOptions.Container` is set, `RootPath` is
  `/{container}`, `ListBuckets` is **never called**, and navigating above the root returns `InvalidPath`.
  When it is empty the pane lists buckets, and a 403 maps to `PermissionDenied` with a message naming the
  remedy: "This key cannot list buckets. Set a bucket name on the connection to browse it directly."
  Both, because account-wide keys are the common AWS case and single-bucket-scoped keys are normal in
  production.
- **`ObjectStoragePath` is a separate type from `SftpPath`.** `SftpPath.Normalize` strips trailing slashes,
  and for object storage a trailing slash is semantically load-bearing (`a/` is a prefix marker, `a` is an
  object). Reuse the algorithm, not the type. Directory-ness is carried by `ObjectEntryKind`, never by a
  trailing slash.
- **Bucket and container creation and deletion are refused** with `NotSupported`, pointing at the provider
  console. Bucket creation carries region, ownership, public-access-block, versioning and lifecycle
  decisions a two-field dialog cannot make safely, and a mis-created public container is a security
  incident, not a UX regression.
- **The SDKs go in `RemoteFlow.Infrastructure` only.**
  `DependencyDirectionTests.ApplicationReferencesOnlyApprovedDependencies` allows Application exactly three
  non-BCL references, and `DomainReferencesOnlyTheBaseClassLibrary` allows Domain none at all.
- **Pin `System.Security.Cryptography.ProtectedData` to 10.0.10 centrally.** It arrives transitively through
  Azure.Core -> MSAL at 4.5.0, whose nuspec carries only a `licenceUrl` and no SPDX expression, and
  `generate-notices.ps1` throws on exactly that shape - which fails the build via RF0002. Transitive
  pinning promotes it to a version with `<license type="expression">MIT</license>`. This is the
  `SQLitePCLRaw.lib.e_sqlite3` precedent already in the file; carry a comment giving the reason.
- **Bump `DbInitializer.CurrentSchemaVersion` to 2 and write it back after `MigrateAsync`.** A 0.2.x binary
  opening a database containing `Protocol = 4` throws in the icon switch on the Connections page;
  `GuardAgainstNewerSchemaAsync` exists to turn that into a clear message instead. The bump is inert
  without the write-back, because `SettingsStore.SeedDefaults` only inserts missing keys.
- **Remote editing over objects is out of scope for v1.** `RemoteEditService` detects conflicts by size,
  mtime and SHA-256; on objects the correct primitive is the ETag, and "open a 400 MB object in your editor
  and we re-upload on save" is a different feature. Say so in the ADR rather than leaving it ambiguous.

## Scope
Domain enums, `IsObjectStorage()`, the `ObjectStorageOptions` value object and its `GetDefaultPort` arms.
The fifth `OwnsOne` block in `ConnectionConfiguration`, an additive migration, and the regenerated model
snapshot. The Application storage contracts under `Abstractions/Storage/`: `IObjectStorageService`,
`IObjectUpload`, `IObjectStorageProvider`, `IObjectStorageClientFactory`, `IObjectStorageSecretProvider`,
`ObjectStoragePath`, `ObjectStorageEndpoint`, `ObjectStoragePaging`. The S3 and Azure adapters and
`ObjectStorageErrorMap` in Infrastructure, registered with `TryAddEnumerable` and selected by protocol the
way `ConfiguredSshTransport` selects an SSH transport. The new credential kind through both duplicated
store-key switches. Validator rules, `ConnectionService.Configure` threading, the additive backup field,
and the connection editor's storage section.

`IObjectUpload` exposes the provider's real limits (`MinimumPartSize`, `MaximumPartSize`,
`MaximumPartCount`) and takes part content as `Func<CancellationToken, ValueTask<Stream>>`, never a bare
`Stream`: issue #96 retries individual parts and a retried part needs a fresh stream. `AbortAsync` is best
effort, never throws, and is permitted to be a no-op - Azure has no abort because uncommitted blocks expire
after seven days and are not billed. Neither adapter may buffer a whole part.

Operation mapping, including the awkward cases:
- **List** one level with `Delimiter = "/"`; common prefixes become directory entries. **Marker suppression
  is mandatory** - drop any object whose key equals the listed prefix, and any zero-byte key ending in `/`.
  Without it every folder created by RemoteFlow, the AWS console or Storage Explorer appears twice, once as
  a folder and once as an empty file.
- **Create folder** PUTs a zero-byte object at `{key}/`, guarded by a `MaxKeys = 1` list that returns
  `AlreadyExists`. Both vendors' own consoles do this; a client-side-only placeholder evaporates on refresh.
- **Recursive delete** pages the recursive listing, then batches S3 `DeleteObjects` at 1000 per request and
  runs Azure deletes at bounded concurrency. Delete the `{prefix}/` marker **last**, so an interrupted
  delete leaves a visible folder rather than an invisible half-deleted one. Non-recursive delete of a
  non-empty prefix returns `NotSupported` with "The folder is not empty. Delete it recursively." - the one
  place the reused error enum is genuinely lossy, so record it in the ADR.
- **Stat** on a container lists with `MaxKeys = 1` rather than calling `HeadBucket`: it proves existence and
  list permission at once, using no permission the caller does not already need. On a key, `HeadObject`,
  and on 404 a `MaxKeys = 1` list of `{key}/` to tell "prefix" from "absent".
- **Always construct clients with an explicit `BasicAWSCredentials` / `StorageSharedKeyCredential`.** A
  parameterless `AmazonS3Client` falls back to AWSSDK.Core's credential chain - `~/.aws/credentials`,
  `AWS_*` environment variables, EC2/ECS metadata endpoints - which silently violates the access-key-only
  scope.
- **Turn the SDKs' own logging and telemetry off explicitly**
  (`AWSConfigs.LoggingConfig.LogResponses = Never`; `BlobClientOptions.Diagnostics` with logging, content
  logging and telemetry all false; no `AzureEventSourceListener`). `RedactingLoggerProvider` cannot redact
  what it never sees, and it cannot see EventSource.

Four traps in existing code will silently ship a broken feature unless each is fixed with a named
regression test:
1. `ConnectionService.cs:316` calls `SetOptions(ssh.Value, SftpOptions.Default(), rdp.Value, ...)` - SFTP
   options are never persisted from the editor. Thread the new VO through `ConnectionInput` -> `Configure`
   -> `SetOptions` or it is reset on every save.
2. `ConnectionEditorViewModel.cs:451` clears the stored credential when `AuthMethod == None`, and storage
   connections use `None`. Guard it with `&& !Protocol.IsObjectStorage()`.
3. `EfBackupImportStore.cs:242` uses a hand-written INSERT with a literal column list. New columns must be
   added there or an import silently drops them to their SQLite DEFAULT.
4. Double-clicking a storage connection throws "Only SSH and SFTP connections can open an SSH terminal
   session" from `SessionManager.cs:42`, because `SshConnectionSessionOpener` only special-cases RDP. Add
   `ConnectionOpenMode.Storage` and a default-mode branch that navigates to the Storage page - which does
   not exist until #97, so land the branch behind the same `IConnectionSessionOpener` seam and have it
   report a clear "not available yet" until then.

Note also that backup enums serialise as camelCase **strings**, not ints
(`ZipBackupArchiveSerializer.cs:12`), so an older RemoteFlow reading an archive containing
`"protocol": "s3"` fails the whole import rather than that one connection. `docs/backup-format.md`'s
ignore-unknown-fields rule covers unknown fields, not unknown enum members. Accept it, document it in that
file, and pin the failure message in a test. Do **not** add an `Unsupported` fallback member: silently
importing a connection whose protocol cannot be opened is worse than refusing the file.

The backup change is one **optional trailing parameter** on `BackupConnection`
(`BackupObjectStorageOptions? ObjectStorage = null`). Optional-with-default is a requirement, not a style
preference: it keeps `Fixtures/backup-v1-golden.zip` importing, and it keeps six existing test files that
construct `BackupConnection` compiling unchanged. `BackupFormat.DomainEntityCoverage` needs no entry -
`ObjectStorageOptions` is a value object, and the reflection test filters on the `Entities` namespace.

## Acceptance criteria
- [ ] An S3 connection and an Azure Blob connection can be created and saved from the editor, with `Host`
      derived and `Port` 443, and both appear in the connections list, the search box and the filter chips
      with a readable label (not `AZUREBLOB`).
- [ ] **Creating, re-saving and duplicating a storage connection preserves its options and its stored secret
      key** - one test per trap, named after the bug, covering `ConnectionService.Configure` wiping options
      and `ConnectionEditorViewModel` clearing the credential under `AuthMethod.None`.
- [ ] Against the in-memory fake, the contracts list one level with folder grouping, stat a container, a
      prefix and an object, create a folder, delete a non-empty prefix recursively, put an object and read
      an exact byte range.
- [ ] A folder created by the adapter is listed **once**, as a folder, not also as a zero-byte file.
- [ ] A `[Theory]` maps every documented `AmazonS3Exception` and `RequestFailedException` case to its
      `SftpError`, including 412 to `PreconditionFailed` and cancellation to `Cancelled`, and an unknown
      error code preserves the provider's message.
- [ ] `IObjectUpload` round-trips a multipart upload against the fake, and `AbortAsync` on an
      Azure-shaped adapter whose abort is a no-op reports success.
- [ ] A new architecture test asserts neither `RemoteFlow.Application` nor `RemoteFlow.UI` references
      `AWSSDK.*` or `Azure.*`, extending the `ApplicationDoesNotReferenceSshImplementations` idiom.
- [ ] `EnumValueTests` pins `ProtocolType` to `[1,2,3,4,5]` and `CredentialKind` to `[0,1,2,3,4]`, and
      `CredentialProviderTests` expects four deleted keys.
- [ ] `PersistenceBehaviorTests` passes with the regenerated `RemoteFlowDbContextModelSnapshot` committed,
      the new owned VO round-trips with all strings null, and `ConnectionQueryServiceTests` still asserts
      the list projection does not widen to `Storage_` columns.
- [ ] A v1 archive with no `objectStorage` field imports with defaults, the committed golden zip still
      imports cleanly and was **not** regenerated, and no plaintext backup entry contains a secret.
- [ ] A database written by this build is refused by a `CurrentSchemaVersion = 1` reader with the
      newer-schema message, and an existing v1 database is stamped to 2 after migration.
- [ ] `dotnet build` and `dotnet test` are green with warnings as errors, and
      `pwsh ./scripts/generate-notices.ps1 -Verify` is clean with both generated files committed.
- [ ] `docs/adr/0019-object-storage-provider-abstraction.md` records every decision above, including the
      reused-error-type debt, the MSAL footprint arriving unused through Azure.Core, and what is deferred.

## Out of scope
The transfer engine and chunked uploads (#96). The Storage page and any browsing UI (#97). Remote editing
over objects. Server-side copy and rename. Object versioning, SSE-C and SSE-KMS keys, lifecycle rules.
Bucket and container creation. Any change to `TransferEngine.cs`, `TransferContracts.cs`,
`TransfersPageViewModel.cs`, `SftpWorkspaceViewModel.cs` or the SFTP adapters.
'@ },

@{ Number=61; Milestone='9 - Cloud Object Storage'
   Title='Chunked transfers for objects in the gigabytes'
   Labels=@('model:opus-5','effort:xhigh','area:core','area:storage','type:feature','risk:load-bearing')
   Body=@'
```yaml
model: claude-opus-5
effort: xhigh
risk: load-bearing
depends_on: [95]        # 95 S3 and Azure Blob connections, credentials and the provider adapters
blocks: [97]            # 97 The dual-pane Storage page
read_first:
  - src/RemoteFlow.Application/Services/TransferEngine.cs
  - src/RemoteFlow.Application/Abstractions/Sftp/TransferContracts.cs
  - src/RemoteFlow.UI/ViewModels/Transfers/TransfersPageViewModel.cs
  - src/RemoteFlow.Application/Abstractions/Storage/ObjectStorageContracts.cs
  - docs/adr/0012-transfer-engine.md
  - tests/RemoteFlow.Application.Tests/TransferEngineTests.cs
touches:
  - src/RemoteFlow.Application/Abstractions/Storage/ObjectTransferContracts.cs
  - src/RemoteFlow.Application/Abstractions/SettingKeys.cs
  - src/RemoteFlow.Application/Services/ObjectStorageTransferEngine.cs
  - src/RemoteFlow.Application/Services/ObjectPartPlanner.cs
  - src/RemoteFlow.Application/Services/TransferRateMeter.cs
  - src/RemoteFlow.Application/Services/BoundedFileSegmentStream.cs
  - tests/RemoteFlow.TestSupport/**
  - tests/RemoteFlow.Application.Tests/**
  - docs/adr/0020-chunked-object-storage-transfers.md
verify: dotnet test tests/RemoteFlow.Application.Tests
```

## Goal
Move multi-gigabyte objects at the throughput parallel parts give, with progress a user can trust and a
cancel that does not leave billable orphans behind. A 4 GB download that fails at 3.9 GB must not have cost
the user a single-stream restart from byte zero.

## Decisions already made - do not re-litigate
- **A new `ObjectStorageTransferEngine`, not an extension of `TransferEngine`.** Those 640 lines are SFTP in
  their bones: `ISftpService` in the constructor, `SftpPath` on every path, `SftpPublisher`'s rename-aside
  dance (which exists only because SFTP v3 has no overwriting rename), parent-directory walks, a remote
  `.part` sidecar, and one semaphore permit held for an entire recursive tree. A second constructor over a
  different service would be two disjoint bodies sharing a class name.
- **No shared base class.** The only genuinely common body is `Report` and its ETA arithmetic, which is
  being replaced by a windowed meter anyway. A base class here would be inheritance for twelve lines and
  would put ADR-0012's documented SFTP behaviour at risk of collateral change.
- **Orchestrate chunking in Application over a part-level port. Do not use `TransferUtility` or
  `BlobClient.UploadAsync` with `StorageTransferOptions`.** Application cannot reference the SDKs, so with
  the vendor helpers the part-size policy, the retry and backoff, the monotonic-progress reconciliation and
  the cancel-aborts guarantee would all live in Infrastructure, where this repository has no CI-visible way
  to test cloud code: `RemoteFlow.Ssh.IntegrationTests` is opt-in and does not run in CI at all, and a
  MinIO or Azurite suite would be equally invisible. CONTRIBUTING's rule - changes to Domain or Application
  come with tests - is satisfiable one way and evaded the other. The consciously accepted cost is
  vendor-maintained correctness for multipart edge cases; the mitigations are below.
- **The provider difference is smaller than it looks.** S3 has a server-issued upload id and an explicit
  abort; Azure has client-generated equal-length base64 block ids, no server-side session and no abort.
  Both vanish behind an opaque handle, an opaque per-part tag, and an `AbortAsync` permitted to be a no-op -
  the same trick this repository already runs with one `ISftpService` over two very different transports.
- **Below the threshold, let the SDK own it.** At or under `SingleShotThreshold` (16 MiB) use the plain
  single-request path. Hand-rolling buys nothing there, and a multipart upload of a 200 KB file costs three
  round trips instead of one.
- **Keep each SDK's transport retry, tuned to one attempt** (`MaxErrorRetry = 1`, `MaxRetries = 1`). Leaving
  the defaults gives up to sixteen HTTP attempts and minutes of invisible delay for one part while the user
  watches a stalled bar. Setting zero is worse in a different way: the SDK is the only layer that can see a
  `Retry-After` header or S3's `SlowDown`, and it can retry within the request without re-streaming the
  part. So the SDK does one fast in-request retry and the engine does four slow re-streamed attempts -
  bounded at eight, and explainable.
- **The part-size ladder is derived arithmetic, not a setting.**
  `partSize = Clamp(NextPowerOfTwo(Ceil(total / 8000)), 8 MiB, 1 GiB)`. The 8 MiB floor (not S3's 5 MiB)
  satisfies both providers with headroom against a slightly misreported size; the 8000-part budget against
  S3's 10,000 cap absorbs a wrong total without an unrecoverable failure at part 9,999; the 1 GiB ceiling
  bounds the cost of retrying one part. A `StoragePartSizeMiB` setting is a footgun disguised as a
  preference - a user picking 5 MiB for a 500 GB object needs 95,368 parts and hits the cap.
- **`Func<CancellationToken, ValueTask<Stream>> ContentFactory`, never a bare `Stream`.** A retried part
  needs a fresh stream; handing the same one twice is the classic hand-rolled-multipart bug, because the
  SDK has already consumed or seeked it. The factory makes the invariant structural rather than a comment.
- **Nothing buffers a part.** Each in-flight part is a `BoundedFileSegmentStream` - a read-only seekable
  window over its own file handle - so peak managed memory is `MaxPartsInFlight` times `CopyBufferSize`,
  four times 1 MiB, whether the object is 4 GB or 500 GB. The "8 in flight times 64 MiB = 512 MiB" figure
  only materialises if you buffer, and this design does not.
- **Downloads preallocate one `.part` and write with `RandomAccess.WriteAsync` at absolute offset.**
  `RandomAccess.SetLength` up front fails fast on `ENOSPC`, so the user learns there is no room for a 500 GB
  object before transferring 499 GB of it. Per-range temp files then concatenated would double the disk
  write volume, need twice the free space, and add a long non-cancellable phase with the bar pinned at 100%.
- **Progress is monotonic by an explicit clamp**: report `Math.Max(_lastReported, sum(perRangeBytes))`. The
  obvious alternative - tracking the highest contiguous offset - leaves the bar at 0% for the whole
  transfer whenever range 0 happens to finish last. On the upload side `CountingReadStream` reports
  `Math.Max(_high, Position)`, because S3 reads the seekable stream once for a checksum and rewinds, and
  cumulative counting would double-count.
- **Abort on every exit that is not "Complete returned success", always with `CancellationToken.None`.**
  Passing the cancelled token means the abort is itself cancelled and the parts survive - that is *the* bug
  in cancel-aborts-multipart code, and `TransferEngine.BestEffortDeleteRemoteAsync` already does it right
  for SFTP. That includes the always-forgotten case: `CompleteMultipartUpload` itself failing, which leaves
  the most parts behind. Bound the abort by a timeout so a dead network cannot hang the cancel, and if it
  fails still report `Cancelled` with a message saying incomplete parts may remain and may be billed -
  swallowing that is dishonest about money.
- **The honest limit belongs in the docs, not in a claim.** A process kill or power loss skips the abort
  entirely, so the only durable guarantee against paid-for orphans is a bucket lifecycle rule
  (`AbortIncompleteMultipartUpload`, seven days). Recommend it; do not pretend the client can guarantee it.
- **The rate becomes windowed.** `transferred / elapsed` is not merely imprecise but actively misleading
  over hours: a 500 GB transfer running at 100 MB/s then 5 MB/s reports about 52 MB/s and an ETA hours
  short, exactly when the user needs to decide whether to leave it running.
- **Report at 250 ms.** `CoalescingProgress` keeps only the latest value per 100 ms tick, so reporting
  faster is pure discard - the existing per-64-KiB reporting throws away roughly 1,590 of 1,600 reports per
  second. 250 ms means nearly every report is applied, which keeps `ProgressUpdateCount` a meaningful
  assertion rather than an accidental one.
- **The queue is reused verbatim.** `TransferQueueRequest.Operation` is
  `Func<IProgress<TransferProgress>, CancellationToken, Task<TransferResult>>`, so this engine drops into
  `TransfersPageViewModel` with no change to those 523 lines, and all of `TransferContracts.cs` is reused
  as-is. `TransferItemResult.Failure` being `SftpFailure?` is an accepted naming wart: map object-storage
  failures into it at the engine boundary rather than renaming a type used at every SFTP call site.
- **Resume across restart stays deferred.** ADR-0012's reasoning holds: without a server capability and an
  identity check, a resumed offset can splice bytes from two different object versions.
- **`TimeProvider` for every delay.** `Microsoft.Extensions.TimeProvider.Testing` is already on every test
  project, so backoff is tested by advancing a `FakeTimeProvider`, not by sleeping. Jitter is a `Func` on
  the options record so a test can pin it and assert exact delays.
- **Do not extend `TransferOptions`** - `BufferSize` and `MaxConcurrentTransfers` mean different things in
  a chunked world. A sibling `ObjectTransferOptions` record, validated in its constructor the way
  `TransferEngine` validates its own.
- **`MaxPartsInFlight` defaults to 4, not 8.** `TransfersPageViewModel` already permits three concurrent
  transfers, so 3 x 4 = 12 concurrent HTTP requests - enough to saturate a link without starving a home
  uplink or tripping connection limits.

## Scope
`ObjectTransferContracts.cs` (the options record and the part-level request types), `ObjectPartPlanner`
(pure arithmetic, and the highest-value unit tests in this milestone), `TransferRateMeter` (a five-second
ring over `(timestamp, cumulativeBytes)` samples), `BoundedFileSegmentStream` and `CountingReadStream`, and
`ObjectStorageTransferEngine` itself: plan, upload in parallel parts with per-part retry and guaranteed
abort, download in parallel ranges onto a preallocated `.part`, and recursive folder transfers that page the
listing rather than materialising it.

Three `SettingKeys` entries, all of which must also be appended to `SettingKeys.All` or they get no seeded
default: `StorageMaxPartsInFlight`, `StorageConflictDefault` (a new enum, default `Overwrite`) and
`StorageDownloadDir`, mirroring the existing `SftpDownloadDir`.

`FakeObjectStorage` and `FakePartOperations` in TestSupport, adapted from `FakeSftpService` at
`FakeSshTransport.cs:202`, whose `ConcurrentDictionary<string, byte[]>` with prefix-based one-level listing
is already an object store. What the fake must add: a container dimension, real pagination with an opaque
token, ETags that change on write and that honour an if-match precondition by returning
`PreconditionFailed`, provider part-size limits so the planner is tested against a real floor, and a
scripted-failure surface (`FailPart(n, times, transient)`, `StallPart`, `FailComplete`, `FailAbort`)
recording each abort together with whether the token it was handed was cancelled.

Leave `TransfersPageViewModel`'s hard-coded `maxConcurrentTransfers = 3` alone: it is a constructor default
registered parameterless, and `ISettingsStore.Get` is async so a DI factory cannot read it without
blocking. Making it configurable is a real change to the composition root and belongs in its own issue.

Do not add queue-wide aggregate byte totals: that means editing a shared singleton with five collections
under a lock for a cosmetic gain. Record it as a known limitation.

## Acceptance criteria
- [ ] **Recorded part boundaries are contiguous with no gaps or overlaps, and the reassembled bytes
      checksum-equal the source** - asserted across several object sizes, including one that lands exactly
      on a part boundary and one that does not.
- [ ] The planner's invariants hold as a theory over many sizes: the part lengths sum to the total, every
      part but the last is at least the provider minimum, the count is within the provider cap, no part
      exceeds the provider maximum, and part numbers are contiguous from 1. A 4 GiB object is 512 parts of
      8 MiB; a 500 GB object is 7,451 parts of 64 MiB; an object above the provider's object cap is
      rejected before any network call.
- [ ] `Complete` receives parts ordered 1..N even when they finish out of order, driven by stalling a part.
- [ ] A transient failure on part 3 uploads it exactly three times and the transfer succeeds, and with
      pinned jitter the `FakeTimeProvider` advances match the expected backoff exactly. A non-transient
      failure retries zero times.
- [ ] **Cancelling mid-transfer records exactly one abort, and the token that abort was called with was not
      cancelled.** `Complete` is never called.
- [ ] `CompleteMultipartUpload` failing still records exactly one abort. A failing abort still reports
      `Cancelled` or `Failed` with no exception escaping, and the failure message says incomplete parts may
      remain.
- [ ] An adapter whose abort is a no-op returning success produces identical engine behaviour, proving the
      opaque-handle abstraction holds.
- [ ] The `BytesTransferred` sequence captured by an inline progress sink is **non-decreasing**, including
      across a forced range retry, and ends at `TotalBytes` with `IsCompleted` true.
- [ ] The download `.part` is preallocated to the full length before the first byte lands; out-of-order
      range completion still checksums equal; cancelling leaves neither the final file nor the `.part`.
- [ ] An object at or below the single-shot threshold makes one put call and zero multipart calls; an
      object whose length is unknown or zero falls back to the single-stream read path with zero ranged
      requests.
- [ ] Concurrent part streams and concurrent ranges never exceed `MaxPartsInFlight`.
- [ ] `TransferRateMeter` converges on a halved rate within its window while a cumulative average provably
      does not - assert the difference, so the test documents why the change was made. Zero elapsed does
      not divide by zero; a zero rate gives a null ETA; completion gives `TimeSpan.Zero`.
- [ ] A second stream from the same `ContentFactory` starts fresh, and `CountingReadStream` does not
      double-count a seek-and-rewind.
- [ ] All eight `TransferEngineTests` and all six `TransferManagerTests` still pass, untouched.
- [ ] `docs/adr/0020-chunked-object-storage-transfers.md` records the decisions above, and ADR-0012 gains a
      scope paragraph saying it governs SFTP transfers specifically. ADR-0012 is **amended, not superseded**.

## Out of scope
Transfer resume across an application restart - still deferred by ADR-0012. Retrofitting `TransferEngine`
onto `TransferRateMeter`. Bandwidth throttling. Queue-wide aggregate totals. Making the queue's concurrency
limit configurable. Any edit to `TransferEngine.cs`, `TransferContracts.cs`, `TransfersPageViewModel.cs` or
`SftpWorkspaceViewModel.cs`. All user-facing UI, including the conflict dialog (#97).
'@ },

@{ Number=62; Milestone='9 - Cloud Object Storage'
   Title='The dual-pane Storage page'
   Labels=@('model:opus-5','effort:high','area:ui','area:storage','type:feature','risk:contained')
   Body=@'
```yaml
model: claude-opus-5
effort: high
risk: contained
depends_on: [95, 96]    # 95 S3 and Azure Blob connections...; 96 Chunked transfers for objects in the gigabytes
blocks: [98]            # 98 Cut 0.3.0 for Windows and Linux, with Linux built by CI
read_first:
  - src/RemoteFlow.UI/ViewModels/Sftp/SftpWorkspaceViewModel.cs
  - src/RemoteFlow.UI/Views/Sftp/SftpWorkspace.axaml
  - src/RemoteFlow.UI/ViewModels/Transfers/TransfersPageViewModel.cs
  - src/RemoteFlow.UI/Services/RemoteEditConflictResolver.cs
  - src/RemoteFlow.UI/DependencyInjection.cs
  - src/RemoteFlow.UI/Views/MainWindow.axaml
  - docs/accessibility.md
  - docs/adr/0009-keybinding-policy.md
  - docs/adr/0013-sftp-workspace.md
touches:
  - src/RemoteFlow.Application/Abstractions/Storage/IFileBrowserSource.cs
  - src/RemoteFlow.Application/Services/LocalFileBrowserSource.cs
  - src/RemoteFlow.Application/Services/ObjectStorageFileBrowserSource.cs
  - src/RemoteFlow.UI/ViewModels/Storage/**
  - src/RemoteFlow.UI/Views/Storage/**
  - src/RemoteFlow.UI/Services/StorageWorkspaceSession.cs
  - src/RemoteFlow.UI/Services/TransferConflictResolver.cs
  - src/RemoteFlow.UI/DependencyInjection.cs
  - src/RemoteFlow.UI/Views/MainWindow.axaml
  - src/RemoteFlow.UI/Styles/Icons.axaml
  - docs/object-storage.md
  - docs/keybindings.md
  - docs/accessibility.md
  - docs/adr/0021-dual-pane-storage-workspace.md
  - tests/**
verify: dotnet test tests/RemoteFlow.UI.Tests; dotnet test tests/RemoteFlow.Application.Tests
```

## Goal
A Storage page with the local filesystem on the left, the bucket or container on the right, and the transfer
queue along the bottom - so moving a 4 GB object is drag, drop, watch, rather than a file picker and a trip
to the Transfers page.

## Decisions already made - do not re-litigate
- **One `FileBrowserPane` control, instantiated twice** over an `IFileBrowserSource`, bound as
  `{Binding Local}` and `{Binding Remote}`. A shared base class that `SftpWorkspaceViewModel` also derives
  from would mean touching the SFTP viewmodel, which this milestone does not do. Copy-paste of 1,271 lines
  is not an option.
- **The local pane goes through `LocalFileBrowserSource` in Application** (plain `System.IO`, which is BCL
  and so architecture-test clean), not `System.IO` in the viewmodel. The behaviour is genuinely non-trivial
  - a mid-enumeration `UnauthorizedAccessException` on something like `C:\System Volume Information` must
  yield a partial page rather than blanking the pane, plus hidden-attribute filtering, drive roots against
  `/`, and `GetParent` at a root - and Application is where it gets plain `[Fact]` coverage instead of an
  Avalonia harness. `System.IO` in the pane would also make the pane non-generic, and you would end up with
  two pane classes again.
- **`IFileBrowserSource` owns path handling** (`Combine`, `GetParent`, `GetName`, `IsValidPath`). That is
  what lets one pane serve `C:\Users\...` and `photos/2024/`, and it is exactly why
  `SftpWorkspaceViewModel`'s `path[0] == '/'` validation must not be carried over - it rejects every
  Windows local path.
- **Carried over from the SFTP pane** (re-implemented generically, not extracted): breadcrumbs, back and
  forward history with their can-execute flags, the sort (directory-first, stable, tie-broken on original
  index), type-ahead selection by prefix, the 250 ms busy-indicator anti-flicker delay, the inline error,
  feedback and drop-target messages, and UI-thread marshalling through `IUiDispatcher`.
- **Not carried over**: the Permissions and Owner columns and their mode formatting; the shell-literal
  "Copy path" (`ToShellLiteral` is meaningless for a key - the remote pane gets "Copy key" and "Copy URI");
  the dot-file hidden filter (locally the correct test is `FileAttributes.Hidden`, and the object pane hides
  the toggle entirely); and inline rename on the remote pane, because S3 has no rename - it is copy plus
  delete - so `SupportsRename` is false there and the source does not fake it.
- **The Storage page embeds the existing `TransfersPageViewModel` singleton, unfiltered**, under a header
  that says "All transfers". A second queue would mean two independent three-slot gates, six concurrent
  transfers with neither aware of the other, and a duplicate of 523 tested lines. Filtering to this session
  would require a per-session tag on `TransferQueueRequest` - editing the provider-blind queue, which is
  precisely what reuse avoids - plus a filtered collection view Avalonia does not hand you for
  `ObservableCollection`. The sidebar status bar already shows this singleton on every page, so users
  already experience it as *the* queue. Accepted consequence: clearing completed from either surface clears
  both.
- **A production `ITransferConflictResolver` ships here for the first time**, split the way
  `RemoteEditConflictResolver` already splits: an Avalonia-free resolver holding policy, so it is plain
  `[Fact]` testable, and a dialog service owning window construction and the UI thread. Copy that split
  exactly.
- **"Apply to all" is a scope, not a decision.** `TransferConflictDecision` stays `{ Skip, Overwrite,
  Cancel }`. One `BatchTransferConflictResolver` instance per user gesture holds the count and the sticky
  answer, so the object's lifetime *is* the batch. `AsyncLocal` does not work - the gesture returns long
  before the queued transfers run, because `QueueAsync` fires and forgets - and a `BatchId` on
  `TransferConflict` would change an Application contract to serve a UI affordance.
- **The default conflict decision is `Overwrite`**, because a put is atomic and idempotent in both
  providers, there is no partial-object state to protect the way SFTP's rename-aside dance protects one, and
  a user who dropped a file onto a prefix that already holds that key overwhelmingly means replace. It is a
  *setting* rather than hard-coded precisely because an unversioned bucket makes overwrite unrecoverable.
- **A null resolver still yields `Conflict`.** Fail closed, exactly as `TransferEngine` does; the Storage
  viewmodel always supplies one. Do not make the engine default to overwrite when nobody asked.
- **Do not wire a resolver into `SftpWorkspaceViewModel.cs:267`.** It still constructs its engine with no
  resolver, SFTP behaviour is unchanged, and
  `TransferEngineTests.ExistingTargetRequiresResolverAndNeverClobbersByDefault` must stay green. Wiring it
  there is a separate issue with its own UX decision, and if it is done here someone will "fix" that test.
- **Pagination by continuation token, not `IAsyncEnumerable`, at the port.** The page boundary is exactly
  what the UI must expose as "Load more" and be able to stop at, and it maps one-to-one onto both
  providers' listing calls. An `IAsyncEnumerable` wrapper sits on top for the recursive walks where paging
  is not user-visible - delete plans and folder transfers.
- **A hard cap of 10 pages / 10,000 rows per prefix.** At the cap the Load-more button is replaced by a
  non-actionable row: "10,000 of many shown. Narrow the prefix, or use the path box to go deeper - this
  view does not load an entire bucket." Handing a `ListBox` a materialised list over a 500,000-key prefix is
  the one thing this design must not do.
- **Sort the plain list before it reaches the observable collection.** Today the SFTP pane clears and
  re-adds `Items` on navigate and again on sort; for 100,000 entries that is about 200,000 change
  notifications on the UI thread, which `VirtualizingStackPanel` does not fix. Do not introduce a bulk
  collection type until profiling asks for one.
- **The filter box is server-side prefix narrowing, labelled "Starts with", not "Search".** Both providers
  support prefix and neither supports substring; offering a search the provider cannot do is worse than not
  offering one. Re-list with `prefix + filterText` - one request instead of a hundred.
- **Never fake a total.** S3 cannot cheaply count a prefix, so show "10,000 shown, more available", never
  "100,000 items". Sorting a truncated listing sorts only what is loaded and the sort-header tooltip says
  so - this is the thing most dual-pane cloud browsers get silently wrong. A folder transfer expands lazily
  and shows a counted, cancellable confirmation before it starts.
- **The local pane uses the identical paging path** with a synthetic index token and the same cap, so a
  `node_modules` with 200,000 files behaves like a 200,000-key prefix and the pane has zero source-specific
  branches.
- **Do not bind `Tab`.** `docs/accessibility.md` gives it to "move between controls", and hijacking it
  creates exactly the keyboard trap `F6` exists to escape. Because the two panes are peer controls in
  declaration order, `Tab` already walks local to remote for free. `Ctrl+Shift+Left` and
  `Ctrl+Shift+Right` are the explicit, discoverable pane jump. `F6` is not reused - ADR-0009 makes it mean
  "escape the keyboard trap" application-wide - and neither are `Ctrl+Tab` or `Alt+1`/`Alt+2`, which the
  terminal claims. ADR-0009's rules are scoped to terminal focus and `docs/keybindings.md`'s function-key
  rows describe keys sent to the PTY, so `F5`, `F7` and `F2` are free.
- **Transfer buttons live on each pane's toolbar** - "Upload" on the local pane, "Download" on the remote
  one - not in a centre column of arrows, which steals width at every window size and lands awkwardly in
  tab order between two lists.
- **The transfer row is a fixed pixel height, not `Auto`.** A `GridSplitter` needs a pixel or star
  neighbour or it is inert.

## Scope
`IFileBrowserSource` with its entry and page records, `LocalFileBrowserSource` and
`ObjectStorageFileBrowserSource` in Application. `FileBrowserPaneViewModel`, `FileBrowserItemViewModel` and
the `FileBrowserPane` user control whose code-behind holds the type-ahead, rename, context-menu and
drag-and-drop handlers **once**, in the `async void` plus `DataContext is ...` pattern already used in
`SftpWorkspace.axaml.cs`. `StoragePageViewModel`, `StorageWorkspace.axaml` and `TransferQueuePane.axaml`.
`StorageWorkspaceSession` and its factory, mirroring `SftpWorkspaceSession` but over
`IObjectStorageClientFactory` rather than an SSH connection. `TransferConflictResolver`, its dialog
viewmodel and its dialog. The DI registration, the nav entry after `sftp`, the `MainWindow.axaml`
`DataTemplate`, and the `Icon.Storage` geometry. The default-mode branch in `SshConnectionSessionOpener`
now navigates to the Storage page for real.

Layout:

```
Grid RowDefinitions="Auto,*,6,180,Auto"
  0  [connection v] [Connect] [refresh]                          s3://media-prod
  1  Grid ColumnDefinitions="*,6,*"
       FileBrowserPane {Binding Local}    ||  FileBrowserPane {Binding Remote}
       [<][>][^][refresh] path  [hidden]  ||  [<][>][^][refresh] key prefix
       / home / andreas / photos          ||  media-prod / 2024 / raw
                  [New folder][Upload >]  ||       [New folder][< Download]
       Name         | Size  | Modified    ||  Name      | Size   | Modified
       > 2023/      |   -   | 12 Jan      ||  > 2024/   |   -    | 3 Mar
         DSC_0001   | 41 MB | 3 Mar       ||    a.mov   | 4.2 GB | 3 Mar
                                          ||  -- 10,000 of many shown --
                                          ||     [Load more]  [Starts with: ]
  2  ==== GridSplitter (rows) ====
  3  All transfers - 2 active, 5 queued                     [Clear completed]
       a.mov  Upload  [#######...]  2.9/4.2 GB  38 MB/s  0:00:34    [Cancel]
  4  status bar
```

Keys: `F5` refresh, `F7` new folder, `Delete` delete (confirmation-gated through
`IConfirmationDialogService`), `Enter` to descend or to transfer to the other pane, `Backspace` and
`Alt+Left` up and back, `Alt+Right` forward, `F2` rename on the local pane only, `Ctrl+Shift+Left` and
`Ctrl+Shift+Right` to focus a pane, `Ctrl+L` for the path box, plus type-ahead.

**The accessible-name trap, which no audit catches:** one pane control used twice makes both Refresh
buttons announce the same name. `AccessibleNameAuditTests` passes and a screen-reader user is lost. Bind
every actionable control's `AutomationProperties.Name` to a pane-scoped string derived from one `PaneName`
property, so the two read "Refresh the local folder" and "Refresh the remote prefix". The audit accepts a
binding expression. Both list boxes and both grid splitters get names too, the splitters with help text
because a keyboard user can move them with arrows once focused. Use only existing design tokens - no
literal colours; if the error banner needs a subtle danger fill, add a token and its contrast-test entry
rather than copying the hard-coded value in `SftpWorkspace.axaml:172`.

Docs: a hand-written "Storage page" section in `docs/keybindings.md` following the existing hand-written
"Embedded RDP on Windows" precedent - these keys do **not** go into `KeymapService.Bindings`, which is the
terminal keymap - a Storage row in `docs/accessibility.md` and its manual-pass list, and a new
`docs/object-storage.md` covering credentials, the recommended bucket lifecycle rule for incomplete
multipart uploads, the 10,000-row cap and why the filter is prefix-only. Add both to the README docs index.

## Acceptance criteria
- [ ] Both panes are the same viewmodel class over different sources, and navigating one leaves the other
      untouched.
- [ ] **`StoragePageViewModel.Transfers` is reference-equal to the injected `TransfersPageViewModel`
      singleton** - this test pins the no-second-queue decision so nobody quietly adds one.
- [ ] `Local.RefreshLabel != Remote.RefreshLabel`, pinning the duplicate-accessible-name fix.
- [ ] Upload, download, create folder and delete all work end to end against the fake, with delete
      confirmation-gated and a folder transfer showing a counted, cancellable confirmation first.
- [ ] A truncated listing shows the cap message instead of Load-more, and the sort-header tooltip changes to
      say it sorts only the rows loaded so far.
- [ ] Typing in the filter box re-lists server-side with the narrowed prefix rather than filtering the
      loaded rows.
- [ ] A five-item batch where the user answers once and ticks apply-to-all invokes the dialog service
      **exactly once**, and apply-to-all is not offered for a single-item batch. A null resolver still
      yields `Conflict`.
- [ ] `LocalFileBrowserSource` survives a mid-enumeration `UnauthorizedAccessException` with a partial page
      and a warning, pages a large directory against the cap, filters hidden entries by attribute, and
      returns sensible roots and parents on both Windows and Unix.
- [ ] `ObjectStorageFileBrowserSource` groups common prefixes into folder rows, does not list a zero-byte
      folder marker twice, round-trips a continuation token across pages, and crosses a page boundary when
      expanding for a delete plan.
- [ ] **A new DI composition test builds the full container and resolves every
      `NavigationPageRegistration.Factory`.** Today `ProjectSmokeTests` only asserts an assembly name, so a
      missing registration is a runtime crash on first navigation.
- [ ] `Tab` from the local list reaches the remote list, the page's first tab stop is the connection
      picker, and `F5`, `F7` and `Delete` reach the focused pane.
- [ ] `AccessibleNameAuditTests`, `VisualStyleTests.EveryIconKeyNamedInCodeResolves` and
      `DarkPaletteContrastTests` all pass, and double-clicking a storage connection in the explorer opens
      this page.
- [ ] All `SftpWorkspaceTests`, `TransferEngineTests` and `TransferManagerTests` still pass, untouched.
- [ ] `docs/adr/0021-dual-pane-storage-workspace.md` records the decisions above; ADR-0013 is **amended,
      not superseded** - its prediction that dual-pane could arrive without changing the SFTP or transfer
      contracts held, and the SFTP workspace stays single-pane and still governed by it.

## Out of scope
Any edit to `SftpWorkspaceViewModel.cs` or `SftpWorkspace.axaml`. Wiring a conflict resolver into the SFTP
transfer engine. Fixing the SFTP drag-out staging leak - `SftpWorkspace.axaml.cs:146` stages into a temp
directory that is never swept; note it as a follow-up and do **not** replicate it, because a local pane
means dragging between panes rather than out to the operating system, so the Storage page needs no staging
directory at all. Remote editing of objects. A "this session only" filter on the transfer queue. Tree-view
navigation in either pane. The release itself (#98).
'@ },

@{ Number=63; Milestone='9 - Cloud Object Storage'
   Title='Cut 0.3.0 for Windows and Linux, with Linux built by CI'
   Labels=@('model:sonnet-5','effort:medium','area:build','type:infra','risk:load-bearing')
   Body=@'
```yaml
model: claude-sonnet-5
effort: medium
risk: load-bearing
depends_on: [95, 96, 97]  # the three feature issues of this milestone
blocks: []
read_first:
  - .github/workflows/release.yml
  - scripts/publish-linux.sh
  - scripts/publish-windows.ps1
  - docs/releasing.md
  - docs/packaging-linux.md
  - docs/packaging-windows.md
  - CHANGELOG.md
  - src/RemoteFlow.Application/Services/Sha256Checksums.cs
touches:
  - .github/workflows/release.yml
  - docs/releasing.md
  - docs/packaging-linux.md
  - CHANGELOG.md
  - README.md
verify: push a tag to a fork or use workflow_dispatch and confirm all four build legs pass and the draft carries eight assets plus a checksums.txt that sha256sum --check --strict accepts
```

## Goal
One tag push produces the whole release. Today `release.yml` has no Linux job, so 0.2.6's `.deb` and tarball
assets were built on a maintainer's machine and attached to the draft by hand, and `checksums.txt` had to be
regenerated over all eight files and re-uploaded. This issue closes that gap and cuts 0.3.0.

## Decisions already made - do not re-litigate
- **Add Linux legs to `release.yml` rather than documenting the manual dance.** The manual step is the
  reason `docs/releasing.md` still describes a Windows-only four-asset release, and a hand-regenerated
  `checksums.txt` is a hand-regenerated parsed contract.
- **0.3.0, not 0.2.7.** A new connection category, two new protocols, a new page and a new transfer engine
  is feature-level work, consistent with 0.2.0 having landed embedded RDP.
- **The version lives only in the git tag.** MinVer reads it, so "bump the version" means renaming the
  changelog section and creating the tag. There is no file to edit.
- **`checksums.txt`'s line format is a parsed contract**, read by `Sha256Checksums` and
  `GitHubUpdateChecker` for self-update, as is the installer asset name
  `RemoteFlow-<version>-<rid>-setup.exe`. Neither may change.
- **Automation produces a draft and never publishes.** The existing job already refuses to overwrite a
  published release; keep that.
- **Linux artefacts are unsigned and there is no Linux smoke-test script.** Do not invent one here - assert
  that the script's reported version matches the tag, and leave a real install pass to the manual list.

## Scope
Add a `build-linux` job to `.github/workflows/release.yml` with an `ubuntu-latest` leg for `linux-x64` and
an arm64 runner leg for `linux-arm64`, each running `./scripts/publish-linux.sh` for its runtime and
uploading that architecture's `.deb` and `.tar.gz` with `if-no-files-found: error`. Read the tag through an
`env:` binding rather than `${{ }}` interpolation, matching the existing "Read the version from the tag"
step and its reason. Then widen the draft job: make it depend on the Linux legs, extend "Check the full set
arrived" from four assets to eight, extend "Write checksums.txt" to cover `*.deb` and `*.tar.gz` as well as
`*.zip` and `*.exe`, and keep "Verify checksums.txt" asserting both the
`^[0-9a-f]{64}  [^/\\]+$` shape and a parseable line for each per-runtime `-setup.exe`.

Then cut the release. Rename `## [Unreleased]` to `## [0.3.0]` with its date and open a fresh empty one,
writing the entries in the house voice - a bolded lead sentence, then the explanation the person upgrading
needs. The entries must cover: S3 and Azure Blob connections and what auth they take; the dual-pane Storage
page; chunked transfers and what they mean for a multi-gigabyte object; that Linux assets are now built by
CI rather than by hand; that the Azure SDK brings a transitively-unused MSAL stack of roughly ten packages
into the notices; and that **once this build has opened your database, 0.2.x will refuse to open it** with a
clear message rather than crashing, because the schema version moved to 2.

Update the README feature list, and its **security posture** section: the existing wording - "RemoteFlow
opens network connections to the hosts *you* configure" - already covers S3 and Azure honestly, but the "No
telemetry, no cloud, no accounts" heading now needs a sentence distinguishing *your* cloud storage, which
you configured and whose keys stay on your machine, from RemoteFlow having a cloud of its own. Do not weaken
the claim and do not overstate it. Rewrite `docs/releasing.md` to describe the eight-asset flow, and prune
the now-stale manual Linux instructions from `docs/packaging-linux.md` while keeping its local-build
documentation.

Commit as `docs: cut 0.3.0 in the changelog`, land it, then tag `v0.3.0` and push. Watch all four build
legs. Work through `docs/packaging-windows.md#verifying-a-release-candidate` - all six steps, release
blocking, including extracting the portable zip on a machine with no .NET runtime and an in-place upgrade
through About - plus a `.deb` install, launch and uninstall pass, and one end-to-end Storage-page transfer
against a real bucket on each platform. Edit the generated notes into something worth reading, then press
Publish by hand.

## Acceptance criteria
- [ ] A tag push runs four build legs - `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64` - and every one
      uploads its artefacts with `if-no-files-found: error`.
- [ ] **The draft carries eight assets** - two Windows installers, two Windows zips, two `.deb`s and two
      `.tar.gz`es - and a `checksums.txt` covering all eight that `sha256sum --check --strict` accepts.
- [ ] The checksum verification step still asserts the line shape and a parseable installer line per
      Windows runtime, and the installer asset names are unchanged.
- [ ] The Linux legs fail loudly if the version the publish script reports does not match the tag.
- [ ] Re-running the workflow on the same tag repairs the existing draft, and it still refuses to touch an
      already-published release.
- [ ] `CHANGELOG.md` has `## [0.3.0]` with its date, a fresh empty `## [Unreleased]`, Keep-a-Changelog
      headings only, and an entry for the database-downgrade consequence.
- [ ] The README feature list and security-posture section describe cloud storage accurately, and
      `docs/releasing.md` describes the eight-asset flow with no Windows-only leftovers.
- [ ] The Windows release-candidate checklist and a Linux `.deb` install pass are both recorded in the pull
      request.
- [ ] `dotnet build`, `dotnet test`, the Linux `CrossPlatform` leg and
      `pwsh ./scripts/generate-notices.ps1 -Verify` are all green on the tagged commit.
- [ ] The release is published by hand, not by automation.

## Out of scope
Signing Linux artefacts. A Linux equivalent of `smoke-test-artifacts.ps1`. Publishing to any apt repository,
Flatpak or Snap. macOS artefacts. Letting automation publish a release. Re-tagging a published release. Any
product code change - if a release-blocking bug appears, fix it in its own issue and re-tag.
'@ }
)
