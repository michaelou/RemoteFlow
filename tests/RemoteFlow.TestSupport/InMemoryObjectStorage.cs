using System.Collections.Concurrent;
using RemoteFlow.Application.Abstractions.Sftp;
using RemoteFlow.Application.Abstractions.Storage;

namespace RemoteFlow.TestSupport;

/// <summary>An account-rooted object store held in memory, for exercising
/// <see cref="IObjectStorageService"/> without a network. It follows the same rules the real adapters do:
/// one level per listing with common prefixes grouped, folder markers suppressed, a zero-byte marker
/// object for a created folder, and refusal to create or delete a container.
///
/// <paramref name="abortIsNoOp"/> makes the uploads Azure-shaped: Azure has no abort call, so its abort
/// does nothing and still reports success.</summary>
public sealed class InMemoryObjectStorage(bool abortIsNoOp = false) : IObjectStorageService
{
    private readonly ConcurrentDictionary<string, byte[]> _objects = new(StringComparer.Ordinal);
    private readonly HashSet<string> _containers = new(StringComparer.Ordinal);

    public bool AbortIsNoOp { get; } = abortIsNoOp;

    public IReadOnlyCollection<string> Keys => [.. _objects.Keys];

    public void AddContainer(string name)
    {
        _ = _containers.Add(name);
    }

    /// <summary>Seeds an object at an account-rooted path, creating its container on the way.</summary>
    public void Seed(string path, byte[] content)
    {
        var (container, key) = ObjectStoragePath.Split(path);
        ArgumentNullException.ThrowIfNull(container);
        _ = _containers.Add(container);
        _objects[$"{container}/{key}"] = content;
    }

    public Task<SftpResult<ObjectStoragePage>> ListAsync(
        string path,
        ObjectStoragePaging? paging = null,
        CancellationToken cancellationToken = default)
    {
        var (container, key) = ObjectStoragePath.Split(path);
        if (container is null)
        {
            var containers = _containers
                .Order(StringComparer.Ordinal)
                .Select(name => new ObjectEntry(name, $"/{name}", ObjectEntryKind.Container, 0, null, null))
                .ToArray();
            return Task.FromResult(SftpResult<ObjectStoragePage>.Success(new ObjectStoragePage(containers)));
        }

        var prefix = ObjectStoragePath.AsPrefix(key);
        var entries = new List<ObjectEntry>();
        var seenPrefixes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stored in _objects.Keys.Where(candidate =>
                     candidate.StartsWith($"{container}/{prefix}", StringComparison.Ordinal))
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
                $"etag-{stored.Length}"));
        }

        return Task.FromResult(SftpResult<ObjectStoragePage>.Success(new ObjectStoragePage(entries)));
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
            return Task.FromResult(_containers.Contains(container)
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
                $"etag-{stored.Length}")));
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

        _objects[marker] = [];
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
        }

        // The marker last, so an interrupted delete leaves a visible folder rather than an invisible one.
        _ = _objects.TryRemove(marker, out _);
        return Task.FromResult(SftpResult.Success());
    }

    public Task<SftpResult<Stream>> OpenReadAsync(
        string path,
        ObjectReadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var (container, key) = ObjectStoragePath.Split(path);
        var stored = $"{container}/{key}";
        if (container is null || !_objects.TryGetValue(stored, out var content))
        {
            return Task.FromResult(SftpResult<Stream>.Fail(SftpError.NotFound, $"'{path}' was not found."));
        }

        if (options?.IfMatchETag is { Length: > 0 } etag &&
            !string.Equals(etag, $"etag-{stored.Length}", StringComparison.Ordinal))
        {
            return Task.FromResult(SftpResult<Stream>.Fail(
                SftpError.PreconditionFailed,
                $"'{path}' changed while it was being read."));
        }

        var offset = (int)(options?.Offset ?? 0);
        if (offset > content.Length)
        {
            return Task.FromResult(SftpResult<Stream>.Fail(SftpError.InvalidPath, "The range is out of bounds."));
        }

        var length = (int)Math.Min(options?.Length ?? (content.Length - offset), content.Length - offset);
        return Task.FromResult(SftpResult<Stream>.Success(
            new MemoryStream(content.AsSpan(offset, length).ToArray(), writable: false)));
    }

    public async Task<SftpResult<ObjectEntry>> WriteAsync(
        string path,
        Stream content,
        long? length = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var (container, key) = ObjectStoragePath.Split(path);
        if (container is null || key.Length == 0)
        {
            return SftpResult<ObjectEntry>.Fail(SftpError.InvalidPath, $"'{path}' does not name an object.");
        }

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        var stored = $"{container}/{key}";
        _ = _containers.Add(container);
        _objects[stored] = buffer.ToArray();
        return SftpResult<ObjectEntry>.Success(new ObjectEntry(
            ObjectStoragePath.GetName(key),
            path,
            ObjectEntryKind.Object,
            _objects[stored].Length,
            null,
            $"etag-{stored.Length}"));
    }

    public Task<SftpResult<IObjectUpload>> StartUploadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var (container, key) = ObjectStoragePath.Split(path);
        return Task.FromResult(container is null || key.Length == 0
            ? SftpResult<IObjectUpload>.Fail(SftpError.InvalidPath, $"'{path}' does not name an object.")
            : SftpResult<IObjectUpload>.Success(new InMemoryObjectUpload(this, path, AbortIsNoOp)));
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    private sealed class InMemoryObjectUpload(InMemoryObjectStorage store, string path, bool abortIsNoOp)
        : IObjectUpload
    {
        private readonly SortedDictionary<int, byte[]> _parts = [];

        public long MinimumPartSize => 5L * 1024 * 1024;

        public long MaximumPartSize => 5L * 1024 * 1024 * 1024;

        public int MaximumPartCount => 10_000;

        public async Task<SftpResult> UploadPartAsync(
            int partNumber,
            long length,
            Func<CancellationToken, ValueTask<Stream>> content,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(content);
            // Taken as a factory on purpose: a retried part needs a fresh stream, because the failed
            // attempt has already consumed the one it was handed.
            var stream = await content(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                _parts[partNumber] = buffer.ToArray();
            }

            return SftpResult.Success();
        }

        public async Task<SftpResult<ObjectEntry>> CompleteAsync(CancellationToken cancellationToken = default)
        {
            using var joined = new MemoryStream();
            foreach (var part in _parts.Values)
            {
                await joined.WriteAsync(part, cancellationToken).ConfigureAwait(false);
            }

            joined.Position = 0;
            return await store.WriteAsync(path, joined, joined.Length, cancellationToken).ConfigureAwait(false);
        }

        public Task<SftpResult> AbortAsync(CancellationToken cancellationToken = default)
        {
            if (!abortIsNoOp)
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
