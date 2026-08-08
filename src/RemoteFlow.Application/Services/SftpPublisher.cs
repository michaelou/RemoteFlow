using RemoteFlow.Application.Abstractions.Sftp;

namespace RemoteFlow.Application.Services;

/// <summary>
/// Publishes a fully written temporary upload under its final remote name.
/// </summary>
public static class SftpPublisher
{
    /// <summary>
    /// Moves <paramref name="temporaryPath"/> onto <paramref name="destinationPath"/>, replacing an
    /// existing file when there is one and keeping its permissions.
    /// </summary>
    /// <remarks>
    /// SFTP v3 rename does not clobber: OpenSSH implements it with link()+unlink(), so a rename onto
    /// an existing name fails. When the plain rename is refused, the current file is moved aside so
    /// the publishing rename lands on a free name. Every step is a plain rename, so the previous
    /// contents survive a failure at any point and are put back.
    /// </remarks>
    public static async Task<SftpResult> PublishAsync(
        ISftpService sftp,
        string temporaryPath,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sftp);
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var existing = await sftp.StatAsync(destinationPath, cancellationToken).ConfigureAwait(false);
        var replaced = existing.IsSuccess ? existing.Value : null;

        var published = await sftp.RenameAsync(temporaryPath, destinationPath, cancellationToken)
            .ConfigureAwait(false);
        if (published.IsFailure && replaced is not null)
        {
            published = await ReplaceAsync(sftp, temporaryPath, destinationPath, published, cancellationToken)
                .ConfigureAwait(false);
        }

        if (published.IsSuccess && replaced is { IsDirectory: false, IsSymlink: false })
        {
            // Best effort: the published file carries the upload's permissions, and a server that
            // rejects chmod must not turn a saved edit into a failed one.
            _ = await sftp.SetPermissionsAsync(destinationPath, replaced.Mode, cancellationToken)
                .ConfigureAwait(false);
        }

        return published;
    }

    private static async Task<SftpResult> ReplaceAsync(
        ISftpService sftp,
        string temporaryPath,
        string destinationPath,
        SftpResult renameFailure,
        CancellationToken cancellationToken)
    {
        var supersededPath = $"{destinationPath}.remoteflow-{Guid.NewGuid():N}.superseded";
        var setAside = await sftp.RenameAsync(destinationPath, supersededPath, cancellationToken)
            .ConfigureAwait(false);
        if (setAside.IsFailure)
        {
            return renameFailure;
        }

        var published = await sftp.RenameAsync(temporaryPath, destinationPath, cancellationToken)
            .ConfigureAwait(false);
        if (published.IsFailure)
        {
            _ = await sftp.RenameAsync(supersededPath, destinationPath, CancellationToken.None)
                .ConfigureAwait(false);
            return published;
        }

        _ = await sftp.DeleteAsync(supersededPath, recursive: false, CancellationToken.None).ConfigureAwait(false);
        return published;
    }
}
