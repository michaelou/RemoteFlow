using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Sftp;
using RemoteFlow.Application.Abstractions.Storage;
using RemoteFlow.Domain.Enums;

namespace RemoteFlow.Infrastructure.Storage;

/// <summary>Picks the provider for a connection's protocol, the way <c>ConfiguredSshTransport</c> picks an
/// SSH transport. The secret is held only for as long as it takes the provider to construct its client.
/// </summary>
public sealed class ObjectStorageClientFactory : IObjectStorageClientFactory
{
    private readonly IConnectionRepository _connections;
    private readonly IObjectStorageSecretProvider _secrets;
    private readonly IReadOnlyList<IObjectStorageProvider> _providers;

    public ObjectStorageClientFactory(
        IConnectionRepository connections,
        IObjectStorageSecretProvider secrets,
        IEnumerable<IObjectStorageProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _providers = [.. providers];
    }

    public async Task<SftpResult<IObjectStorageService>> CreateAsync(
        Guid connectionId,
        CancellationToken cancellationToken = default)
    {
        var connection = await _connections.GetByIdAsync(connectionId, cancellationToken).ConfigureAwait(false);
        if (connection is null)
        {
            return SftpResult<IObjectStorageService>.Fail(
                SftpError.NotFound,
                $"Connection '{connectionId}' was not found.");
        }

        var endpoint = ObjectStorageEndpoint.Create(connection);
        if (endpoint.IsFailure)
        {
            return SftpResult<IObjectStorageService>.Fail(endpoint.Failure.Error, endpoint.Failure.Message);
        }

        var provider = _providers.FirstOrDefault(candidate => candidate.Protocol == connection.Protocol);
        if (provider is null)
        {
            return SftpResult<IObjectStorageService>.Fail(
                SftpError.NotSupported,
                $"No object storage provider is registered for {connection.Protocol.GetDisplayName()}.");
        }

        using var secret = await _secrets.GetSecretKeyAsync(connection, cancellationToken).ConfigureAwait(false);
        return secret is null
            ? SftpResult<IObjectStorageService>.Fail(
                SftpError.PermissionDenied,
                connection.Protocol == ProtocolType.S3
                    ? "No secret access key is stored for this connection. Store it in the connection editor."
                    : "No account key is stored for this connection. Store it in the connection editor.")
            : provider.Create(endpoint.Value, secret.Secret);
    }
}

/// <summary>Reads the connection's single stored credential, refusing anything that is not the storage
/// secret-key kind so an SSH password can never be signed with by mistake.</summary>
public sealed class ConnectionObjectStorageSecretProvider(
    IEnumerable<ICredentialProvider> credentialProviders) : IObjectStorageSecretProvider
{
    private readonly IReadOnlyList<ICredentialProvider> _credentialProviders = [.. credentialProviders];

    public async Task<SecretHandle?> GetSecretKeyAsync(
        Domain.Entities.Connection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var reference = connection.Credential;
        if (reference.IsEmpty || reference.Kind != CredentialKind.StorageSecretKey)
        {
            return null;
        }

        var provider = _credentialProviders.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, reference.StoreProvider, StringComparison.OrdinalIgnoreCase));
        return provider?.IsAvailable != true
            ? null
            : await provider.GetAsync(reference.StoreKey, cancellationToken).ConfigureAwait(false);
    }
}
