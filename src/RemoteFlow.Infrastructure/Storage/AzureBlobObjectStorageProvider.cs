using Azure;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using RemoteFlow.Application.Abstractions.Sftp;
using RemoteFlow.Application.Abstractions.Storage;
using RemoteFlow.Domain.Enums;

namespace RemoteFlow.Infrastructure.Storage;

public sealed class AzureBlobObjectStorageProvider : IObjectStorageProvider
{
    public ProtocolType Protocol => ProtocolType.AzureBlob;

    public SftpResult<IObjectStorageService> Create(ObjectStorageEndpoint endpoint, ReadOnlyMemory<char> secretKey)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (secretKey.IsEmpty)
        {
            return SftpResult<IObjectStorageService>.Fail(
                SftpError.PermissionDenied,
                "No account key is stored for this connection.");
        }

        // The endpoint is the connection's own host, so a sovereign-cloud account works by hand-editing
        // the host box rather than by adding a field nobody else needs.
        var serviceUri = endpoint.ServiceUrl ?? new Uri($"https://{endpoint.Host}");
        var options = new BlobClientOptions();

        // Azure's own diagnostics travel over EventSource, which RedactingLoggerProvider cannot see and
        // therefore cannot redact. Nothing installs an AzureEventSourceListener either.
        options.Diagnostics.IsLoggingEnabled = false;
        options.Diagnostics.IsLoggingContentEnabled = false;
        options.Diagnostics.IsTelemetryEnabled = false;
        options.Diagnostics.IsDistributedTracingEnabled = false;

        // An Azure account key is base64, and the credential constructor throws a bare FormatException on
        // anything else. A mistyped key is a thing users do; an unhandled exception out of here is not an
        // acceptable answer to it.
        StorageSharedKeyCredential credential;
        try
        {
            credential = new StorageSharedKeyCredential(endpoint.AccessKeyId, new string(secretKey.Span));
        }
        catch (FormatException)
        {
            return SftpResult<IObjectStorageService>.Fail(
                SftpError.PermissionDenied,
                "The stored account key is not a valid Azure storage key. Copy it again from the portal, under Access keys.");
        }

        return SftpResult<IObjectStorageService>.Success(new AzureBlobObjectStorageService(
            new BlobServiceClient(serviceUri, credential, options),
            endpoint));
    }
}

/// <summary>The Azure Blob half of <see cref="IObjectStorageService"/>. Containers are never created or
/// deleted from here — a mis-created public container is a security incident, not a UX regression.
/// </summary>
public sealed class AzureBlobObjectStorageService(BlobServiceClient client, ObjectStorageEndpoint endpoint)
    : IObjectStorageService
{
    /// <summary>How many blob deletes to have in flight at once. Azure has no batch-delete equivalent of
    /// S3's thousand-key request on this client, so recursion is bounded concurrency instead.</summary>
    private const int _deleteConcurrency = 8;

    private readonly BlobServiceClient _client = client ?? throw new ArgumentNullException(nameof(client));
    private readonly ObjectStorageEndpoint _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));

    public async Task<SftpResult<ObjectStoragePage>> ListAsync(
        string path,
        ObjectStoragePaging? paging = null,
        CancellationToken cancellationToken = default)
    {
        var resolved = _endpoint.Resolve(path);
        if (resolved.IsFailure)
        {
            return SftpResult<ObjectStoragePage>.Fail(resolved.Failure.Error, resolved.Failure.Message);
        }

        var (container, key) = resolved.Value;
        var pageSize = ClampPageSize(paging);
        try
        {
            if (container is null)
            {
                return await ListContainersAsync(pageSize, paging?.ContinuationToken, cancellationToken)
                    .ConfigureAwait(false);
            }

            var prefix = ObjectStoragePath.AsPrefix(key);
            var entries = new List<ObjectEntry>();
            var pages = _client.GetBlobContainerClient(container)
                .GetBlobsByHierarchyAsync(
                    new GetBlobsByHierarchyOptions
                    {
                        Delimiter = ObjectStoragePath.Separator.ToString(),
                        Prefix = prefix + paging?.NamePrefix,
                    },
                    cancellationToken)
                .AsPages(paging?.ContinuationToken, pageSize);

            await foreach (var page in pages.ConfigureAwait(false))
            {
                foreach (var item in page.Values)
                {
                    if (item.IsPrefix)
                    {
                        entries.Add(new ObjectEntry(
                            ObjectStoragePath.GetName(item.Prefix),
                            _endpoint.ToPath(container, item.Prefix),
                            ObjectEntryKind.Prefix,
                            0,
                            null,
                            null));
                        continue;
                    }

                    var size = item.Blob.Properties.ContentLength ?? 0;
                    if (IsFolderMarker(item.Blob.Name, size, prefix))
                    {
                        continue;
                    }

                    entries.Add(new ObjectEntry(
                        ObjectStoragePath.GetName(item.Blob.Name),
                        _endpoint.ToPath(container, item.Blob.Name),
                        ObjectEntryKind.Object,
                        size,
                        item.Blob.Properties.LastModified?.ToUniversalTime(),
                        item.Blob.Properties.ETag?.ToString().Trim('"')));
                }

                return SftpResult<ObjectStoragePage>.Success(new ObjectStoragePage(entries, page.ContinuationToken));
            }

            return SftpResult<ObjectStoragePage>.Success(new ObjectStoragePage(entries));
        }
        catch (Exception exception)
        {
            return Failed<ObjectStoragePage>(exception, cancellationToken);
        }
    }

    public async Task<SftpResult<ObjectEntry?>> StatAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var resolved = _endpoint.Resolve(path);
        if (resolved.IsFailure)
        {
            return SftpResult<ObjectEntry?>.Fail(resolved.Failure.Error, resolved.Failure.Message);
        }

        var (container, key) = resolved.Value;
        if (container is null)
        {
            return SftpResult<ObjectEntry?>.Success(
                new ObjectEntry(ObjectStoragePath.Root, ObjectStoragePath.Root, ObjectEntryKind.Prefix, 0, null, null));
        }

        var containerClient = _client.GetBlobContainerClient(container);
        try
        {
            // A one-blob listing rather than GetProperties on the container: it proves existence and list
            // permission together, using no permission browsing does not already need.
            if (key.Length == 0)
            {
                await foreach (var _ in containerClient
                    .GetBlobsAsync(new GetBlobsOptions(), cancellationToken)
                    .AsPages(pageSizeHint: 1)
                    .ConfigureAwait(false))
                {
                    break;
                }

                return SftpResult<ObjectEntry?>.Success(new ObjectEntry(
                    container,
                    _endpoint.ToPath(container, string.Empty),
                    ObjectEntryKind.Container,
                    0,
                    null,
                    null));
            }

            var properties = await containerClient.GetBlobClient(key)
                .GetPropertiesAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return SftpResult<ObjectEntry?>.Success(new ObjectEntry(
                ObjectStoragePath.GetName(key),
                _endpoint.ToPath(container, key),
                ObjectEntryKind.Object,
                properties.Value.ContentLength,
                properties.Value.LastModified.ToUniversalTime(),
                properties.Value.ETag.ToString().Trim('"')));
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return await StatPrefixAsync(containerClient, container, key, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return Failed<ObjectEntry?>(exception, cancellationToken);
        }
    }

    public async Task<SftpResult> CreateFolderAsync(string path, CancellationToken cancellationToken = default)
    {
        var resolved = _endpoint.Resolve(path);
        if (resolved.IsFailure)
        {
            return SftpResult.Fail(resolved.Failure.Error, resolved.Failure.Message);
        }

        var (container, key) = resolved.Value;
        if (container is null || key.Length == 0)
        {
            return SftpResult.Fail(
                SftpError.NotSupported,
                "RemoteFlow does not create containers. Create it in the Azure portal, where its public-access level is decided.");
        }

        var prefix = ObjectStoragePath.AsPrefix(key);
        try
        {
            var containerClient = _client.GetBlobContainerClient(container);
            if (await AnyBlobAsync(containerClient, prefix, cancellationToken).ConfigureAwait(false))
            {
                return SftpResult.Fail(SftpError.AlreadyExists, $"'{prefix}' already exists.");
            }

            _ = await containerClient.GetBlobClient(prefix)
                .UploadAsync(new MemoryStream([]), overwrite: false, cancellationToken)
                .ConfigureAwait(false);
            return SftpResult.Success();
        }
        catch (Exception exception)
        {
            var failure = ObjectStorageErrorMap.FromException(exception, cancellationToken);
            return SftpResult.Fail(failure.Error, failure.Message);
        }
    }

    public async Task<SftpResult> DeleteAsync(
        string path,
        bool recursive,
        CancellationToken cancellationToken = default)
    {
        var resolved = _endpoint.Resolve(path);
        if (resolved.IsFailure)
        {
            return SftpResult.Fail(resolved.Failure.Error, resolved.Failure.Message);
        }

        var (container, key) = resolved.Value;
        if (container is null || key.Length == 0)
        {
            return SftpResult.Fail(
                SftpError.NotSupported,
                "RemoteFlow does not delete containers. Delete it in the Azure portal.");
        }

        try
        {
            var stat = await StatAsync(path, cancellationToken).ConfigureAwait(false);
            if (stat.IsFailure)
            {
                return SftpResult.Fail(stat.Failure.Error, stat.Failure.Message);
            }

            if (stat.Value is null)
            {
                return SftpResult.Fail(SftpError.NotFound, $"'{path}' was not found.");
            }

            var containerClient = _client.GetBlobContainerClient(container);
            if (stat.Value.Kind == ObjectEntryKind.Object)
            {
                _ = await containerClient.DeleteBlobAsync(key, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                return SftpResult.Success();
            }

            return await DeletePrefixAsync(containerClient, key, recursive, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var failure = ObjectStorageErrorMap.FromException(exception, cancellationToken);
            return SftpResult.Fail(failure.Error, failure.Message);
        }
    }

    public async Task<SftpResult<Stream>> OpenReadAsync(
        string path,
        ObjectReadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var resolved = _endpoint.Resolve(path);
        if (resolved.IsFailure)
        {
            return SftpResult<Stream>.Fail(resolved.Failure.Error, resolved.Failure.Message);
        }

        var (container, key) = resolved.Value;
        if (container is null || key.Length == 0)
        {
            return SftpResult<Stream>.Fail(SftpError.InvalidPath, $"'{path}' does not name a blob.");
        }

        try
        {
            var download = new BlobDownloadOptions();
            if (options is { Length: > 0 } range)
            {
                download.Range = new HttpRange(range.Offset, range.Length);
            }
            else if (options is { Offset: > 0 })
            {
                download.Range = new HttpRange(options.Offset);
            }

            if (options?.IfMatchETag is { Length: > 0 } etag)
            {
                download.Conditions = new BlobRequestConditions { IfMatch = new ETag(etag) };
            }

            var response = await _client.GetBlobContainerClient(container)
                .GetBlobClient(key)
                .DownloadStreamingAsync(download, cancellationToken)
                .ConfigureAwait(false);
            return SftpResult<Stream>.Success(new ResponseOwnedStream(response.Value.Content, response.Value));
        }
        catch (Exception exception)
        {
            return Failed<Stream>(exception, cancellationToken);
        }
    }

    public async Task<SftpResult<ObjectEntry>> WriteAsync(
        string path,
        Stream content,
        long? length = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var resolved = _endpoint.Resolve(path);
        if (resolved.IsFailure)
        {
            return SftpResult<ObjectEntry>.Fail(resolved.Failure.Error, resolved.Failure.Message);
        }

        var (container, key) = resolved.Value;
        if (container is null || key.Length == 0)
        {
            return SftpResult<ObjectEntry>.Fail(SftpError.InvalidPath, $"'{path}' does not name a blob.");
        }

        try
        {
            var response = await _client.GetBlobContainerClient(container)
                .GetBlobClient(key)
                .UploadAsync(content, overwrite: true, cancellationToken)
                .ConfigureAwait(false);
            return SftpResult<ObjectEntry>.Success(new ObjectEntry(
                ObjectStoragePath.GetName(key),
                _endpoint.ToPath(container, key),
                ObjectEntryKind.Object,
                length ?? (content.CanSeek ? content.Length : 0),
                response.Value.LastModified.ToUniversalTime(),
                response.Value.ETag.ToString().Trim('"')));
        }
        catch (Exception exception)
        {
            return Failed<ObjectEntry>(exception, cancellationToken);
        }
    }

    public Task<SftpResult<IObjectUpload>> StartUploadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var resolved = _endpoint.Resolve(path);
        if (resolved.IsFailure)
        {
            return Task.FromResult(SftpResult<IObjectUpload>.Fail(resolved.Failure.Error, resolved.Failure.Message));
        }

        var (container, key) = resolved.Value;
        if (container is null || key.Length == 0)
        {
            return Task.FromResult(SftpResult<IObjectUpload>.Fail(
                SftpError.InvalidPath,
                $"'{path}' does not name a blob."));
        }

        // Azure needs no round trip to begin: a block upload is staged blocks plus one commit, and an
        // uncommitted block list is not visible to anybody.
        var blockBlob = _client.GetBlobContainerClient(container).GetBlockBlobClient(key);
        return Task.FromResult(SftpResult<IObjectUpload>.Success(
            new AzureBlockBlobUpload(blockBlob, _endpoint.ToPath(container, key))));
    }

    public ValueTask DisposeAsync()
    {
        // BlobServiceClient holds no disposable state of its own; its pipeline is shared and pooled.
        return ValueTask.CompletedTask;
    }

    private static bool IsFolderMarker(string name, long size, string prefix)
    {
        return string.Equals(name, prefix, StringComparison.Ordinal) ||
            (size == 0 && name.EndsWith(ObjectStoragePath.Separator));
    }

    private static int ClampPageSize(ObjectStoragePaging? paging)
    {
        var requested = paging?.PageSize ?? ObjectStoragePaging.DefaultPageSize;
        return Math.Clamp(requested, 1, ObjectStoragePaging.MaximumPageSize);
    }

    private static SftpResult<T> Failed<T>(Exception exception, CancellationToken cancellationToken)
    {
        var failure = ObjectStorageErrorMap.FromException(exception, cancellationToken);
        return SftpResult<T>.Fail(failure.Error, failure.Message);
    }

    private static async Task<bool> AnyBlobAsync(
        BlobContainerClient container,
        string prefix,
        CancellationToken cancellationToken)
    {
        await foreach (var page in container
            .GetBlobsAsync(new GetBlobsOptions { Prefix = prefix }, cancellationToken)
            .AsPages(pageSizeHint: 1)
            .ConfigureAwait(false))
        {
            return page.Values.Count > 0;
        }

        return false;
    }

    private async Task<SftpResult<ObjectStoragePage>> ListContainersAsync(
        int pageSize,
        string? continuationToken,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var page in _client
                .GetBlobContainersAsync(BlobContainerTraits.None, cancellationToken: cancellationToken)
                .AsPages(continuationToken, pageSize)
                .ConfigureAwait(false))
            {
                var entries = page.Values
                    .Select(item => new ObjectEntry(
                        item.Name,
                        _endpoint.ToPath(item.Name, string.Empty),
                        ObjectEntryKind.Container,
                        0,
                        item.Properties?.LastModified.ToUniversalTime(),
                        null))
                    .ToArray();
                return SftpResult<ObjectStoragePage>.Success(new ObjectStoragePage(entries, page.ContinuationToken));
            }

            return SftpResult<ObjectStoragePage>.Success(new ObjectStoragePage([]));
        }
        catch (RequestFailedException exception) when (exception.Status is 401 or 403)
        {
            return SftpResult<ObjectStoragePage>.Fail(
                SftpError.PermissionDenied,
                "This key cannot list containers. Set a container name on the connection to browse it directly.");
        }
    }

    private async Task<SftpResult<ObjectEntry?>> StatPrefixAsync(
        BlobContainerClient containerClient,
        string container,
        string key,
        CancellationToken cancellationToken)
    {
        var prefix = ObjectStoragePath.AsPrefix(key);
        try
        {
            return SftpResult<ObjectEntry?>.Success(
                await AnyBlobAsync(containerClient, prefix, cancellationToken).ConfigureAwait(false)
                    ? new ObjectEntry(
                        ObjectStoragePath.GetName(key),
                        _endpoint.ToPath(container, key),
                        ObjectEntryKind.Prefix,
                        0,
                        null,
                        null)
                    : null);
        }
        catch (Exception exception)
        {
            return Failed<ObjectEntry?>(exception, cancellationToken);
        }
    }

    private static async Task<SftpResult> DeletePrefixAsync(
        BlobContainerClient containerClient,
        string key,
        bool recursive,
        CancellationToken cancellationToken)
    {
        var prefix = ObjectStoragePath.AsPrefix(key);
        var names = new List<string>();
        await foreach (var blob in containerClient
            .GetBlobsAsync(new GetBlobsOptions { Prefix = prefix }, cancellationToken)
            .ConfigureAwait(false))
        {
            if (!string.Equals(blob.Name, prefix, StringComparison.Ordinal))
            {
                names.Add(blob.Name);
            }
        }

        if (!recursive)
        {
            if (names.Count > 0)
            {
                return SftpResult.Fail(SftpError.NotSupported, "The folder is not empty. Delete it recursively.");
            }

            _ = await containerClient.DeleteBlobIfExistsAsync(prefix, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return SftpResult.Success();
        }

        await Parallel.ForEachAsync(
            names,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = _deleteConcurrency,
                CancellationToken = cancellationToken,
            },
            async (name, token) => _ = await containerClient
                .DeleteBlobIfExistsAsync(name, cancellationToken: token)
                .ConfigureAwait(false)).ConfigureAwait(false);

        // The marker last, so an interrupted delete leaves a visible empty folder rather than an
        // invisible half-deleted one.
        _ = await containerClient.DeleteBlobIfExistsAsync(prefix, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return SftpResult.Success();
    }
}
