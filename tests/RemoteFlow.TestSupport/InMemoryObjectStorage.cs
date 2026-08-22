using System.Collections.Concurrent;
using System.Text;
using RemoteFlow.Application.Abstractions.Sftp;
using RemoteFlow.Application.Abstractions.Storage;

namespace RemoteFlow.TestSupport;

/// <summary>One recorded abort, and — the point of recording it — whether the token it was handed had
/// already been cancelled. Passing the cancelled token is <em>the</em> bug in cancel-aborts-multipart
/// code: the abort is itself cancelled and the parts survive, billed.</summary>
public sealed record ObjectAbortRecord(bool TokenWasCancelled, bool Succeeded);

/// <summary>One upload-part attempt, successful or not.</summary>
public sealed record ObjectPartAttempt(int PartNumber, long DeclaredLength, long BytesRead, bool Succeeded);

/// <summary>An account-rooted object store held in memory, for exercising
/// <see cref="IObjectStorageService"/> without a network. It follows the same rules the real adapters do:
/// one level per listing with common prefixes grouped, folder markers suppressed, a zero-byte marker
/// object for a created folder, refusal to create or delete a container, real pagination behind an opaque
/// token, and ETags that change on every write and are honoured as an if-match precondition.
///
/// It also carries a scripted-failure surface — <see cref="FailPart"/>, <see cref="StallPart"/>,
/// <see cref="FailComplete"/>, <see cref="FailAbort"/>, <see cref="FailRange"/> and
/// <see cref="TruncateRange"/> — so retry, backoff, ordering and abort behaviour can be asserted without a
/// cloud account, and records what the engine actually did with it.
///
/// <paramref name="abortIsNoOp"/> makes the uploads Azure-shaped: Azure has no abort call, so its abort
/// does nothing and still reports success.</summary>
public sealed class InMemoryObjectStorage(bool abortIsNoOp = false) : IObjectStorageService
{
    private readonly ConcurrentDictionary<string, byte[]> _objects = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _etags = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _containers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, PartFailure> _partFailures = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource> _stalls = new();
    private readonly ConcurrentDictionary<long, PartFailure> _rangeFailures = new();
    private readonly ConcurrentDictionary<long, int> _rangeTruncations = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource> _rangeStalls = new();
    private readonly ConcurrentQueue<ObjectAbortRecord> _aborts = new();
    private readonly ConcurrentQueue<int> _completionOrder = new();
    private readonly ConcurrentQueue<ObjectPartAttempt> _attempts = new();
    private readonly Lock _sync = new();
    private PartFailure? _completeFailure;
    private bool _abortFails;
    private int _etagCounter;
    private int _partsInFlight;
    private int _maxPartsInFlight;
    private int _readsInFlight;
    private int _maxReadsInFlight;
    private int _rangedReads;
    private int _wholeReads;
    private int _writes;

    public bool AbortIsNoOp { get; } = abortIsNoOp;

    /// <summary>The part limits handed to a caller through <see cref="IObjectUpload"/>. Settable so a test
    /// can force many small parts out of an object it can afford to hold in memory.</summary>
    public long MinimumPartSize { get; set; } = 5L * 1024 * 1024;

    public long MaximumPartSize { get; set; } = 5L * 1024 * 1024 * 1024;

    public int MaximumPartCount { get; set; } = 10_000;

    /// <summary>Caps a listing page below what the caller asked for, the way a real store is free to. Set
    /// it small to make an engine's paging loop run for real against a handful of keys.</summary>
    public int? PageSizeCap { get; set; }

    public int ListCount { get; private set; }

    public IReadOnlyCollection<string> Keys => [.. _objects.Keys];

    public IReadOnlyList<ObjectAbortRecord> Aborts => [.. _aborts];

    /// <summary>Part numbers in the order their uploads finished, which is the order the network gave and
    /// not the order the object has to be assembled in.</summary>
    public IReadOnlyList<int> PartCompletionOrder => [.. _completionOrder];

    /// <summary>The part numbers handed to <c>Complete</c>, in the order they were handed over.</summary>
    public IReadOnlyList<int> CompletedPartNumbers { get; private set; } = [];

    public IReadOnlyList<ObjectPartAttempt> PartAttempts => [.. _attempts];

    public int CompletedPartCount => _completionOrder.Count;

    public int MaxConcurrentParts => Volatile.Read(ref _maxPartsInFlight);

    public int MaxConcurrentReads => Volatile.Read(ref _maxReadsInFlight);

    public int RangedReadCount => Volatile.Read(ref _rangedReads);

    public int WholeReadCount => Volatile.Read(ref _wholeReads);

    public int WriteCount => Volatile.Read(ref _writes);

    public int StartUploadCount { get; private set; }

    public int CompleteCount { get; private set; }

    public void AddContainer(string name)
    {
        _ = _containers.TryAdd(name, 0);
    }

    /// <summary>Seeds an object at an account-rooted path, creating its container on the way.</summary>
    public void Seed(string path, byte[] content)
    {
        var (container, key) = ObjectStoragePath.Split(path);
        ArgumentNullException.ThrowIfNull(container);
        _ = _containers.TryAdd(container, 0);
        Store($"{container}/{key}", content);
    }

    /// <summary>Fails the next <paramref name="times"/> attempts at one part. A transient failure is one
    /// the engine retries; a non-transient one it must not.</summary>
    public void FailPart(int partNumber, int times, bool transient = true)
    {
        _partFailures[partNumber] = new PartFailure(times, transient);
    }

    /// <summary>Holds one part open until <see cref="ReleasePart"/>, so later parts finish first and the
    /// ordering guarantee at <c>Complete</c> is actually exercised.</summary>
    public void StallPart(int partNumber)
    {
        _stalls[partNumber] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void ReleasePart(int partNumber)
    {
        if (_stalls.TryGetValue(partNumber, out var gate))
        {
            _ = gate.TrySetResult();
        }
    }

    public void FailComplete(int times, bool transient = false)
    {
        _completeFailure = new PartFailure(times, transient);
    }

    public void FailAbort()
    {
        _abortFails = true;
    }

    /// <summary>Holds one ranged read open until <see cref="ReleaseRange"/>, so a transfer can be
    /// cancelled while a range is genuinely in flight.</summary>
    public void StallRange(long offset)
    {
        _rangeStalls[offset] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void ReleaseRange(long offset)
    {
        if (_rangeStalls.TryGetValue(offset, out var gate))
        {
            _ = gate.TrySetResult();
        }
    }

    /// <summary>Fails the next <paramref name="times"/> ranged reads that start at one offset.</summary>
    public void FailRange(long offset, int times, bool transient = true)
    {
        _rangeFailures[offset] = new PartFailure(times, transient);
    }

    /// <summary>Returns a short stream for the next <paramref name="times"/> ranged reads at one offset,
    /// which is how a real connection drop looks from the client: bytes, then silence.</summary>
    public void TruncateRange(long offset, int times)
    {
        _rangeTruncations[offset] = times;
    }

    public int AttemptsFor(int partNumber)
    {
        return _attempts.Count(attempt => attempt.PartNumber == partNumber);
    }

    public Task<SftpResult<ObjectStoragePage>> ListAsync(
        string path,
        ObjectStoragePaging? paging = null,
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            ListCount++;
        }

        var (container, key) = ObjectStoragePath.Split(path);
        var all = container is null
            ? ListContainers()
            : ListPrefix(container, key, paging?.NamePrefix);
        return Task.FromResult(SftpResult<ObjectStoragePage>.Success(Page(all, paging)));
    }

    public Task<SftpResult<ObjectEntry?>> StatAsync(string path, CancellationToken cancellationToken = default)
    {
        var (container, key) = ObjectStoragePath.Split(path);
        if (container is null)
        {
            return Task.FromResult(SftpResult<ObjectEntry?>.Success(new ObjectEntry(
                ObjectStoragePath.Root,
                ObjectStoragePath.Root,
                ObjectEntryKind.Prefix,
                0,
                null,
                null)));
        }

        if (key.Length == 0)
        {
            return Task.FromResult(_containers.ContainsKey(container)
                ? SftpResult<ObjectEntry?>.Success(new ObjectEntry(
                    container,
                    $"/{container}",
                    ObjectEntryKind.Container,
                    0,
                    null,
                    null))
                : SftpResult<ObjectEntry?>.Success(null));
        }

        var stored = $"{container}/{key}";
        if (_objects.TryGetValue(stored, out var content))
        {
            return Task.FromResult(SftpResult<ObjectEntry?>.Success(new ObjectEntry(
                ObjectStoragePath.GetName(key),
                path,
                ObjectEntryKind.Object,
                content.Length,
                null,
                ETagOf(stored))));
        }

        var prefix = $"{container}/{ObjectStoragePath.AsPrefix(key)}";
        return Task.FromResult(_objects.Keys.Any(candidate =>
                candidate.StartsWith(prefix, StringComparison.Ordinal))
            ? SftpResult<ObjectEntry?>.Success(new ObjectEntry(
                ObjectStoragePath.GetName(key),
                path,
                ObjectEntryKind.Prefix,
                0,
                null,
                null))
            : SftpResult<ObjectEntry?>.Success(null));
    }

    public Task<SftpResult> CreateFolderAsync(string path, CancellationToken cancellationToken = default)
    {
        var (container, key) = ObjectStoragePath.Split(path);
        if (container is null || key.Length == 0)
        {
            return Task.FromResult(SftpResult.Fail(
                SftpError.NotSupported,
                "RemoteFlow does not create buckets or containers."));
        }

        var marker = $"{container}/{ObjectStoragePath.AsPrefix(key)}";
        if (_objects.Keys.Any(candidate => candidate.StartsWith(marker, StringComparison.Ordinal)))
        {
            return Task.FromResult(SftpResult.Fail(SftpError.AlreadyExists, $"'{path}' already exists."));
        }

        _ = _containers.TryAdd(container, 0);
        Store(marker, []);
        return Task.FromResult(SftpResult.Success());
    }

    public Task<SftpResult> DeleteAsync(
        string path,
        bool recursive,
        CancellationToken cancellationToken = default)
    {
        var (container, key) = ObjectStoragePath.Split(path);
        if (container is null || key.Length == 0)
        {
            return Task.FromResult(SftpResult.Fail(
                SftpError.NotSupported,
                "RemoteFlow does not delete buckets or containers."));
        }

        var stored = $"{container}/{key}";
        if (_objects.TryRemove(stored, out _))
        {
            _ = _etags.TryRemove(stored, out _);
            return Task.FromResult(SftpResult.Success());
        }

        var marker = $"{container}/{ObjectStoragePath.AsPrefix(key)}";
        var matches = _objects.Keys
            .Where(candidate => candidate.StartsWith(marker, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length == 0)
        {
            return Task.FromResult(SftpResult.Fail(SftpError.NotFound, $"'{path}' was not found."));
        }

        var children = matches.Where(candidate => !string.Equals(candidate, marker, StringComparison.Ordinal));
        if (!recursive && children.Any())
        {
            return Task.FromResult(SftpResult.Fail(
                SftpError.NotSupported,
                "The folder is not empty. Delete it recursively."));
        }

        foreach (var match in matches.Where(candidate => !string.Equals(candidate, marker, StringComparison.Ordinal)))
        {
            _ = _objects.TryRemove(match, out _);
            _ = _etags.TryRemove(match, out _);
        }

        // The marker last, so an interrupted delete leaves a visible folder rather than an invisible one.
        _ = _objects.TryRemove(marker, out _);
        _ = _etags.TryRemove(marker, out _);
        return Task.FromResult(SftpResult.Success());
    }

    public async Task<SftpResult<Stream>> OpenReadAsync(
        string path,
        ObjectReadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var ranged = options is not null && (options.Offset > 0 || options.Length is not null);
        if (ranged && _rangeStalls.TryGetValue(options!.Offset, out var stall))
        {
            await stall.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        _ = ranged ? Interlocked.Increment(ref _rangedReads) : Interlocked.Increment(ref _wholeReads);

        var (container, key) = ObjectStoragePath.Split(path);
        var stored = $"{container}/{key}";
        if (container is null || !_objects.TryGetValue(stored, out var content))
        {
            return SftpResult<Stream>.Fail(SftpError.NotFound, $"'{path}' was not found.");
        }

        if (options?.IfMatchETag is { Length: > 0 } etag &&
            !string.Equals(etag, ETagOf(stored), StringComparison.Ordinal))
        {
            return SftpResult<Stream>.Fail(
                SftpError.PreconditionFailed,
                $"'{path}' changed while it was being read.");
        }

        var offset = (int)(options?.Offset ?? 0);
        if (offset > content.Length)
        {
            return SftpResult<Stream>.Fail(SftpError.InvalidPath, "The range is out of bounds.");
        }

        if (ranged && _rangeFailures.TryGetValue(options!.Offset, out var scripted) && scripted.Take())
        {
            return SftpResult<Stream>.Fail(
                scripted.Transient ? SftpError.ConnectionLost : SftpError.PermissionDenied,
                "The ranged read failed.");
        }

        var length = (int)Math.Min(options?.Length ?? (content.Length - offset), content.Length - offset);
        if (ranged && TakeTruncation(options!.Offset))
        {
            length /= 2;
        }

        var body = new MemoryStream(content.AsSpan(offset, length).ToArray(), writable: false);
        if (!ranged)
        {
            return SftpResult<Stream>.Success(body);
        }

        // Concurrency is only observable across the stream's lifetime: the request is answered at once,
        // but the engine holds its permit until the range has been read.
        EnterRead();
        return SftpResult<Stream>.Success(new TrackedStream(body, ExitRead));
    }

    public async Task<SftpResult<ObjectEntry>> WriteAsync(
        string path,
        Stream content,
        long? length = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        _ = Interlocked.Increment(ref _writes);
        var (container, key) = ObjectStoragePath.Split(path);
        if (container is null || key.Length == 0)
        {
            return SftpResult<ObjectEntry>.Fail(SftpError.InvalidPath, $"'{path}' does not name an object.");
        }

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        var stored = $"{container}/{key}";
        _ = _containers.TryAdd(container, 0);
        Store(stored, buffer.ToArray());
        return SftpResult<ObjectEntry>.Success(new ObjectEntry(
            ObjectStoragePath.GetName(key),
            path,
            ObjectEntryKind.Object,
            _objects[stored].Length,
            null,
            ETagOf(stored)));
    }

    public Task<SftpResult<IObjectUpload>> StartUploadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            StartUploadCount++;
        }

        var (container, key) = ObjectStoragePath.Split(path);
        return Task.FromResult(container is null || key.Length == 0
            ? SftpResult<IObjectUpload>.Fail(SftpError.InvalidPath, $"'{path}' does not name an object.")
            : SftpResult<IObjectUpload>.Success(new InMemoryObjectUpload(this, path)));
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    private ObjectStoragePage Page(ObjectEntry[] entries, ObjectStoragePaging? paging)
    {
        var size = Math.Min(
            Math.Clamp(
                paging?.PageSize ?? ObjectStoragePaging.DefaultPageSize,
                1,
                ObjectStoragePaging.MaximumPageSize),
            PageSizeCap ?? int.MaxValue);
        var start = 0;
        if (paging?.ContinuationToken is { Length: > 0 } token)
        {
            var after = Encoding.UTF8.GetString(Convert.FromBase64String(token));
            var index = entries
                .Select((entry, position) => (entry.Path, position))
                .FirstOrDefault(candidate => string.Equals(candidate.Path, after, StringComparison.Ordinal));
            start = index.Path is null ? entries.Length : index.position + 1;
        }

        var window = entries.Skip(start).Take(size).ToArray();
        var more = start + window.Length < entries.Length;
        return new ObjectStoragePage(
            window,
            more && window.Length > 0
                ? Convert.ToBase64String(Encoding.UTF8.GetBytes(window[^1].Path))
                : null);
    }

    private bool TakeTruncation(long offset)
    {
        while (_rangeTruncations.TryGetValue(offset, out var remaining) && remaining > 0)
        {
            if (_rangeTruncations.TryUpdate(offset, remaining - 1, remaining))
            {
                return true;
            }
        }

        return false;
    }

    private void Store(string stored, byte[] content)
    {
        _objects[stored] = content;

        // The ETag changes on every write, the way a real one does, so a resumed ranged read against a
        // stale one is a precondition failure rather than a silent splice of two object versions.
        _etags[stored] = $"etag-{Interlocked.Increment(ref _etagCounter)}";
    }

    private string ETagOf(string stored)
    {
        return _etags.TryGetValue(stored, out var etag) ? etag : "etag-0";
    }

    private ObjectEntry[] ListContainers()
    {
        return [.. _containers.Keys
            .Order(StringComparer.Ordinal)
            .Select(name => new ObjectEntry(name, $"/{name}", ObjectEntryKind.Container, 0, null, null))];
    }

    private ObjectEntry[] ListPrefix(string container, string key, string? namePrefix)
    {
        var prefix = ObjectStoragePath.AsPrefix(key);
        var search = prefix + namePrefix;
        var entries = new List<ObjectEntry>();
        var seenPrefixes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stored in _objects.Keys.Where(candidate =>
                     candidate.StartsWith($"{container}/{search}", StringComparison.Ordinal))
                 .Order(StringComparer.Ordinal))
        {
            var relative = stored[(container.Length + 1 + prefix.Length)..];
            var separator = relative.IndexOf(ObjectStoragePath.Separator, StringComparison.Ordinal);
            if (separator >= 0)
            {
                var name = relative[..separator];
                if (seenPrefixes.Add(name))
                {
                    entries.Add(new ObjectEntry(
                        name,
                        $"/{container}/{prefix}{name}",
                        ObjectEntryKind.Prefix,
                        0,
                        null,
                        null));
                }

                continue;
            }

            // Marker suppression, the same rule the adapters apply: the listed prefix itself, and any
            // zero-byte key ending in a separator, would otherwise show up as an empty file beside the
            // folder it stands for.
            if (relative.Length == 0)
            {
                continue;
            }

            entries.Add(new ObjectEntry(
                relative,
                $"/{container}/{prefix}{relative}",
                ObjectEntryKind.Object,
                _objects[stored].Length,
                null,
                ETagOf(stored)));
        }

        return [.. entries];
    }

    private void EnterPart()
    {
        var inFlight = Interlocked.Increment(ref _partsInFlight);
        var highest = Volatile.Read(ref _maxPartsInFlight);
        while (inFlight > highest)
        {
            var seen = Interlocked.CompareExchange(ref _maxPartsInFlight, inFlight, highest);
            if (seen == highest)
            {
                break;
            }

            highest = seen;
        }
    }

    private void ExitPart()
    {
        _ = Interlocked.Decrement(ref _partsInFlight);
    }

    private void EnterRead()
    {
        var inFlight = Interlocked.Increment(ref _readsInFlight);
        var highest = Volatile.Read(ref _maxReadsInFlight);
        while (inFlight > highest)
        {
            var seen = Interlocked.CompareExchange(ref _maxReadsInFlight, inFlight, highest);
            if (seen == highest)
            {
                break;
            }

            highest = seen;
        }
    }

    private void ExitRead()
    {
        _ = Interlocked.Decrement(ref _readsInFlight);
    }

    private sealed class TrackedStream(MemoryStream inner, Action onDisposed) : Stream
    {
        private int _disposed;

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return inner.Read(buffer, offset, count);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return inner.ReadAsync(buffer, cancellationToken);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            return inner.Seek(offset, origin);
        }

        public override void Flush() { }

        public override void SetLength(long value)
        {
            throw new NotSupportedException("The stored object is read-only.");
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException("The stored object is read-only.");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                inner.Dispose();
                onDisposed();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class PartFailure(int times, bool transient)
    {
        private int _remaining = times;

        public bool Transient { get; } = transient;

        public bool Take()
        {
            var remaining = Volatile.Read(ref _remaining);
            while (remaining > 0)
            {
                var seen = Interlocked.CompareExchange(ref _remaining, remaining - 1, remaining);
                if (seen == remaining)
                {
                    return true;
                }

                remaining = seen;
            }

            return false;
        }
    }

    private sealed class InMemoryObjectUpload(InMemoryObjectStorage store, string path) : IObjectUpload
    {
        private readonly ConcurrentDictionary<int, byte[]> _parts = new();

        public long MinimumPartSize => store.MinimumPartSize;

        public long MaximumPartSize => store.MaximumPartSize;

        public int MaximumPartCount => store.MaximumPartCount;

        public async Task<SftpResult> UploadPartAsync(
            int partNumber,
            long length,
            Func<CancellationToken, ValueTask<Stream>> content,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(content);
            store.EnterPart();
            try
            {
                if (store._stalls.TryGetValue(partNumber, out var gate))
                {
                    await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                var scripted = store._partFailures.TryGetValue(partNumber, out var failure) && failure.Take()
                    ? failure
                    : null;

                // Taken as a factory on purpose: a retried part needs a fresh stream, because the failed
                // attempt has already consumed the one it was handed.
                var stream = await content(cancellationToken).ConfigureAwait(false);
                await using (stream.ConfigureAwait(false))
                {
                    using var buffer = new MemoryStream();
                    if (scripted is null)
                    {
                        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                        _parts[partNumber] = buffer.ToArray();
                        store._attempts.Enqueue(
                            new ObjectPartAttempt(partNumber, length, buffer.Length, Succeeded: true));
                        store._completionOrder.Enqueue(partNumber);
                        return SftpResult.Success();
                    }

                    // A scripted failure still reads part of the stream, the way a request that dies
                    // mid-flight does, so the retry has real progress to be monotonic across.
                    var half = new byte[Math.Max(1, length / 2)];
                    var read = await stream.ReadAsync(half, cancellationToken).ConfigureAwait(false);
                    store._attempts.Enqueue(new ObjectPartAttempt(partNumber, length, read, Succeeded: false));
                    return SftpResult.Fail(
                        scripted.Transient ? SftpError.ConnectionLost : SftpError.PermissionDenied,
                        $"Part {partNumber} failed.");
                }
            }
            finally
            {
                store.ExitPart();
            }
        }

        public async Task<SftpResult<ObjectEntry>> CompleteAsync(CancellationToken cancellationToken = default)
        {
            lock (store._sync)
            {
                store.CompleteCount++;
            }

            var ordered = _parts.Keys.Order().ToArray();
            store.CompletedPartNumbers = ordered;
            if (store._completeFailure is { } scripted && scripted.Take())
            {
                return SftpResult<ObjectEntry>.Fail(
                    scripted.Transient ? SftpError.ConnectionLost : SftpError.PermissionDenied,
                    "The upload could not be completed.");
            }

            using var joined = new MemoryStream();
            foreach (var part in ordered)
            {
                await joined.WriteAsync(_parts[part], cancellationToken).ConfigureAwait(false);
            }

            joined.Position = 0;
            return await store.WriteAsync(path, joined, joined.Length, cancellationToken).ConfigureAwait(false);
        }

        public Task<SftpResult> AbortAsync(CancellationToken cancellationToken = default)
        {
            var failed = store._abortFails;
            store._aborts.Enqueue(new ObjectAbortRecord(
                cancellationToken.IsCancellationRequested,
                Succeeded: !failed));
            if (failed)
            {
                return Task.FromResult(SftpResult.Fail(SftpError.ConnectionLost, "The abort failed."));
            }

            // Azure has no abort call: uncommitted blocks are invisible, unbilled, and expire after seven
            // days. Reporting failure for a no-op would make every cancelled transfer look broken.
            if (!store.AbortIsNoOp)
            {
                _parts.Clear();
            }

            return Task.FromResult(SftpResult.Success());
        }

        public ValueTask DisposeAsync()
        {
            _parts.Clear();
            return ValueTask.CompletedTask;
        }
    }
}
