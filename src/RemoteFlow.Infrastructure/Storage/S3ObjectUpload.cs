using Amazon.S3;
using Amazon.S3.Model;
using RemoteFlow.Application.Abstractions.Sftp;
using RemoteFlow.Application.Abstractions.Storage;

namespace RemoteFlow.Infrastructure.Storage;

/// <summary>One S3 multipart upload. Part content is taken as a factory rather than a stream so a retried
/// part gets a fresh one; nothing here buffers a whole part.</summary>
public sealed class S3ObjectUpload(
    IAmazonS3 client,
    string bucketName,
    string key,
    string uploadId,
    string path) : IObjectUpload
{
    private readonly IAmazonS3 _client = client ?? throw new ArgumentNullException(nameof(client));
    private readonly List<PartETag> _parts = [];
    private readonly Lock _gate = new();
    private bool _finished;

    /// <summary>S3 rejects any part but the last below 5 MiB.</summary>
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
        ArgumentOutOfRangeException.ThrowIfLessThan(partNumber, 1);
        if (partNumber > MaximumPartCount)
        {
            return SftpResult.Fail(
                SftpError.QuotaExceeded,
                $"S3 accepts at most {MaximumPartCount} parts per upload.");
        }

        try
        {
            var stream = await content(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var response = await _client.UploadPartAsync(
                    new UploadPartRequest
                    {
                        BucketName = bucketName,
                        Key = key,
                        UploadId = uploadId,
                        PartNumber = partNumber,
                        PartSize = length,
                        InputStream = stream,
                    },
                    cancellationToken).ConfigureAwait(false);
                lock (_gate)
                {
                    _ = _parts.RemoveAll(part => part.PartNumber == partNumber);
                    _parts.Add(new PartETag(partNumber, response.ETag));
                }
            }

            return SftpResult.Success();
        }
        catch (Exception exception)
        {
            var failure = ObjectStorageErrorMap.FromException(exception, cancellationToken);
            return SftpResult.Fail(failure.Error, failure.Message);
        }
    }

    public async Task<SftpResult<ObjectEntry>> CompleteAsync(CancellationToken cancellationToken = default)
    {
        List<PartETag> parts;
        lock (_gate)
        {
            parts = [.. _parts.OrderBy(part => part.PartNumber)];
        }

        try
        {
            var response = await _client.CompleteMultipartUploadAsync(
                new CompleteMultipartUploadRequest
                {
                    BucketName = bucketName,
                    Key = key,
                    UploadId = uploadId,
                    PartETags = parts,
                },
                cancellationToken).ConfigureAwait(false);
            _finished = true;
            return SftpResult<ObjectEntry>.Success(new ObjectEntry(
                ObjectStoragePath.GetName(key),
                path,
                ObjectEntryKind.Object,
                0,
                null,
                response.ETag?.Trim('"')));
        }
        catch (Exception exception)
        {
            var failure = ObjectStorageErrorMap.FromException(exception, cancellationToken);
            return SftpResult<ObjectEntry>.Fail(failure.Error, failure.Message);
        }
    }

    public async Task<SftpResult> AbortAsync(CancellationToken cancellationToken = default)
    {
        if (_finished)
        {
            return SftpResult.Success();
        }

        try
        {
            // Not best-effort decoration: an S3 multipart upload that is never aborted keeps billing for
            // its uploaded parts until a lifecycle rule removes them.
            _ = await _client.AbortMultipartUploadAsync(
                bucketName,
                key,
                uploadId,
                cancellationToken).ConfigureAwait(false);
            _finished = true;
            return SftpResult.Success();
        }
        catch (Exception exception)
        {
            var failure = ObjectStorageErrorMap.FromException(exception, cancellationToken);
            return SftpResult.Fail(failure.Error, failure.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _ = await AbortAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
