# ADR-0019: Object storage provider abstraction

- Status: Accepted
- Date: 2026-08-22

## Context

RemoteFlow's file browsing, transfers and remote editing are built on `ISftpService`, obtained from
`ISshConnection.OpenSftp()`. AWS S3 and Azure Blob Storage are file-shaped enough to browse and transfer
against, and different enough that reusing that interface would be a lie with a bill attached.

Three differences matter. There is no connection: every operation is an independent, authenticated HTTP
request. There are no directories: a flat key space is presented as a tree by grouping on a delimiter.
And there is no rename: the closest primitive is a server-side copy followed by a delete, billed per byte
and capped at 5 GiB per copy.

## Decision

### A new `IObjectStorageService`, not an `ISftpService` implementation

`SftpPublisher` — the whole of [ADR-0012](0012-transfer-engine.md)'s atomic-publish story — needs
`RenameAsync` and `SetPermissionsAsync`. A faked `ISftpService` would compile against S3 and turn every
upload publish into a `CopyObject`, on a feature whose premise is multi-gigabyte objects. `ISftpService` is
also only obtainable from `ISshConnection.OpenSftp()`, so a cloud client would need a sham `ISshConnection`
to exist at all.

### The SFTP result types are reused unchanged, and that is acknowledged debt

`SftpResult`, `SftpFailure` and `SftpError` appear 229 times across 17 files. C# cannot alias an open
generic, so `SftpResult<T>` cannot be given a second honest name, and renaming them would collide with
every other issue in this milestone for no behavioural gain. The names lie; the semantics do not.

One place the reused enum is genuinely lossy: a non-recursive delete of a non-empty prefix returns
`NotSupported` with the message "The folder is not empty. Delete it recursively." No member of `SftpError`
says "not empty", and the message carries what the enum cannot.

`SftpError.PreconditionFailed = 11` is new. HTTP 412 on a ranged GET under an `If-Match` ETag means "the
object changed under you, restart the transfer", and no existing member says that honestly — `NotFound` and
`PermissionDenied` would both send a caller looking in the wrong place. No SFTP adapter returns it, there
is no exhaustive switch over `SftpError` anywhere, and it carries no `EnumValueTests` pin.

### `ProtocolType { S3 = 4, AzureBlob = 5 }` with an `IsObjectStorage()` predicate

`S3` rather than `AmazonS3`, because the same protocol covers MinIO, Ceph/RGW, Backblaze B2, Cloudflare R2
and Wasabi. The predicate exists so that the branch sites which behave differently for object storage do
not each hand-list two members; `GetDisplayName()` exists because `ToString().ToUpperInvariant()` produces
`AZUREBLOB`, which is not a name anybody uses.

### `Host` and `Port` are the real service endpoint

Derived by the editor, but stored and true: `s3.{region}.amazonaws.com`,
`{account}.blob.core.windows.net`, or the authority of a custom endpoint; port 443. Making them optional
would mean relaxing validation in two layers, a nullable-column migration on an indexed `.IsRequired()`
column, and auditing every non-null `Host` consumer — to remove a field that has a truthful value.

The editor overwrites the host box only while it still holds what the editor last put there, the same rule
the port box already follows. Hand-editing it is how a sovereign-cloud account
(`*.blob.core.chinacloudapi.cn`) is reached without another field.

### A custom S3 endpoint and a path-style-addressing flag are in scope

Without them the feature does not work against any S3-compatible service, which is most of the interesting
cases. One nullable string and one bool, defaulting to real AWS. This stays inside "an access key and a
secret key": it changes where the key is sent, not what it is.

### One owned value object for both providers

`ObjectStorageOptions`, columns prefixed `Storage_`: `Region`, `ServiceUrl`, `UsePathStyleAddressing`,
`Container`, `RootPrefix`, `LocalDownloadPath`. A connection is exactly one protocol, so two value objects
would mean two more `OwnsOne` blocks and two more positional `SetOptions` parameters for fields that are
null on every row of the other kind.

Every string is nullable and exactly one bool is not. This is load-bearing: an owned type whose every
column is NULL materialises as `null`, and the `Navigation(...).IsRequired()` configured for it then throws
on query. The non-nullable bool keeps one column populated and makes the migration purely additive with a
single `defaultValue: false`.

### Container-name validation is the loose rule

`[a-z0-9.-]`, 3 to 63 characters, alphanumeric at both ends — not the S3-Azure intersection. Azure forbids
dots and S3 allows them, and the Domain must never reject a name the user's own provider accepts. The
provider is authoritative; anything stricter comes back from it as `InvalidPath`.

### The identifier is plaintext, the secret is not

The access key ID or storage account name goes in the existing `Username` column. The secret access key or
account key goes in the single `CredentialRef` slot as `CredentialKind.StorageSecretKey = 4`, store-key
suffix `storage-secret-key`. Zero schema change, and it satisfies [ADR-0007](0007-credential-storage.md). A
40-character S3 secret and an 88-character Azure key both sit far inside windows-credman's 2560-byte cap.

`AuthMethod` stays `None` and its combo is hidden for these protocols. Adding `AccessKey = 7` would break
`EnumValueTests.AuthMethodValuesAreStable`, add a dead throwing arm to `SshAuthenticationMaterialProvider`,
and — because the combo is `Enum.GetValues<AuthMethod>()` — make "AccessKey" selectable on SSH
connections. Reusing `Password` would put the word "Password" in the details pane for a thing that is not
one.

### The path model is rooted at the account

`/` lists buckets or containers, `/mybucket` is its root, `/mybucket/logs/2026` is a prefix. When
`ObjectStorageOptions.Container` is set, the root is `/{container}`, `ListBuckets` is never called, and
navigating above the root returns `InvalidPath`. When it is empty the pane lists buckets, and a 403 maps to
`PermissionDenied` with a message naming the remedy: "This key cannot list buckets. Set a bucket name on
the connection to browse it directly." Both, because account-wide keys are the common AWS case and
single-bucket-scoped keys are normal in production.

`ObjectStoragePath` is a separate type from `SftpPath`. `SftpPath.Normalize` strips trailing slashes, and
for object storage a trailing slash is semantically load-bearing: `a/` is a prefix marker and `a` is an
object. The algorithm is reused, not the type. Directory-ness is carried by `ObjectEntryKind`, never by a
trailing slash.

### Marker suppression is mandatory

A listing is one level, with `Delimiter = "/"`; common prefixes become directory entries. The adapters drop
any object whose key equals the listed prefix, and any zero-byte key ending in `/`. Without both, every
folder created by RemoteFlow, the AWS console or Azure Storage Explorer appears twice — once as a folder and
once as an empty file. Creating a folder PUTs a zero-byte object at `{key}/`, guarded by a one-key listing;
both vendors' own consoles do this, and a client-side-only placeholder evaporates on refresh.

Recursive delete pages the recursive listing, batches S3 `DeleteObjects` at 1000 keys per request, and runs
Azure deletes at bounded concurrency. **The `{prefix}/` marker is deleted last**, so an interrupted delete
leaves a visible empty folder rather than an invisible half-deleted one.

`Stat` on a container lists with one key rather than calling `HeadBucket`: it proves existence and list
permission at once, using no permission the caller does not already need. On a key it is `HeadObject`, and
on 404 a one-key listing of `{key}/` tells "prefix" from "absent".

### Bucket and container creation and deletion are refused

`NotSupported`, pointing at the provider console. Bucket creation carries region, ownership,
public-access-block, versioning and lifecycle decisions a two-field dialog cannot make safely, and a
mis-created public container is a security incident rather than a UX regression.

### Clients are always constructed with explicit credentials

A parameterless `AmazonS3Client` falls back to AWSSDK.Core's credential chain — `~/.aws/credentials`,
`AWS_*` environment variables, EC2/ECS metadata endpoints — which would silently reach past the access key
the user entered. Both adapters take an explicit `BasicAWSCredentials` / `StorageSharedKeyCredential`, and
the factory refuses outright when no key is stored, so there is never a path on which the chain is reached.

An Azure account key is base64, and `StorageSharedKeyCredential` throws a bare `FormatException` on
anything else. A mistyped key is a thing users do, so the provider catches it and returns
`PermissionDenied` with the remedy.

### The SDKs' own logging and telemetry are turned off explicitly

`AWSConfigs.LoggingConfig` gets `LogTo = None`, `LogResponses = Never` and `LogMetrics = false`;
`BlobClientOptions.Diagnostics` gets logging, content logging, telemetry and distributed tracing all
false, and nothing installs an `AzureEventSourceListener`. `RedactingLoggerProvider` cannot redact what it
never sees, and it cannot see EventSource at all.

### The SDKs live in `RemoteFlow.Infrastructure` only

`DependencyDirectionTests.ApplicationReferencesOnlyApprovedDependencies` allows Application exactly three
non-BCL references and `DomainReferencesOnlyTheBaseClassLibrary` allows Domain none, so a leak into either
already fails the build. The UI would not, so it is named explicitly in
`ApplicationAndUiDoNotReferenceTheObjectStorageSdks`.

### `System.Security.Cryptography.ProtectedData` is pinned to 10.0.10

It arrives transitively through `Azure.Core` → `Microsoft.Identity.Client.Extensions.Msal` at 4.5.0, whose
nuspec carries only a `licenceUrl` and no SPDX expression — exactly the shape `generate-notices.ps1`
throws on, which fails the build through RF0002. Transitive pinning promotes it to a version declaring
`MIT` as an expression. This is the `SQLitePCLRaw.lib.e_sqlite3` precedent already in
`Directory.Packages.props`.

The MSAL footprint itself is unused. RemoteFlow authenticates with a shared key and never with a token
credential, but `Azure.Core` depends on MSAL unconditionally, so `Microsoft.Identity.Client`,
`Microsoft.Identity.Client.Extensions.Msal` and `Microsoft.IdentityModel.Abstractions` ship in the binary
regardless. They are attributed in `THIRD-PARTY-NOTICES.md` like everything else. Accepted rather than
worked around: the alternative is hand-rolling the shared-key signature.

### The schema version is bumped to 2 and written back

A 0.2.x binary opening a database that contains `Protocol = 4` throws in the icon switch on the
connections page. `GuardAgainstNewerSchemaAsync` exists to turn that into a clear message instead. The bump
is inert without the write-back after `MigrateAsync`, because `SettingsStore.SeedDefaults` only inserts
keys that are missing.

### Backup carries one optional trailing field

`BackupConnection` gains `BackupObjectStorageOptions? ObjectStorage = null`. Optional-with-default is a
requirement, not a style preference: it keeps `Fixtures/backup-v1-golden.zip` importing without being
regenerated, and it keeps the existing test files that construct `BackupConnection` compiling.
`BackupFormat.DomainEntityCoverage` needs no entry — `ObjectStorageOptions` is a value object, and the
reflection test filters on the `Entities` namespace.

Backup enums serialise as camelCase **strings**, not integers, so an older RemoteFlow reading an archive
containing `"protocol": "s3"` fails the whole import rather than that one connection. The
ignore-unknown-fields rule in [docs/backup-format.md](../backup-format.md) covers unknown fields, not
unknown enum members. This is accepted and documented there, and the failure message is pinned in a test.
An `Unsupported` fallback member was considered and refused: silently importing a connection whose protocol
cannot be opened is worse than refusing the file.

## Consequences

`IObjectUpload` exposes each provider's real limits — `MinimumPartSize`, `MaximumPartSize`,
`MaximumPartCount` — so a caller can size parts without knowing which provider it has. Part content is
taken as `Func<CancellationToken, ValueTask<Stream>>` rather than a bare `Stream`, because a retried part
needs a fresh one: the failed attempt has already consumed the stream it was handed. `AbortAsync` is best
effort, never throws, and is permitted to be a no-op — Azure has no abort call, because uncommitted blocks
are invisible, unbilled, and garbage-collected after seven days. Neither adapter buffers a whole part.

## Deferred

- **The transfer engine and chunked uploads.** `IObjectUpload` is the seam; nothing drives it yet.
- **The Storage page and any browsing UI.** `ConnectionOpenMode.Storage` and the branch in
  `SshConnectionSessionOpener` are in place and report that browsing is not available yet, so a
  double-clicked storage connection gets a sentence rather than "Only SSH and SFTP connections can open an
  SSH terminal session".
- **Remote editing over objects.** `RemoteEditService` detects conflicts by size, mtime and SHA-256; on
  objects the correct primitive is the ETag, and "open a 400 MB object in your editor and re-upload on
  save" is a different feature with different failure modes.
- **Server-side copy and rename**, object versioning, SSE-C and SSE-KMS keys, and lifecycle rules.
