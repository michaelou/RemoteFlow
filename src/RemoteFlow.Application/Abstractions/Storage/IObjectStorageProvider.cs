using RemoteFlow.Application.Abstractions.Sftp;
using RemoteFlow.Domain.Entities;
using RemoteFlow.Domain.Enums;

namespace RemoteFlow.Application.Abstractions.Storage;

/// <summary>One provider SDK behind the contracts. Registered with <c>TryAddEnumerable</c> and selected by
/// protocol, the way an SSH transport is selected by setting.</summary>
public interface IObjectStorageProvider
{
    ProtocolType Protocol { get; }

    /// <summary>Builds a client for the account. The secret is copied into the SDK's own credential object
    /// and not retained by the provider.</summary>
    SftpResult<IObjectStorageService> Create(ObjectStorageEndpoint endpoint, ReadOnlyMemory<char> secretKey);
}

/// <summary>Turns a saved connection into a live client: reads it, resolves the endpoint, fetches the
/// stored secret key, and picks the provider for its protocol.</summary>
public interface IObjectStorageClientFactory
{
    Task<SftpResult<IObjectStorageService>> CreateAsync(
        Guid connectionId,
        CancellationToken cancellationToken = default);
}

/// <summary>Where the secret access key or storage account key comes from. Separate from the client
/// factory so tests can supply a key without a credential store, and so the key's lifetime stays visible:
/// the handle is disposed as soon as the client has been constructed.</summary>
public interface IObjectStorageSecretProvider
{
    Task<SecretHandle?> GetSecretKeyAsync(Connection connection, CancellationToken cancellationToken = default);
}
