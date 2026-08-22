# ADR-0020: Chunked object storage transfers

- Status: Accepted
- Date: 2026-08-22

## Context

[ADR-0019](0019-object-storage-provider-abstraction.md) put `IObjectStorageService` and `IObjectUpload` in
place and drove neither. The premise of the whole milestone is multi-gigabyte objects, and a single-stream
copy is the wrong shape for one: it leaves throughput on the table, and a 4 GB download that fails at
3.9 GB costs a restart from byte zero.

Objects also cost money in a way SFTP files do not. An abandoned multipart upload leaves parts on the
server that are billed until something removes them, so "cancel" has a financial meaning here that
[ADR-0012](0012-transfer-engine.md)'s `.part` sidecar never had.

## Decision

### A new `ObjectStorageTransferEngine`, and no shared base class

`TransferEngine` is SFTP in its bones: `ISftpService` in the constructor, `SftpPath` on every path,
`SftpPublisher`'s rename-aside dance — which exists only because SFTP v3 has no overwriting rename —
parent-directory walks, a remote `.part` sidecar, and one concurrency permit held for an entire recursive
tree. A second constructor over a different service would be two disjoint bodies sharing a class name.

A base class was considered and refused. The only genuinely common body was `Report` and its ETA
arithmetic, which is replaced here by a windowed meter anyway; a base class would have been inheritance for
twelve lines, with ADR-0012's documented SFTP behaviour exposed to collateral change.

The queue is reused verbatim. `TransferQueueRequest.Operation` is
`Func<IProgress<TransferProgress>, CancellationToken, Task<TransferResult>>`, so this engine drops into
`TransfersPageViewModel` unchanged, and all of `TransferContracts.cs` is reused as-is.
`TransferItemResult.Failure` being an `SftpFailure` is an accepted naming wart, consistent with ADR-0019:
object-storage failures are mapped into it at the engine boundary rather than renaming a type used at every
SFTP call site.

### Chunking is orchestrated in Application over a part-level port

Not `TransferUtility`, and not `BlobClient.UploadAsync` with `StorageTransferOptions`. Application cannot
reference the SDKs, so with the vendor helpers the part-size policy, the retry and backoff, the
monotonic-progress reconciliation and the cancel-aborts guarantee would all live in Infrastructure — where
this repository has no CI-visible way to test cloud code. `RemoteFlow.Ssh.IntegrationTests` is opt-in and
does not run in CI at all, and a MinIO or Azurite suite would be equally invisible. CONTRIBUTING's rule —
changes to Domain or Application come with tests — is satisfiable one way and evaded the other.

The consciously accepted cost is vendor-maintained correctness for multipart edge cases. The mitigations
are the decisions below, each of which is pinned by a test.

The provider difference is smaller than it looks. S3 has a server-issued upload id and an explicit abort;
Azure has client-generated equal-length base64 block ids, no server-side session and no abort. Both vanish
behind an opaque handle, an opaque per-part tag, and an `AbortAsync` permitted to be a no-op — the same
trick this repository already runs with one `ISftpService` over two very different transports. A test runs
the whole cancellation path against a no-op abort and asserts identical engine behaviour.

### Below `SingleShotThreshold`, the SDK owns it

At or under 16 MiB, the plain single-request path. Hand-rolling buys nothing there, and a multipart upload
of a 200 KB file costs three round trips instead of one. An object whose length is zero or unknown takes
the single-stream read path and issues no ranged request at all.

### Each SDK keeps its transport retry, tuned to one attempt

`MaxErrorRetry = 1` and `MaxRetries = 1`. Leaving the defaults gives up to sixteen HTTP attempts and
minutes of invisible delay for one part while the user watches a stalled bar. Setting zero is worse in a
different way: the SDK is the only layer that can see a `Retry-After` header or S3's `SlowDown`, and it can
retry inside the request without re-streaming the part. So the SDK does one fast in-request retry and the
engine does up to four slow re-streamed attempts — bounded at eight, and explainable.

### The part-size ladder is derived arithmetic, not a setting

`partSize = Clamp(NextPowerOfTwo(Ceil(total / 8000)), 8 MiB, 1 GiB)`, then squeezed into the provider's own
floor and ceiling. The 8 MiB floor — not S3's 5 MiB — satisfies both providers with headroom against a
slightly misreported size. The 8,000-part budget against S3's 10,000-part cap absorbs a wrong total without
an unrecoverable failure at part 9,999. The 1 GiB ceiling bounds the cost of retrying one part.

A `StoragePartSizeMiB` setting would be a footgun disguised as a preference: a user picking 5 MiB for a
500 GB object needs 95,368 parts and hits the cap. An object larger than the ladder can address is refused
before the first network call, rather than after a `CreateMultipartUpload` that then has to be aborted.

### Part content is a factory, never a bare `Stream`

`Func<CancellationToken, ValueTask<Stream>>`. A retried part needs a fresh stream; handing the same one
twice is the classic hand-rolled-multipart bug, because the SDK has already consumed or seeked it. The
factory makes the invariant structural rather than a comment.

### Nothing buffers a part

Each in-flight part is a `BoundedFileSegmentStream` — a read-only seekable window over its own file handle
— so peak managed memory is `MaxPartsInFlight × CopyBufferSize`: four times one mebibyte, whether the
object is 4 GB or 500 GB. The "8 in flight × 64 MiB = 512 MiB" figure only materialises if you buffer, and
this design does not.

`MaxPartsInFlight` defaults to 4, not 8. `TransfersPageViewModel` already permits three concurrent
transfers, so 3 × 4 = 12 concurrent HTTP requests — enough to saturate a link without starving a home
uplink or tripping connection limits.

### Downloads preallocate one `.part` and write at absolute offsets

`RandomAccess.SetLength` up front fails fast on `ENOSPC`, so the user learns there is no room for a 500 GB
object before transferring 499 GB of it. Ranges are written with `RandomAccess.WriteAsync` at their
absolute offset. Per-range temp files then concatenated would double the disk write volume, need twice the
free space, and add a long non-cancellable phase with the bar pinned at 100%.

Every ranged read carries the object's ETag as an if-match precondition, so an object that changes under a
transfer comes back as `SftpError.PreconditionFailed` — "restart" — rather than silently splicing bytes
from two versions.

### Progress is monotonic by an explicit clamp

Reported as `Math.Max(lastReported, sum(perRangeBytes))`. The obvious alternative — tracking the highest
contiguous offset — leaves the bar at 0% for the whole transfer whenever range 0 happens to finish last. On
the upload side `CountingReadStream` reports `Math.Max(high, Position)`, because S3 reads the seekable
stream once for a checksum and rewinds, and cumulative counting would double-count.

Progress is reported *under* the reporter's lock rather than merely computed under it: parts report from
many threads at once, and releasing the lock first would let a later value reach the sink ahead of an
earlier one.

### Abort on every exit that is not "Complete returned success", always with a fresh token

Passing the cancelled token means the abort is itself cancelled and the parts survive — that is *the* bug
in cancel-aborts-multipart code, and `TransferEngine.BestEffortDeleteRemoteAsync` already does it right for
SFTP. That includes the always-forgotten case: `CompleteMultipartUpload` itself failing, which leaves the
most parts behind. The abort is bounded by a timeout so a dead network cannot hang the cancel, and if it
fails the transfer still reports `Cancelled` or `Failed` with a message saying incomplete parts may remain
and may be billed. Swallowing that would be dishonest about money.

The honest limit belongs here rather than in a claim: a process kill or power loss skips the abort
entirely, so **the only durable guarantee against paid-for orphans is a bucket lifecycle rule** — S3's
`AbortIncompleteMultipartUpload` at seven days, or Azure's uncommitted-block expiry, which is automatic.
Recommend it; the client cannot guarantee it.

### The rate becomes windowed

`transferred / elapsed` is not merely imprecise but actively misleading over hours: a 500 GB transfer
running at 100 MB/s and then 5 MB/s reports about 52 MB/s and an estimate hours short, exactly when the
user needs to decide whether to leave it running. `TransferRateMeter` is a five-second ring over
`(timestamp, cumulativeBytes)` samples. Zero elapsed does not divide by zero, a zero rate gives a null
estimate, and completion gives `TimeSpan.Zero`.

Progress is reported at 250 ms. `CoalescingProgress` keeps only the latest value per 100 ms tick, so
reporting faster is pure discard — the existing per-64-KiB reporting throws away roughly 1,590 of 1,600
reports per second.

### `TimeProvider` for every delay

`Microsoft.Extensions.TimeProvider.Testing` is already on every test project, so backoff is tested by
advancing a virtual clock rather than by sleeping. Jitter is a `Func` on the options record, so a test can
pin it and assert exact delays.

### A sibling `ObjectTransferOptions`, not an extension of `TransferOptions`

`BufferSize` and `MaxConcurrentTransfers` mean different things once one file is many parallel requests.
The record is validated in the engine's constructor, the way `TransferEngine` validates its own.

## Consequences

Three settings are added — `StorageMaxPartsInFlight`, `StorageConflictDefault` (default `Overwrite`) and
`StorageDownloadDir`, mirroring `SftpDownloadDir` — and all three are in `SettingKeys.All`, without which
they would get no seeded default.

`InMemoryObjectStorage` in TestSupport grows a container dimension, real pagination behind an opaque token,
ETags that change on write and honour an if-match precondition, configurable provider part limits, and a
scripted-failure surface (`FailPart`, `StallPart`, `FailComplete`, `FailAbort`, `FailRange`,
`TruncateRange`) that records each abort together with whether the token it was handed was cancelled.

## Known limitations

- **No queue-wide aggregate byte totals.** The progress bar is per transfer. Adding a queue-wide total
  means mutating a shared singleton with five collections under a lock, for a cosmetic gain.
- **The queue's concurrency limit stays hard-coded at three.** It is a constructor default registered
  parameterless, and `ISettingsStore.Get` is async, so a DI factory cannot read it without blocking. Making
  it configurable is a real change to the composition root.
- **No bandwidth throttling.**

## Deferred

Transfer resume across an application restart, still deferred by ADR-0012 for the same reason: without a
server capability and an identity check, a resumed offset can splice bytes from two different object
versions. Retrofitting `TransferEngine` onto `TransferRateMeter` is deliberately out of scope — ADR-0012's
behaviour is not being touched.
