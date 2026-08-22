using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Storage;
using RemoteFlow.Domain.Entities;
using RemoteFlow.Domain.Enums;

namespace RemoteFlow.UI.Services;

public interface IStorageWorkspaceSessionFactory
{
    Task<StorageWorkspaceSession> OpenAsync(Guid connectionId, CancellationToken cancellationToken = default);
}

/// <summary>One attached object-storage account. The counterpart of <c>SftpWorkspaceSession</c>, and
/// deliberately thinner: there is no connection to hold open, because every operation is an independent
/// authenticated HTTP request.</summary>
public sealed class StorageWorkspaceSession(
    Connection definition,
    IObjectStorageService storage) : IAsyncDisposable
{
    public Connection Definition { get; } = definition ?? throw new ArgumentNullException(nameof(definition));

    public IObjectStorageService Storage { get; } = storage ?? throw new ArgumentNullException(nameof(storage));

    /// <summary>Where the remote pane opens: the bucket when the connection names one, plus its root
    /// prefix when it has one, and the account otherwise.</summary>
    public string RootPath
    {
        get
        {
            var container = Definition.ObjectStorage.Container;
            if (string.IsNullOrWhiteSpace(container))
            {
                return ObjectStoragePath.Root;
            }

            var prefix = Definition.ObjectStorage.RootPrefix;
            return string.IsNullOrWhiteSpace(prefix)
                ? ObjectStoragePath.Normalize("/" + container)
                : ObjectStoragePath.Combine("/" + container, prefix);
        }
    }

    public string DisplayName => Definition.Protocol.GetDisplayName() + " · " + Definition.Name;

    public ValueTask DisposeAsync()
    {
        return Storage.DisposeAsync();
    }
}

public sealed class StorageWorkspaceSessionFactory(
    IConnectionRepository connections,
    IObjectStorageClientFactory clients,
    IRecentConnectionStore recent,
    IClock clock) : IStorageWorkspaceSessionFactory
{
    public async Task<StorageWorkspaceSession> OpenAsync(
        Guid connectionId,
        CancellationToken cancellationToken = default)
    {
        var definition = await connections.GetByIdAsync(connectionId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Connection '{connectionId}' was not found.");
        if (!definition.Protocol.IsObjectStorage())
        {
            throw new InvalidOperationException("The selected connection is not an object-storage account.");
        }

        var client = await clients.CreateAsync(connectionId, cancellationToken).ConfigureAwait(false);
        if (client.IsFailure)
        {
            throw new InvalidOperationException(client.Failure.Message);
        }

        await recent.RecordOpenedAsync(definition.Id, clock.UtcNow, cancellationToken).ConfigureAwait(false);
        return new StorageWorkspaceSession(definition, client.Value);
    }
}
