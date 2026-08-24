using RemoteFlow.Application.Abstractions.Backup;
using RemoteFlow.Application.Abstractions.Sftp;
using RemoteFlow.Application.Abstractions.Ssh;

namespace RemoteFlow.Application.Services.Backup;

/// <summary>Writes automatic backups over SFTP, holding the SSH connection open for the length of one run.
/// Uploading goes through <see cref="TransferEngine"/> rather than the raw stream API so it inherits the
/// atomic publish already built there: bytes land under a temporary name and are renamed into place.</summary>
public sealed class SftpBackupDestination(
    ISshConnection connection,
    ISftpService sftp,
    string remotePath,
    string stagingRoot,
    string description) : IAutoBackupDestination
{
    private readonly ISshConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    private readonly ISftpService _sftp = sftp ?? throw new ArgumentNullException(nameof(sftp));
    private readonly string _remotePath = SftpPath.Normalize(remotePath);
    private readonly string _stagingRoot = stagingRoot ?? throw new ArgumentNullException(nameof(stagingRoot));

    public string Description { get; } = description ?? throw new ArgumentNullException(nameof(description));

    /// <summary>Remote destinations build the archive in the cache directory. It is reproducible and safe
    /// to lose, and a half-written archive holds nothing that is not already in the database.</summary>
    public string GetStagingPath(string fileName)
    {
        return Path.Combine(_stagingRoot, fileName);
    }

    public async Task<SftpResult> PublishAsync(
        string stagingPath,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        var ensured = await EnsureDirectoryAsync(cancellationToken).ConfigureAwait(false);
        if (ensured.IsFailure)
        {
            return ensured;
        }

        using var engine = new TransferEngine(_sftp, AlwaysOverwriteConflictResolver.Instance);
        var result = await engine
            .UploadAsync(stagingPath, SftpPath.Combine(_remotePath, fileName), progress: null, cancellationToken)
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
        var listed = await _sftp.ListAsync(_remotePath, cancellationToken).ConfigureAwait(false);
        if (listed.IsFailure)
        {
            return SftpResult<IReadOnlyList<AutoBackupArchive>>.Fail(
                listed.Failure.Error,
                $"{Description} could not be listed: {listed.Failure.Message}");
        }

        var archives = new List<AutoBackupArchive>();
        foreach (var entry in listed.Value)
        {
            // Directories are skipped before parsing: a directory that happened to be named like an archive
            // would otherwise become a deletion candidate.
            if (entry.IsDirectory || !AutoBackupNaming.TryParse(entry.Name, out var createdUtc))
            {
                continue;
            }

            archives.Add(new AutoBackupArchive(entry.Name, entry.FullPath, createdUtc, entry.Size));
        }

        return SftpResult<IReadOnlyList<AutoBackupArchive>>.Success(archives);
    }

    public Task<SftpResult> DeleteAsync(
        AutoBackupArchive archive,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archive);
        return _sftp.DeleteAsync(archive.Path, recursive: false, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _sftp.DisposeAsync().ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    private async Task<SftpResult> EnsureDirectoryAsync(CancellationToken cancellationToken)
    {
        // A backup destination that has to be created by hand before the first run is a worse design than
        // one that makes itself, so "already there" is the success case, not an error.
        var created = await _sftp.CreateDirectoryAsync(_remotePath, cancellationToken).ConfigureAwait(false);
        return created.IsSuccess || created.Failure.Error == SftpError.AlreadyExists
            ? SftpResult.Success()
            : created;
    }
}
