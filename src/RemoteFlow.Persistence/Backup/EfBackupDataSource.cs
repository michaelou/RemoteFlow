using Microsoft.EntityFrameworkCore;
using RemoteFlow.Application.Abstractions.Backup;

namespace RemoteFlow.Persistence.Backup;

public sealed class EfBackupDataSource(IDbContextFactory<RemoteFlowDbContext> contextFactory) : IBackupDataSource
{
    private readonly IDbContextFactory<RemoteFlowDbContext> _contextFactory =
        contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));

    public async Task<BackupDataSnapshot> CaptureAsync(
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var connections = await context.Connections.AsNoTracking()
            .OrderBy(connection => connection.Id)
            .Select(connection => new BackupConnection(
                connection.Id,
                connection.Name,
                connection.Host,
                connection.Port,
                connection.Protocol,
                connection.Username,
                connection.AuthMethod,
                connection.Notes,
                connection.FolderId,
                connection.IsFavorite,
                connection.Environment,
                connection.ColorOverrideHex,
                connection.SortOrder,
                connection.ConcurrencyStamp,
                connection.CreatedUtc,
                connection.ModifiedUtc,
                new BackupCredentialReference(
                    connection.Credential.Kind,
                    connection.Credential.StoreKey,
                    connection.Credential.StoreProvider,
                    connection.Credential.UpdatedUtc),
                new BackupSshOptions(
                    connection.Ssh.KeepAliveSeconds,
                    connection.Ssh.TerminalType,
                    connection.Ssh.PrivateKeyPath,
                    connection.Ssh.InitialCommand,
                    connection.Ssh.StartupDirectory,
                    connection.Ssh.HostKeyPolicy,
                    connection.Ssh.RequestPty),
                new BackupSftpOptions(
                    connection.Sftp.RemoteRootPath,
                    connection.Sftp.LocalDownloadPath,
                    connection.Sftp.PreserveTimestamps,
                    connection.Sftp.ShowHiddenFiles),
                new BackupRdpOptions(
                    connection.Rdp.Domain,
                    connection.Rdp.FullScreen,
                    connection.Rdp.Width,
                    connection.Rdp.Height,
                    connection.Rdp.Multimon,
                    connection.Rdp.RedirectClipboard,
                    connection.Rdp.RedirectDrives)))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        progress?.Report(new BackupProgress("Connections read", 1, 8));

        var folders = await context.Folders.AsNoTracking().OrderBy(folder => folder.Depth).ThenBy(folder => folder.Path)
            .Select(folder => new BackupFolder(
                folder.Id,
                folder.Name,
                folder.ParentId,
                folder.Path,
                folder.Depth,
                folder.SortOrder,
                folder.IsExpanded,
                folder.ConcurrencyStamp,
                folder.CreatedUtc,
                folder.ModifiedUtc))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        progress?.Report(new BackupProgress("Folders read", 2, 8));

        var tags = await context.Tags.AsNoTracking().OrderBy(tag => tag.Name)
            .Select(tag => new BackupTag(tag.Id, tag.Name, tag.ColorHex, tag.CreatedUtc))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        progress?.Report(new BackupProgress("Tags read", 3, 8));

        var connectionTags = await context.ConnectionTags.AsNoTracking()
            .OrderBy(link => link.ConnectionId).ThenBy(link => link.TagId)
            .Select(link => new BackupConnectionTag(link.ConnectionId, link.TagId))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        progress?.Report(new BackupProgress("Tag links read", 4, 8));

        var settings = await context.Settings.AsNoTracking().OrderBy(setting => setting.Key)
            .Select(setting => new BackupSetting(setting.Key, setting.Value, setting.ModifiedUtc))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        progress?.Report(new BackupProgress("Settings read", 5, 8));

        var hostKeys = await context.HostKeys.AsNoTracking()
            .OrderBy(hostKey => hostKey.Host).ThenBy(hostKey => hostKey.Port).ThenBy(hostKey => hostKey.KeyAlgorithm)
            .Select(hostKey => new BackupHostKey(
                hostKey.Id,
                hostKey.Host,
                hostKey.Port,
                hostKey.KeyAlgorithm,
                hostKey.PublicKeyBase64,
                hostKey.Sha256Fingerprint,
                hostKey.TrustState,
                hostKey.Source,
                hostKey.Comment,
                hostKey.FirstSeenUtc,
                hostKey.LastSeenUtc))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        progress?.Report(new BackupProgress("Host keys read", 6, 8));

        return new BackupDataSnapshot(connections, folders, tags, connectionTags, settings, hostKeys);
    }
}
