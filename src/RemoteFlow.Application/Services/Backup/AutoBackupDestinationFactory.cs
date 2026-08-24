using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Backup;
using RemoteFlow.Application.Abstractions.Sftp;
using RemoteFlow.Application.Abstractions.Ssh;
using RemoteFlow.Application.Abstractions.Storage;
using RemoteFlow.Domain.Enums;

namespace RemoteFlow.Application.Services.Backup;

/// <summary>Turns a stored destination configuration into something that can be written to, and — just as
/// importantly — turns a configuration that no longer makes sense into a described failure rather than an
/// exception. That path is routine, not exotic: settings travel inside backup archives, so importing one
/// can point this machine at a connection or a folder that only ever existed on another.</summary>
public sealed class AutoBackupDestinationFactory(
    IConnectionRepository connections,
    ISshAuthenticationMaterialProvider authentication,
    ISshTransport transport,
    IObjectStorageClientFactory objectStorage,
    IAppPaths paths) : IAutoBackupDestinationFactory
{
    public const string StagingDirectoryName = "auto-backup";

    private readonly IConnectionRepository _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    private readonly ISshAuthenticationMaterialProvider _authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
    private readonly ISshTransport _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    private readonly IObjectStorageClientFactory _objectStorage = objectStorage ?? throw new ArgumentNullException(nameof(objectStorage));
    private readonly IAppPaths _paths = paths ?? throw new ArgumentNullException(nameof(paths));

    /// <summary>Remote destinations build their archive here. The cache directory, because it is
    /// reproducible and safe to lose, mirroring where remote edit keeps its working files.</summary>
    public string StagingRoot => Path.Combine(_paths.CacheDirectory, StagingDirectoryName);

    public Task<SftpResult<IAutoBackupDestination>> CreateAsync(
        AutoBackupDestination destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return destination.Kind switch
        {
            AutoBackupDestinationKind.LocalFolder =>
                Task.FromResult(LocalFolderBackupDestination.Create(destination.LocalFolder)),
            AutoBackupDestinationKind.SftpConnection =>
                CreateSftpAsync(destination, cancellationToken),
            AutoBackupDestinationKind.ObjectStorageConnection =>
                CreateObjectStorageAsync(destination, cancellationToken),
            _ => Task.FromResult(SftpResult<IAutoBackupDestination>.Fail(
                SftpError.NotSupported, "The automatic backup destination is not a kind RemoteFlow knows.")),
        };
    }

    private async Task<SftpResult<IAutoBackupDestination>> CreateSftpAsync(
        AutoBackupDestination destination,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(destination.RemotePath))
        {
            return Missing("Choose a remote directory for automatic backups.");
        }

        var connection = destination.ConnectionId is null
            ? null
            : await _connections.GetByIdAsync(destination.ConnectionId.Value, cancellationToken).ConfigureAwait(false);
        if (connection is null)
        {
            return Missing("The connection automatic backups are sent to no longer exists on this machine.");
        }

        if (!connection.SupportsSftp)
        {
            return SftpResult<IAutoBackupDestination>.Fail(
                SftpError.NotSupported,
                $"'{connection.Name}' is {connection.Protocol.GetDisplayName()} and cannot receive backups over SFTP.");
        }

        if (string.IsNullOrWhiteSpace(connection.Username))
        {
            return Missing($"'{connection.Name}' has no username, so RemoteFlow cannot sign in to it.");
        }

        var materials = await _authentication.CreateAsync(connection, cancellationToken).ConfigureAwait(false);
        var connected = await _transport.ConnectAsync(new SshConnectRequest
        {
            Host = connection.Host,
            Port = connection.Port,
            Username = connection.Username,
            AuthenticationMethods = materials,
            HostKeyPolicy = connection.Ssh.HostKeyPolicy,
            KeepAliveInterval = TimeSpan.FromSeconds(connection.Ssh.KeepAliveSeconds ?? 30),
            OperationTimeout = TimeSpan.FromSeconds(30),
        }, cancellationToken).ConfigureAwait(false);
        if (connected.IsFailure)
        {
            return SftpResult<IAutoBackupDestination>.Fail(
                SftpError.ConnectionLost,
                $"RemoteFlow could not connect to '{connection.Name}': {connected.Failure.Message}");
        }

        try
        {
            EnsureStagingRoot();
            var description = $"sftp://{connection.Host}:{connection.Port}{SftpPath.Normalize(destination.RemotePath)}";
            return SftpResult<IAutoBackupDestination>.Success(new SftpBackupDestination(
                connected.Value,
                connected.Value.OpenSftp(),
                destination.RemotePath,
                StagingRoot,
                description));
        }
        catch
        {
            await connected.Value.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<SftpResult<IAutoBackupDestination>> CreateObjectStorageAsync(
        AutoBackupDestination destination,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(destination.RemotePath))
        {
            return Missing("Choose a bucket or container for automatic backups.");
        }

        var connection = destination.ConnectionId is null
            ? null
            : await _connections.GetByIdAsync(destination.ConnectionId.Value, cancellationToken).ConfigureAwait(false);
        if (connection is null)
        {
            return Missing("The storage account automatic backups are sent to no longer exists on this machine.");
        }

        if (!connection.SupportsObjectStorage)
        {
            return SftpResult<IAutoBackupDestination>.Fail(
                SftpError.NotSupported,
                $"'{connection.Name}' is {connection.Protocol.GetDisplayName()} and is not an object storage account.");
        }

        var client = await _objectStorage.CreateAsync(connection.Id, cancellationToken).ConfigureAwait(false);
        if (client.IsFailure)
        {
            return SftpResult<IAutoBackupDestination>.Fail(
                client.Failure.Error,
                $"RemoteFlow could not reach '{connection.Name}': {client.Failure.Message}");
        }

        try
        {
            EnsureStagingRoot();
            var normalized = ObjectStoragePath.Normalize(destination.RemotePath);
            var scheme = connection.Protocol == ProtocolType.S3 ? "s3" : "azure";
            return SftpResult<IAutoBackupDestination>.Success(new ObjectStorageBackupDestination(
                client.Value,
                destination.RemotePath,
                StagingRoot,
                $"{scheme}://{normalized.TrimStart('/')}"));
        }
        catch
        {
            await client.Value.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static SftpResult<IAutoBackupDestination> Missing(string message)
    {
        return SftpResult<IAutoBackupDestination>.Fail(SftpError.NotFound, message);
    }

    private void EnsureStagingRoot()
    {
        _ = Directory.CreateDirectory(StagingRoot);
    }
}
