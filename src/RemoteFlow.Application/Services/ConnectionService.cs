using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Validation;
using RemoteFlow.Domain.Abstractions;
using RemoteFlow.Domain.Common;
using RemoteFlow.Domain.Entities;
using RemoteFlow.Domain.Enums;
using RemoteFlow.Domain.ValueObjects;

namespace RemoteFlow.Application.Services;

public interface IConnectionService
{
    Task<Result<Connection>> CreateAsync(ConnectionInput input, CancellationToken cancellationToken = default);

    Task<Result<Connection>> UpdateAsync(Guid id, ConnectionInput input, CancellationToken cancellationToken = default);

    Task<Result<Connection>> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Result<Connection>> DuplicateAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Result<Connection>> MoveToFolderAsync(Guid id, Guid? folderId, CancellationToken cancellationToken = default);

    Task<Result<Connection>> ToggleFavoriteAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Result<Connection>> RenameAsync(Guid id, string name, CancellationToken cancellationToken = default);

    Task<Result<Connection>> SetSortOrderAsync(Guid id, int? sortOrder, CancellationToken cancellationToken = default);
}

public sealed class ConnectionService(
    IConnectionRepository connections,
    IRecentConnectionStore recentConnections,
    IEnumerable<ICredentialProvider> credentialProviders,
    IUnitOfWork unitOfWork,
    IGuidProvider guidProvider,
    IClock clock,
    IConnectionChangeNotifier? changeNotifier = null) : IConnectionService
{
    private readonly IReadOnlyList<ICredentialProvider> _credentialProviders = [.. credentialProviders];

    public Task<Result<Connection>> CreateAsync(
        ConnectionInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var validation = ValidateFirst(input);
        return validation is not null
            ? Task.FromResult(Result<Connection>.Failure(validation))
            : NotifyAfterAsync(unitOfWork.ExecuteAsync(async token =>
        {
            var created = Connection.Create(
                guidProvider,
                input.Name,
                input.Host,
                input.Port,
                input.Protocol,
                clock.UtcNow);
            if (created.IsFailure)
            {
                return created;
            }

            var connection = created.Value;
            var configured = Configure(connection, input);
            if (configured.IsFailure)
            {
                return configured;
            }

            await connections.AddAsync(connection, token).ConfigureAwait(false);
            return Result<Connection>.Success(connection);
        }, cancellationToken), ConnectionChangeKind.Created);
    }

    public Task<Result<Connection>> UpdateAsync(
        Guid id,
        ConnectionInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var validation = ValidateFirst(input);
        return validation is not null
            ? Task.FromResult(Result<Connection>.Failure(validation))
            : NotifyAfterAsync(unitOfWork.ExecuteAsync(async token =>
        {
            var connection = await connections.GetByIdAsync(id, token).ConfigureAwait(false);
            if (connection is null)
            {
                return MissingConnection(id);
            }

            var port = input.Port;
            if (connection.Protocol != input.Protocol &&
                connection.Port == connection.Protocol.GetDefaultPort() &&
                input.Port == connection.Port)
            {
                port = input.Protocol.GetDefaultPort();
            }

            var normalizedInput = input with { Port = port };
            var renamed = connection.Rename(normalizedInput.Name, guidProvider, clock.UtcNow);
            if (renamed.IsFailure)
            {
                return renamed;
            }

            var endpoint = connection.ChangeEndpoint(
                normalizedInput.Host,
                normalizedInput.Port,
                normalizedInput.Protocol,
                guidProvider,
                clock.UtcNow);
            if (endpoint.IsFailure)
            {
                return endpoint;
            }

            var configured = Configure(connection, normalizedInput);
            if (configured.IsFailure)
            {
                return configured;
            }

            await connections.UpdateAsync(connection, token).ConfigureAwait(false);
            return Result<Connection>.Success(connection);
        }, cancellationToken), ConnectionChangeKind.Updated);
    }

    public Task<Result<Connection>> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return NotifyAfterAsync(unitOfWork.ExecuteAsync(async token =>
        {
            var connection = await connections.GetByIdAsync(id, token).ConfigureAwait(false);
            if (connection is null)
            {
                return MissingConnection(id);
            }

            if (!connection.Credential.IsEmpty)
            {
                var provider = _credentialProviders.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, connection.Credential.StoreProvider, StringComparison.OrdinalIgnoreCase));
                if (provider is null || !provider.IsAvailable)
                {
                    return Result<Connection>.Failure(RemoteFlowError.Unavailable(
                        "credential.provider_unavailable",
                        "The saved credential could not be removed because its credential store is unavailable."));
                }

                await provider.DeleteAsync(connection.Credential.StoreKey, token).ConfigureAwait(false);
            }

            await recentConnections.RemoveAsync(id, token).ConfigureAwait(false);
            await connections.DeleteAsync(id, token).ConfigureAwait(false);
            return Result<Connection>.Success(connection);
        }, cancellationToken), ConnectionChangeKind.Deleted);
    }

    public Task<Result<Connection>> DuplicateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return NotifyAfterAsync(unitOfWork.ExecuteAsync(async token =>
        {
            var source = await connections.GetByIdAsync(id, token).ConfigureAwait(false);
            if (source is null)
            {
                return MissingConnection(id);
            }

            var created = Connection.Create(
                guidProvider,
                $"{source.Name} (copy)",
                source.Host,
                source.Port,
                source.Protocol,
                clock.UtcNow);
            if (created.IsFailure)
            {
                return created;
            }

            var duplicate = created.Value;
            var details = duplicate.SetDetails(
                source.Username,
                source.AuthMethod,
                source.Notes,
                source.Environment,
                source.ColorOverrideHex,
                guidProvider,
                clock.UtcNow);
            if (details.IsFailure)
            {
                return details;
            }

            _ = duplicate.SetFolder(source.FolderId, guidProvider, clock.UtcNow)
                .SetFavorite(source.IsFavorite, guidProvider, clock.UtcNow)
                .SetSortOrder(source.SortOrder, guidProvider, clock.UtcNow)
                .SetOptions(source.Ssh, source.Sftp, source.Rdp, source.ObjectStorage, guidProvider, clock.UtcNow);
            foreach (var tag in source.Tags)
            {
                _ = duplicate.AddTag(tag.TagId);
            }

            await connections.AddAsync(duplicate, token).ConfigureAwait(false);
            return Result<Connection>.Success(duplicate);
        }, cancellationToken), ConnectionChangeKind.Created);
    }

    public Task<Result<Connection>> MoveToFolderAsync(
        Guid id,
        Guid? folderId,
        CancellationToken cancellationToken = default)
    {
        return MutateAsync(id, connection => connection.SetFolder(folderId, guidProvider, clock.UtcNow), cancellationToken);
    }

    public Task<Result<Connection>> ToggleFavoriteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return MutateAsync(
            id,
            connection => connection.SetFavorite(!connection.IsFavorite, guidProvider, clock.UtcNow),
            cancellationToken);
    }

    public Task<Result<Connection>> RenameAsync(
        Guid id,
        string name,
        CancellationToken cancellationToken = default)
    {
        return NotifyAfterAsync(unitOfWork.ExecuteAsync(async token =>
        {
            var connection = await connections.GetByIdAsync(id, token).ConfigureAwait(false);
            if (connection is null)
            {
                return MissingConnection(id);
            }

            var renamed = connection.Rename(name, guidProvider, clock.UtcNow);
            if (renamed.IsFailure)
            {
                return renamed;
            }

            await connections.UpdateAsync(connection, token).ConfigureAwait(false);
            return renamed;
        }, cancellationToken), ConnectionChangeKind.Updated);
    }

    public Task<Result<Connection>> SetSortOrderAsync(
        Guid id,
        int? sortOrder,
        CancellationToken cancellationToken = default)
    {
        return MutateAsync(
            id,
            connection => connection.SetSortOrder(sortOrder, guidProvider, clock.UtcNow),
            cancellationToken);
    }

    private Task<Result<Connection>> MutateAsync(
        Guid id,
        Func<Connection, Connection> mutation,
        CancellationToken cancellationToken)
    {
        return NotifyAfterAsync(unitOfWork.ExecuteAsync(async token =>
        {
            var connection = await connections.GetByIdAsync(id, token).ConfigureAwait(false);
            if (connection is null)
            {
                return MissingConnection(id);
            }

            _ = mutation(connection);
            await connections.UpdateAsync(connection, token).ConfigureAwait(false);
            return Result<Connection>.Success(connection);
        }, cancellationToken), ConnectionChangeKind.Updated);
    }

    private Result<Connection> Configure(Connection connection, ConnectionInput input)
    {
        var ssh = SshOptions.Default().Configure(
            privateKeyPath: input.PrivateKeyPath,
            hostKeyPolicy: input.HostKeyPolicy);
        if (ssh.IsFailure)
        {
            return Result<Connection>.Failure(ssh.Error);
        }

        var rdp = RdpOptions.Default().Configure(
            domain: input.RdpDomain,
            fullScreen: input.RdpFullScreen,
            width: input.RdpWidth,
            height: input.RdpHeight,
            multimon: input.RdpMultimon,
            redirectClipboard: input.RdpRedirectClipboard,
            redirectDrives: input.RdpRedirectDrives);
        if (rdp.IsFailure)
        {
            return Result<Connection>.Failure(rdp.Error);
        }

        // SetOptions replaces every owned options object at once, so anything not rebuilt from the input
        // here is reset to its default on every save. SFTP options have no editor fields yet and so are
        // still handed the default; the storage options do, and are threaded through.
        var storage = ObjectStorageOptions.Default().Configure(
            region: input.StorageRegion,
            serviceUrl: input.StorageServiceUrl,
            usePathStyleAddressing: input.StorageUsePathStyleAddressing,
            container: input.StorageContainer,
            rootPrefix: input.StorageRootPrefix,
            localDownloadPath: input.StorageLocalDownloadPath);
        if (storage.IsFailure)
        {
            return Result<Connection>.Failure(storage.Error);
        }

        var details = connection.SetDetails(
            input.Username,
            input.AuthMethod,
            input.Notes,
            input.Environment,
            input.ColorOverrideHex,
            guidProvider,
            clock.UtcNow);
        if (details.IsFailure)
        {
            return details;
        }

        _ = connection.SetFolder(input.FolderId, guidProvider, clock.UtcNow)
            .SetOptions(ssh.Value, SftpOptions.Default(), rdp.Value, storage.Value, guidProvider, clock.UtcNow);
        return Result<Connection>.Success(connection);
    }

    private static RemoteFlowError? ValidateFirst(ConnectionInput input)
    {
        var errors = ConnectionValidator.Validate(input);
        return errors.Count == 0 ? null : errors[0];
    }

    private static Result<Connection> MissingConnection(Guid id)
    {
        return Result<Connection>.Failure(RemoteFlowError.NotFound(
            "connection.not_found",
            $"Connection '{id}' was not found."));
    }

    private void Notify(Guid connectionId, ConnectionChangeKind kind)
    {
        changeNotifier?.Notify(connectionId, kind);
    }

    private async Task<Result<Connection>> NotifyAfterAsync(
        Task<Result<Connection>> operation,
        ConnectionChangeKind kind)
    {
        var result = await operation.ConfigureAwait(false);
        if (result.IsSuccess)
        {
            Notify(result.Value.Id, kind);
        }

        return result;
    }
}
