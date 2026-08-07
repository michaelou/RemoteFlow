using RemoteFlow.Application.Abstractions;
using RemoteFlow.Domain.Abstractions;
using RemoteFlow.Domain.Common;
using RemoteFlow.Domain.Entities;
using RemoteFlow.Domain.Enums;
using RemoteFlow.Domain.ValueObjects;

namespace RemoteFlow.Application.Services;

public enum CredentialStorageStatus
{
    NotStored = 0,
    Stored = 1,
    UnavailableOnThisMachine = 2,
}

public sealed record CredentialStorageInfo(CredentialStorageStatus Status, string? ProviderName);

public interface IConnectionCredentialService
{
    Task<CredentialStorageInfo> InspectAsync(Guid connectionId, CancellationToken cancellationToken = default);

    Task<Result<Connection>> StoreAsync(
        Guid connectionId,
        CredentialKind kind,
        ReadOnlyMemory<char> secret,
        string displayName,
        CancellationToken cancellationToken = default);

    Task<Result<Connection>> ClearAsync(Guid connectionId, CancellationToken cancellationToken = default);
}

public sealed class ConnectionCredentialService(
    IConnectionRepository connections,
    ICredentialProviderSelector providerSelector,
    IEnumerable<ICredentialProvider> providers,
    IUnitOfWork unitOfWork,
    IGuidProvider guidProvider,
    IClock clock,
    IConnectionChangeNotifier? changeNotifier = null) : IConnectionCredentialService
{
    private const string _storeKeyPrefix = "remoteflow/connection";
    private readonly IReadOnlyList<ICredentialProvider> _providers = [.. providers];

    public async Task<CredentialStorageInfo> InspectAsync(
        Guid connectionId,
        CancellationToken cancellationToken = default)
    {
        var connection = await connections.GetByIdAsync(connectionId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Connection '{connectionId}' was not found.");
        if (connection.Credential.IsEmpty)
        {
            return new CredentialStorageInfo(CredentialStorageStatus.NotStored, null);
        }

        var provider = FindProvider(connection.Credential.StoreProvider);
        if (provider is null || !provider.IsAvailable)
        {
            return new CredentialStorageInfo(
                CredentialStorageStatus.UnavailableOnThisMachine,
                connection.Credential.StoreProvider);
        }

        using var secret = await provider.GetAsync(connection.Credential.StoreKey, cancellationToken).ConfigureAwait(false);
        return new CredentialStorageInfo(
            secret is null ? CredentialStorageStatus.UnavailableOnThisMachine : CredentialStorageStatus.Stored,
            connection.Credential.StoreProvider);
    }

    public async Task<Result<Connection>> StoreAsync(
        Guid connectionId,
        CredentialKind kind,
        ReadOnlyMemory<char> secret,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        if (secret.IsEmpty)
        {
            return Result<Connection>.Failure(RemoteFlowError.Validation(
                "credential.secret",
                "Enter a secret before saving it."));
        }

        if (!Enum.IsDefined(kind) || kind == CredentialKind.None)
        {
            return Result<Connection>.Failure(RemoteFlowError.Validation(
                "credential.kind",
                "Choose a supported credential kind."));
        }

        var connection = await connections.GetByIdAsync(connectionId, cancellationToken).ConfigureAwait(false);
        if (connection is null)
        {
            return MissingConnection(connectionId);
        }

        try
        {
            var provider = await providerSelector.SelectAsync(cancellationToken).ConfigureAwait(false);
            var storeKey = BuildStoreKey(connectionId, kind);
            await provider.SetAsync(storeKey, secret, displayName, cancellationToken).ConfigureAwait(false);
            var credential = CredentialRef.Create(kind, storeKey, provider.Name, clock.UtcNow);
            if (credential.IsFailure)
            {
                await provider.DeleteAsync(storeKey, cancellationToken).ConfigureAwait(false);
                return Result<Connection>.Failure(credential.Error);
            }

            var previous = connection.Credential;
            _ = connection.SetCredential(credential.Value, guidProvider, clock.UtcNow);
            try
            {
                await unitOfWork.ExecuteAsync(
                    token => connections.UpdateAsync(connection, token),
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await provider.DeleteAsync(storeKey, CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            if (!previous.IsEmpty &&
                (!string.Equals(previous.StoreProvider, provider.Name, StringComparison.Ordinal) ||
                 !string.Equals(previous.StoreKey, storeKey, StringComparison.Ordinal)))
            {
                var previousProvider = FindProvider(previous.StoreProvider);
                if (previousProvider?.IsAvailable == true)
                {
                    await previousProvider.DeleteAsync(previous.StoreKey, cancellationToken).ConfigureAwait(false);
                }
            }

            changeNotifier?.Notify(connection.Id, ConnectionChangeKind.Updated);
            return Result<Connection>.Success(connection);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Result<Connection>.Failure(RemoteFlowError.Unavailable(
                "credential.store_unavailable",
                $"The credential could not be stored: {exception.Message}"));
        }
    }

    public async Task<Result<Connection>> ClearAsync(
        Guid connectionId,
        CancellationToken cancellationToken = default)
    {
        var connection = await connections.GetByIdAsync(connectionId, cancellationToken).ConfigureAwait(false);
        if (connection is null)
        {
            return MissingConnection(connectionId);
        }

        if (connection.Credential.IsEmpty)
        {
            return Result<Connection>.Success(connection);
        }

        var provider = FindProvider(connection.Credential.StoreProvider);
        if (provider is null || !provider.IsAvailable)
        {
            return Result<Connection>.Failure(RemoteFlowError.Unavailable(
                "credential.provider_unavailable",
                "The credential cannot be cleared because its store is unavailable on this machine."));
        }

        try
        {
            await provider.DeleteAsync(connection.Credential.StoreKey, cancellationToken).ConfigureAwait(false);
            _ = connection.SetCredential(CredentialRef.None(), guidProvider, clock.UtcNow);
            await unitOfWork.ExecuteAsync(
                token => connections.UpdateAsync(connection, token),
                cancellationToken).ConfigureAwait(false);
            changeNotifier?.Notify(connection.Id, ConnectionChangeKind.Updated);
            return Result<Connection>.Success(connection);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Result<Connection>.Failure(RemoteFlowError.Unavailable(
                "credential.clear_failed",
                $"The credential could not be cleared: {exception.Message}"));
        }
    }

    private ICredentialProvider? FindProvider(string name)
    {
        return _providers.FirstOrDefault(provider =>
            string.Equals(provider.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildStoreKey(Guid connectionId, CredentialKind kind)
    {
        var suffix = kind switch
        {
            CredentialKind.None => throw new ArgumentOutOfRangeException(nameof(kind)),
            CredentialKind.Password => "password",
            CredentialKind.PrivateKeyPassphrase => "private-key-passphrase",
            CredentialKind.RdpPassword => "rdp-password",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        return $"{_storeKeyPrefix}/{connectionId:D}/{suffix}";
    }

    private static Result<Connection> MissingConnection(Guid id)
    {
        return Result<Connection>.Failure(RemoteFlowError.NotFound(
            "connection.not_found",
            $"Connection '{id}' was not found."));
    }
}
