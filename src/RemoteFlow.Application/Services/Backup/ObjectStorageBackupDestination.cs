using RemoteFlow.Application.Abstractions.Backup;
using RemoteFlow.Application.Abstractions.Sftp;
using RemoteFlow.Application.Abstractions.Storage;

namespace RemoteFlow.Application.Services.Backup;

/// <summary>Writes automatic backups to an S3 bucket or Azure container. Unlike SFTP there is no atomic
/// publish to hide behind: an object appears when its upload completes. The engine aborts a multipart
/// upload that fails, so a torn transfer normally leaves nothing behind, and anything it does leave is
/// named <c>.part</c>-free only on success — retention still refuses to count what it cannot parse.</summary>
public sealed class ObjectStorageBackupDestination(
    IObjectStorageService storage,
    string remotePath,
    string stagingRoot,
    string description) : IAutoBackupDestination
{
    private readonly IObjectStorageService _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    private readonly string _remotePath = ObjectStoragePath.Normalize(remotePath);
    private readonly string _stagingRoot = stagingRoot ?? throw new ArgumentNullException(nameof(stagingRoot));

    public string Description { get; } = description ?? throw new ArgumentNullException(nameof(description));

    public string GetStagingPath(string fileName)
    {
        return Path.Combine(_stagingRoot, fileName);
    }

    public async Task<SftpResult> PublishAsync(
        string stagingPath,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        var engine = new ObjectStorageTransferEngine(_storage, AlwaysOverwriteConflictResolver.Instance);
        var result = await engine
            .UploadAsync(stagingPath, ObjectStoragePath.Combine(_remotePath, fileName), progress: null, cancellationToken)
            .ConfigureAwait(false);
        if (result.IsSuccess)
        {
            return SftpResult.Success();
        }

        var failure = result.Items.Select(item => item.Failure).FirstOrDefault(item => item is not null);
        return SftpResult.Fail(
            failure?.Error ?? SftpError.Unknown,
            $"The backup could not be uploaded to {Description}: {failure?.Message ?? "the transfer did not complete."}");
    }

    public async Task<SftpResult<IReadOnlyList<AutoBackupArchive>>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var archives = new List<AutoBackupArchive>();
        string? continuation = null;
        do
        {
            // Paged to exhaustion. Stopping at the first page would hide older archives from retention, so
            // they would accumulate for ever while the count looked correct.
            var page = await _storage
                .ListAsync(_remotePath, new ObjectStoragePaging { ContinuationToken = continuation }, cancellationToken)
                .ConfigureAwait(false);
            if (page.IsFailure)
            {
                return SftpResult<IReadOnlyList<AutoBackupArchive>>.Fail(
                    page.Failure.Error,
                    $"{Description} could not be listed: {page.Failure.Message}");
            }

            foreach (var entry in page.Value.Entries)
            {
                if (entry.Kind != ObjectEntryKind.Object || !AutoBackupNaming.TryParse(entry.Name, out var createdUtc))
                {
                    continue;
                }

                archives.Add(new AutoBackupArchive(entry.Name, entry.Path, createdUtc, entry.Size));
            }

            continuation = page.Value.ContinuationToken;
        }
        while (!string.IsNullOrEmpty(continuation));

        return SftpResult<IReadOnlyList<AutoBackupArchive>>.Success(archives);
    }

    public Task<SftpResult> DeleteAsync(
        AutoBackupArchive archive,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archive);
        return _storage.DeleteAsync(archive.Path, recursive: false, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        return _storage.DisposeAsync();
    }
}
