using System.Globalization;
using System.Text;
using Azure.Storage.Blobs.Specialized;
using RemoteFlow.Application.Abstractions.Sftp;
using RemoteFlow.Application.Abstractions.Storage;

namespace RemoteFlow.Infrastructure.Storage;

/// <summary>One Azure block-blob upload: staged blocks plus a single commit. Part content is taken as a
/// factory rather than a stream so a retried block gets a fresh one; nothing here buffers a whole block.
/// </summary>
public sealed class AzureBlockBlobUpload(BlockBlobClient blob, string path) : IObjectUpload
{
    private readonly BlockBlobClient _blob = blob ?? throw new ArgumentNullException(nameof(blob));
    private readonly SortedDictionary<int, string> _blocks = [];
    private readonly Lock _gate = new();

    /// <summary>Azure imposes no minimum block size. One byte is a legal block.</summary>
    public long MinimumPartSize => 1;

    public long MaximumPartSize => _blob.BlockBlobMaxStageBlockLongBytes;

    public int MaximumPartCount => _blob.BlockBlobMaxBlocks;

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
                $"Azure Blob accepts at most {MaximumPartCount.ToString(CultureInfo.InvariantCulture)} blocks per blob.");
        }

        var blockId = BuildBlockId(partNumber);
        try
        {
            var stream = await content(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                _ = await _blob.StageBlockAsync(blockId, stream, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }

            lock (_gate)
            {
                _blocks[partNumber] = blockId;
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
        string[] blockIds;
        lock (_gate)
        {
            blockIds = [.. _blocks.Values];
        }

        try
        {
            var response = await _blob.CommitBlockListAsync(blockIds, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return SftpResult<ObjectEntry>.Success(new ObjectEntry(
                ObjectStoragePath.GetName(_blob.Name),
                path,
                ObjectEntryKind.Object,
                0,
                response.Value.LastModified.ToUniversalTime(),
                response.Value.ETag.ToString().Trim('"')));
        }
        catch (Exception exception)
        {
            var failure = ObjectStorageErrorMap.FromException(exception, cancellationToken);
            return SftpResult<ObjectEntry>.Fail(failure.Error, failure.Message);
        }
    }

    /// <summary>A no-op, and reported as success. Azure has no abort call: an uncommitted block list is
    /// invisible, is not billed, and the service garbage-collects it after seven days.</summary>
    public Task<SftpResult> AbortAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _blocks.Clear();
        }

        return Task.FromResult(SftpResult.Success());
    }

    public async ValueTask DisposeAsync()
    {
        _ = await AbortAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Block IDs must be base64 and all the same length within one blob, so the part number is
    /// zero-padded before encoding.</summary>
    private static string BuildBlockId(int partNumber)
    {
        return Convert.ToBase64String(
            Encoding.ASCII.GetBytes(partNumber.ToString("D8", CultureInfo.InvariantCulture)));
    }
}
