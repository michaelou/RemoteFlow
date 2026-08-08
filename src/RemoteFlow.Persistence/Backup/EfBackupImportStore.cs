using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Data.Sqlite;
using RemoteFlow.Application.Abstractions.Backup;
using RemoteFlow.Domain.Enums;

namespace RemoteFlow.Persistence.Backup;

public sealed class EfBackupImportStore(
    IDbContextFactory<RemoteFlowDbContext> contextFactory,
    string databasePath,
    IBackupImportFaultInjector? faultInjector = null) : IBackupImportStore, IDisposable
{
    private readonly IDbContextFactory<RemoteFlowDbContext> _contextFactory =
        contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    private readonly string _databasePath = Path.GetFullPath(databasePath);
    private readonly IBackupImportFaultInjector? _faultInjector = faultInjector;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public async Task<BackupImportStoreResult> ApplyAsync(
        BackupDataSnapshot target,
        MergeStrategy strategy,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(target);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var backupPath = strategy == MergeStrategy.Replace
                ? $"{_databasePath}.bak"
                : $"{_databasePath}.pre-import.bak";
            await CheckpointAsync(cancellationToken).ConfigureAwait(false);
            SqliteConnection.ClearAllPools();
            File.Copy(_databasePath, backupPath, overwrite: true);
            try
            {
                await ApplyCoreAsync(target, strategy, cancellationToken).ConfigureAwait(false);
                SqliteConnection.ClearAllPools();
                return new BackupImportStoreResult(backupPath);
            }
            catch
            {
                SqliteConnection.ClearAllPools();
                File.Copy(backupPath, _databasePath, overwrite: true);
                TryDeleteSidecar($"{_databasePath}-wal");
                TryDeleteSidecar($"{_databasePath}-shm");
                throw;
            }
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _gate.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private async Task CheckpointAsync(CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        _ = await context.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ApplyCoreAsync(
        BackupDataSnapshot target,
        MergeStrategy strategy,
        CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var recent = strategy == MergeStrategy.Merge
            ? await context.RecentConnections.AsNoTracking()
                .Select(item => new RecentRow(item.ConnectionId, item.LastOpenedUtc, item.OpenCount))
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false)
            : [];
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var step = 0;
        try
        {
            foreach (var table in new[] { "ConnectionTags", "RecentConnections", "Connections", "Folders", "Tags", "Settings", "HostKeys" })
            {
                await ExecuteAsync(
                    context,
                    $"DELETE FROM \"{table}\";",
                    new Dictionary<string, object?>(),
                    cancellationToken).ConfigureAwait(false);
                InjectFault(ref step);
            }

            foreach (var folder in target.Folders.OrderBy(item => item.Depth))
            {
                await InsertFolderAsync(context, folder, cancellationToken).ConfigureAwait(false);
                InjectFault(ref step);
            }

            foreach (var tag in target.Tags)
            {
                await InsertTagAsync(context, tag, cancellationToken).ConfigureAwait(false);
                InjectFault(ref step);
            }

            foreach (var connection in target.Connections)
            {
                await InsertConnectionAsync(context, connection, cancellationToken).ConfigureAwait(false);
                InjectFault(ref step);
            }

            foreach (var link in target.ConnectionTags)
            {
                await ExecuteAsync(context,
                    "INSERT INTO ConnectionTags (ConnectionId, TagId) VALUES (@connectionId, @tagId);",
                    new Dictionary<string, object?>
                    {
                        ["connectionId"] = GuidText(link.ConnectionId),
                        ["tagId"] = GuidText(link.TagId),
                    }, cancellationToken).ConfigureAwait(false);
                InjectFault(ref step);
            }

            foreach (var setting in target.Settings)
            {
                await ExecuteAsync(context,
                    "INSERT INTO Settings (Key, Value, ModifiedUtc) VALUES (@key, @value, @modifiedUtc);",
                    new Dictionary<string, object?>
                    {
                        ["key"] = setting.Key,
                        ["value"] = setting.Value,
                        ["modifiedUtc"] = DateText(setting.ModifiedUtc),
                    }, cancellationToken).ConfigureAwait(false);
                InjectFault(ref step);
            }

            foreach (var hostKey in target.HostKeys)
            {
                await InsertHostKeyAsync(context, hostKey, cancellationToken).ConfigureAwait(false);
                InjectFault(ref step);
            }

            var connectionIds = target.Connections.Select(item => item.Id).ToHashSet();
            foreach (var item in recent.Where(item => connectionIds.Contains(item.ConnectionId)))
            {
                await ExecuteAsync(context,
                    "INSERT INTO RecentConnections (ConnectionId, LastOpenedUtc, OpenCount) VALUES (@connectionId, @lastOpenedUtc, @openCount);",
                    new Dictionary<string, object?>
                    {
                        ["connectionId"] = GuidText(item.ConnectionId),
                        ["lastOpenedUtc"] = DateText(item.LastOpenedUtc),
                        ["openCount"] = item.OpenCount,
                    }, cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static Task InsertFolderAsync(
        RemoteFlowDbContext context,
        BackupFolder value,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(context,
            "INSERT INTO Folders (Id, Name, ParentId, Path, Depth, SortOrder, IsExpanded, ConcurrencyStamp, CreatedUtc, ModifiedUtc) " +
            "VALUES (@id, @name, @parentId, @path, @depth, @sortOrder, @isExpanded, @stamp, @createdUtc, @modifiedUtc);",
            new Dictionary<string, object?>
            {
                ["id"] = GuidText(value.Id),
                ["name"] = value.Name,
                ["parentId"] = GuidText(value.ParentId),
                ["path"] = value.Path,
                ["depth"] = value.Depth,
                ["sortOrder"] = value.SortOrder,
                ["isExpanded"] = Bool(value.IsExpanded),
                ["stamp"] = GuidText(value.ConcurrencyStamp),
                ["createdUtc"] = DateText(value.CreatedUtc),
                ["modifiedUtc"] = DateText(value.ModifiedUtc),
            }, cancellationToken);
    }

    private static Task InsertTagAsync(
        RemoteFlowDbContext context,
        BackupTag value,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(context,
            "INSERT INTO Tags (Id, Name, ColorHex, CreatedUtc) VALUES (@id, @name, @color, @createdUtc);",
            new Dictionary<string, object?>
            {
                ["id"] = GuidText(value.Id),
                ["name"] = value.Name,
                ["color"] = value.ColorHex,
                ["createdUtc"] = DateText(value.CreatedUtc),
            }, cancellationToken);
    }

    private static Task InsertHostKeyAsync(
        RemoteFlowDbContext context,
        BackupHostKey value,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(context,
            "INSERT INTO HostKeys (Id, Host, Port, KeyAlgorithm, PublicKeyBase64, Sha256Fingerprint, TrustState, Source, Comment, FirstSeenUtc, LastSeenUtc) " +
            "VALUES (@id, @host, @port, @algorithm, @publicKey, @fingerprint, @trust, @source, @comment, @firstSeen, @lastSeen);",
            new Dictionary<string, object?>
            {
                ["id"] = GuidText(value.Id),
                ["host"] = value.Host,
                ["port"] = value.Port,
                ["algorithm"] = value.KeyAlgorithm,
                ["publicKey"] = value.PublicKeyBase64,
                ["fingerprint"] = value.Sha256Fingerprint,
                ["trust"] = (int)value.TrustState,
                ["source"] = (int)value.Source,
                ["comment"] = value.Comment,
                ["firstSeen"] = DateText(value.FirstSeenUtc),
                ["lastSeen"] = DateText(value.LastSeenUtc),
            }, cancellationToken);
    }

    private static Task InsertConnectionAsync(
        RemoteFlowDbContext context,
        BackupConnection value,
        CancellationToken cancellationToken)
    {
        const string columns = "Id, Name, Host, Port, Protocol, Username, AuthMethod, Notes, FolderId, IsFavorite, Environment, ColorOverrideHex, SortOrder, ConcurrencyStamp, CreatedUtc, ModifiedUtc, Credential_Kind, Credential_StoreKey, Credential_StoreProvider, Credential_UpdatedUtc, Ssh_KeepAliveSeconds, Ssh_TerminalType, Ssh_PrivateKeyPath, Ssh_InitialCommand, Ssh_StartupDirectory, Ssh_HostKeyPolicy, Ssh_RequestPty, Sftp_RemoteRootPath, Sftp_LocalDownloadPath, Sftp_PreserveTimestamps, Sftp_ShowHiddenFiles, Rdp_Domain, Rdp_FullScreen, Rdp_Width, Rdp_Height, Rdp_Multimon, Rdp_RedirectClipboard, Rdp_RedirectDrives";
        const string values = "@id, @name, @host, @port, @protocol, @username, @authMethod, @notes, @folderId, @favorite, @environment, @color, @sortOrder, @stamp, @createdUtc, @modifiedUtc, @credentialKind, @storeKey, @storeProvider, @credentialUpdated, @keepAlive, @terminalType, @privateKeyPath, @initialCommand, @startupDirectory, @hostKeyPolicy, @requestPty, @remoteRoot, @localDownload, @preserveTimestamps, @showHidden, @domain, @fullScreen, @width, @height, @multimon, @redirectClipboard, @redirectDrives";
        return ExecuteAsync(context, $"INSERT INTO Connections ({columns}) VALUES ({values});",
            new Dictionary<string, object?>
            {
                ["id"] = GuidText(value.Id),
                ["name"] = value.Name,
                ["host"] = value.Host,
                ["port"] = value.Port,
                ["protocol"] = (int)value.Protocol,
                ["username"] = value.Username,
                ["authMethod"] = (int)value.AuthMethod,
                ["notes"] = value.Notes,
                ["folderId"] = GuidText(value.FolderId),
                ["favorite"] = Bool(value.IsFavorite),
                ["environment"] = (int)value.Environment,
                ["color"] = value.ColorOverrideHex,
                ["sortOrder"] = value.SortOrder,
                ["stamp"] = GuidText(value.ConcurrencyStamp),
                ["createdUtc"] = DateText(value.CreatedUtc),
                ["modifiedUtc"] = DateText(value.ModifiedUtc),
                ["credentialKind"] = (int)value.Credential.Kind,
                ["storeKey"] = value.Credential.StoreKey,
                ["storeProvider"] = value.Credential.StoreProvider,
                ["credentialUpdated"] = DateText(value.Credential.UpdatedUtc),
                ["keepAlive"] = value.Ssh.KeepAliveSeconds,
                ["terminalType"] = value.Ssh.TerminalType,
                ["privateKeyPath"] = value.Ssh.PrivateKeyPath,
                ["initialCommand"] = value.Ssh.InitialCommand,
                ["startupDirectory"] = value.Ssh.StartupDirectory,
                ["hostKeyPolicy"] = (int)value.Ssh.HostKeyPolicy,
                ["requestPty"] = Bool(value.Ssh.RequestPty),
                ["remoteRoot"] = value.Sftp.RemoteRootPath,
                ["localDownload"] = value.Sftp.LocalDownloadPath,
                ["preserveTimestamps"] = Bool(value.Sftp.PreserveTimestamps),
                ["showHidden"] = Bool(value.Sftp.ShowHiddenFiles),
                ["domain"] = value.Rdp.Domain,
                ["fullScreen"] = Bool(value.Rdp.FullScreen),
                ["width"] = value.Rdp.Width,
                ["height"] = value.Rdp.Height,
                ["multimon"] = Bool(value.Rdp.Multimon),
                ["redirectClipboard"] = Bool(value.Rdp.RedirectClipboard),
                ["redirectDrives"] = Bool(value.Rdp.RedirectDrives),
            }, cancellationToken);
    }

    private static async Task ExecuteAsync(
        RemoteFlowDbContext context,
        string commandText,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken)
    {
        var connection = context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
        foreach (var pair in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = $"@{pair.Key}";
            parameter.Value = pair.Value ?? DBNull.Value;
            _ = command.Parameters.Add(parameter);
        }

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private void InjectFault(ref int step)
    {
        _faultInjector?.OnImportStep(++step);
    }

    private static string GuidText(Guid value)
    {
        return value.ToString("D");
    }

    private static string? GuidText(Guid? value)
    {
        return value?.ToString("D");
    }

    private static string DateText(DateTimeOffset value)
    {
        return value.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string? DateText(DateTimeOffset? value)
    {
        return value?.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static int Bool(bool value)
    {
        return value ? 1 : 0;
    }

    private static void TryDeleteSidecar(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record RecentRow(Guid ConnectionId, DateTimeOffset LastOpenedUtc, int OpenCount);
}
