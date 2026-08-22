using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using RemoteFlow.Application.Abstractions.Sftp;
using RemoteFlow.Application.Abstractions.Storage;
using RemoteFlow.Domain.Enums;

namespace RemoteFlow.Infrastructure.Storage;

public sealed class S3ObjectStorageProvider : IObjectStorageProvider
{
    public ProtocolType Protocol => ProtocolType.S3;

    public SftpResult<IObjectStorageService> Create(ObjectStorageEndpoint endpoint, ReadOnlyMemory<char> secretKey)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (secretKey.IsEmpty)
        {
            return SftpResult<IObjectStorageService>.Fail(
                SftpError.PermissionDenied,
                "No secret access key is stored for this connection.");
        }

        // Never the SDK's own credential chain. A parameterless client would fall back to
        // ~/.aws/credentials, AWS_* environment variables and the EC2/ECS metadata endpoints, which
        // silently reaches past the access key the user actually entered.
        var credentials = new BasicAWSCredentials(endpoint.AccessKeyId, new string(secretKey.Span));

        // The SDK's own response log would carry request bodies and headers past RedactingLoggerProvider,
        // which cannot redact what it never sees.
        AWSConfigs.LoggingConfig.LogTo = LoggingOptions.None;
        AWSConfigs.LoggingConfig.LogResponses = ResponseLoggingOption.Never;
        AWSConfigs.LoggingConfig.LogMetrics = false;

        var configuration = new AmazonS3Config
        {
            ForcePathStyle = endpoint.UsePathStyleAddressing,
        };
        if (endpoint.ServiceUrl is { } serviceUrl)
        {
            // An S3-compatible endpoint still has to be signed for some region, and the SDK will not
            // invent one. MinIO and friends accept any value; us-east-1 is the conventional default.
            configuration.ServiceURL = serviceUrl.ToString();
            configuration.AuthenticationRegion = endpoint.Region is { Length: > 0 } signingRegion
                ? signingRegion
                : RegionEndpoint.USEast1.SystemName;
        }
        else
        {
            configuration.RegionEndpoint = endpoint.Region is { Length: > 0 } region
                ? RegionEndpoint.GetBySystemName(region)
                : RegionEndpoint.USEast1;
        }

        return SftpResult<IObjectStorageService>.Success(
            new S3ObjectStorageService(new AmazonS3Client(credentials, configuration), endpoint));
    }
}

/// <summary>The S3 half of <see cref="IObjectStorageService"/>. Buckets are never created or deleted from
/// here — see ADR-0019.</summary>
public sealed class S3ObjectStorageService(IAmazonS3 client, ObjectStorageEndpoint endpoint) : IObjectStorageService
{
    /// <summary>S3 refuses a batch delete of more than a thousand keys.</summary>
    private const int _deleteBatchSize = 1_000;

    private readonly IAmazonS3 _client = client ?? throw new ArgumentNullException(nameof(client));
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
                return await ListBucketsAsync(cancellationToken).ConfigureAwait(false);
            }

            var prefix = ObjectStoragePath.AsPrefix(key);
            var response = await _client.ListObjectsV2Async(
                new ListObjectsV2Request
                {
                    BucketName = container,
                    Prefix = prefix,
                    Delimiter = ObjectStoragePath.Separator.ToString(),
                    MaxKeys = pageSize,
                    ContinuationToken = paging?.ContinuationToken,
                },
                cancellationToken).ConfigureAwait(false);

            var entries = new List<ObjectEntry>();
            foreach (var commonPrefix in response.CommonPrefixes ?? [])
            {
                entries.Add(new ObjectEntry(
                    ObjectStoragePath.GetName(commonPrefix),
                    _endpoint.ToPath(container, commonPrefix),
                    ObjectEntryKind.Prefix,
                    0,
                    null,
                    null));
            }

            foreach (var item in response.S3Objects ?? [])
            {
                if (IsFolderMarker(item.Key, item.Size ?? 0, prefix))
                {
                    continue;
                }

                entries.Add(new ObjectEntry(
                    ObjectStoragePath.GetName(item.Key),
                    _endpoint.ToPath(container, item.Key),
                    ObjectEntryKind.Object,
                    item.Size ?? 0,
                    item.LastModified is { } modified ? new DateTimeOffset(modified.ToUniversalTime(), TimeSpan.Zero) : null,
                    Normalize(item.ETag)));
            }

            return SftpResult<ObjectStoragePage>.Success(new ObjectStoragePage(entries, response.NextContinuationToken));
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

        try
        {
            // A one-key listing rather than HeadBucket: it proves the bucket exists and that the caller
            // may list it, using no permission browsing does not already need.
            if (key.Length == 0)
            {
                _ = await _client.ListObjectsV2Async(
                    new ListObjectsV2Request { BucketName = container, MaxKeys = 1 },
                    cancellationToken).ConfigureAwait(false);
                return SftpResult<ObjectEntry?>.Success(new ObjectEntry(
                    container,
                    _endpoint.ToPath(container, string.Empty),
                    ObjectEntryKind.Container,
                    0,
                    null,
                    null));
            }

            var metadata = await _client.GetObjectMetadataAsync(
                new GetObjectMetadataRequest { BucketName = container, Key = key },
                cancellationToken).ConfigureAwait(false);
            return SftpResult<ObjectEntry?>.Success(new ObjectEntry(
                ObjectStoragePath.GetName(key),
                _endpoint.ToPath(container, key),
                ObjectEntryKind.Object,
                metadata.ContentLength,
                metadata.LastModified is { } modified ? new DateTimeOffset(modified.ToUniversalTime(), TimeSpan.Zero) : null,
                Normalize(metadata.ETag)));
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // 404 on the key itself does not settle it: the same name may exist as a prefix, with or
            // without a marker object under it.
            return await StatPrefixAsync(container, key, cancellationToken).ConfigureAwait(false);
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
                "RemoteFlow does not create buckets. Create it in the AWS console, where its region, public-access and versioning settings are decided.");
        }

        var prefix = ObjectStoragePath.AsPrefix(key);
        try
        {
            var existing = await _client.ListObjectsV2Async(
                new ListObjectsV2Request { BucketName = container, Prefix = prefix, MaxKeys = 1 },
                cancellationToken).ConfigureAwait(false);
            if (existing.S3Objects?.Count > 0)
            {
                return SftpResult.Fail(SftpError.AlreadyExists, $"'{prefix}' already exists.");
            }

            // A zero-byte object at "{key}/". Both vendors' consoles do exactly this; a placeholder that
            // only exists in the client evaporates on the next refresh.
            _ = await _client.PutObjectAsync(
                new PutObjectRequest
                {
                    BucketName = container,
                    Key = prefix,
                    InputStream = new MemoryStream([]),
                },
                cancellationToken).ConfigureAwait(false);
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
                "RemoteFlow does not delete buckets. Delete it in the AWS console.");
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

            if (stat.Value.Kind == ObjectEntryKind.Object)
            {
                _ = await _client.DeleteObjectAsync(container, key, cancellationToken).ConfigureAwait(false);
                return SftpResult.Success();
            }

            return await DeletePrefixAsync(container, key, recursive, cancellationToken).ConfigureAwait(false);
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
            return SftpResult<Stream>.Fail(SftpError.InvalidPath, $"'{path}' does not name an object.");
        }

        try
        {
            var request = new GetObjectRequest { BucketName = container, Key = key };
            if (options is { Length: > 0 } range)
            {
                request.ByteRange = new ByteRange(range.Offset, range.Offset + range.Length.Value - 1);
            }
            else if (options is { Offset: > 0 })
            {
                request.ByteRange = new ByteRange($"bytes={options.Offset}-");
            }

            if (options?.IfMatchETag is { Length: > 0 } etag)
            {
                request.EtagToMatch = etag;
            }

            var response = await _client.GetObjectAsync(request, cancellationToken).ConfigureAwait(false);
            return SftpResult<Stream>.Success(new ResponseOwnedStream(response.ResponseStream, response));
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
            return SftpResult<ObjectEntry>.Fail(SftpError.InvalidPath, $"'{path}' does not name an object.");
        }

        try
        {
            var request = new PutObjectRequest
            {
                BucketName = container,
                Key = key,
                InputStream = content,
                AutoCloseStream = false,
            };
            if (length is { } contentLength)
            {
                request.Headers.ContentLength = contentLength;
            }

            var response = await _client.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);
            return SftpResult<ObjectEntry>.Success(new ObjectEntry(
                ObjectStoragePath.GetName(key),
                _endpoint.ToPath(container, key),
                ObjectEntryKind.Object,
                length ?? (content.CanSeek ? content.Length : 0),
                null,
                Normalize(response.ETag)));
        }
        catch (Exception exception)
        {
            return Failed<ObjectEntry>(exception, cancellationToken);
        }
    }

    public async Task<SftpResult<IObjectUpload>> StartUploadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var resolved = _endpoint.Resolve(path);
        if (resolved.IsFailure)
        {
            return SftpResult<IObjectUpload>.Fail(resolved.Failure.Error, resolved.Failure.Message);
        }

        var (container, key) = resolved.Value;
        if (container is null || key.Length == 0)
        {
            return SftpResult<IObjectUpload>.Fail(SftpError.InvalidPath, $"'{path}' does not name an object.");
        }

        try
        {
            var initiated = await _client.InitiateMultipartUploadAsync(
                new InitiateMultipartUploadRequest { BucketName = container, Key = key },
                cancellationToken).ConfigureAwait(false);
            return SftpResult<IObjectUpload>.Success(new S3ObjectUpload(
                _client,
                container,
                key,
                initiated.UploadId,
                _endpoint.ToPath(container, key)));
        }
        catch (Exception exception)
        {
            return Failed<IObjectUpload>(exception, cancellationToken);
        }
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>Every folder RemoteFlow, the AWS console or Storage Explorer creates is a zero-byte object
    /// whose key ends in a separator, and the listed prefix itself comes back as an object when a marker
    /// exists for it. Without dropping both, every folder appears twice — once as a folder and once as an
    /// empty file.</summary>
    private static bool IsFolderMarker(string key, long size, string prefix)
    {
        return string.Equals(key, prefix, StringComparison.Ordinal) ||
            (size == 0 && key.EndsWith(ObjectStoragePath.Separator));
    }

    private static int ClampPageSize(ObjectStoragePaging? paging)
    {
        var requested = paging?.PageSize ?? ObjectStoragePaging.DefaultPageSize;
        return Math.Clamp(requested, 1, ObjectStoragePaging.MaximumPageSize);
    }

    private static string? Normalize(string? etag)
    {
        return string.IsNullOrEmpty(etag) ? null : etag.Trim('"');
    }

    private static SftpResult<T> Failed<T>(Exception exception, CancellationToken cancellationToken)
    {
        var failure = ObjectStorageErrorMap.FromException(exception, cancellationToken);
        return SftpResult<T>.Fail(failure.Error, failure.Message);
    }

    private async Task<SftpResult<ObjectStoragePage>> ListBucketsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await _client.ListBucketsAsync(cancellationToken).ConfigureAwait(false);
            var entries = (response.Buckets ?? [])
                .Select(bucket => new ObjectEntry(
                    bucket.BucketName,
                    _endpoint.ToPath(bucket.BucketName, string.Empty),
                    ObjectEntryKind.Container,
                    0,
                    bucket.CreationDate is { } created
                        ? new DateTimeOffset(created.ToUniversalTime(), TimeSpan.Zero)
                        : null,
                    null))
                .ToArray();
            return SftpResult<ObjectStoragePage>.Success(new ObjectStoragePage(entries));
        }
        catch (AmazonS3Exception exception) when (
            exception.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized)
        {
            // A key scoped to one bucket is normal in production, and the remedy is a connection setting
            // rather than a wider key.
            return SftpResult<ObjectStoragePage>.Fail(
                SftpError.PermissionDenied,
                "This key cannot list buckets. Set a bucket name on the connection to browse it directly.");
        }
    }

    private async Task<SftpResult<ObjectEntry?>> StatPrefixAsync(
        string container,
        string key,
        CancellationToken cancellationToken)
    {
        var prefix = ObjectStoragePath.AsPrefix(key);
        try
        {
            var listing = await _client.ListObjectsV2Async(
                new ListObjectsV2Request { BucketName = container, Prefix = prefix, MaxKeys = 1 },
                cancellationToken).ConfigureAwait(false);
            return SftpResult<ObjectEntry?>.Success(listing.S3Objects?.Count > 0
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

    private async Task<SftpResult> DeletePrefixAsync(
        string container,
        string key,
        bool recursive,
        CancellationToken cancellationToken)
    {
        var prefix = ObjectStoragePath.AsPrefix(key);
        if (!recursive)
        {
            var probe = await _client.ListObjectsV2Async(
                new ListObjectsV2Request { BucketName = container, Prefix = prefix, MaxKeys = 2 },
                cancellationToken).ConfigureAwait(false);
            var others = (probe.S3Objects ?? [])
                .Count(item => !string.Equals(item.Key, prefix, StringComparison.Ordinal));
            if (others > 0)
            {
                return SftpResult.Fail(
                    SftpError.NotSupported,
                    "The folder is not empty. Delete it recursively.");
            }

            _ = await _client.DeleteObjectAsync(container, prefix, cancellationToken).ConfigureAwait(false);
            return SftpResult.Success();
        }

        string? continuation = null;
        do
        {
            var listing = await _client.ListObjectsV2Async(
                new ListObjectsV2Request
                {
                    BucketName = container,
                    Prefix = prefix,
                    MaxKeys = _deleteBatchSize,
                    ContinuationToken = continuation,
                },
                cancellationToken).ConfigureAwait(false);
            continuation = listing.NextContinuationToken;

            // The marker goes last, in its own request, so an interrupted delete leaves a visible empty
            // folder rather than an invisible half-deleted one.
            var keys = (listing.S3Objects ?? [])
                .Where(item => !string.Equals(item.Key, prefix, StringComparison.Ordinal))
                .Select(item => new KeyVersion { Key = item.Key })
                .ToList();
            if (keys.Count > 0)
            {
                var deleted = await _client.DeleteObjectsAsync(
                    new DeleteObjectsRequest { BucketName = container, Objects = keys },
                    cancellationToken).ConfigureAwait(false);
                if (deleted.DeleteErrors?.Count > 0)
                {
                    var first = deleted.DeleteErrors[0];
                    return SftpResult.Fail(
                        SftpError.PermissionDenied,
                        $"'{first.Key}' could not be deleted: {first.Message}");
                }
            }
        }
        while (!string.IsNullOrEmpty(continuation));

        _ = await _client.DeleteObjectAsync(container, prefix, cancellationToken).ConfigureAwait(false);
        return SftpResult.Success();
    }
}
