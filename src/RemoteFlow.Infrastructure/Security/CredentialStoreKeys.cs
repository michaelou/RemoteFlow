using RemoteFlow.Application.Abstractions;
using RemoteFlow.Domain.Enums;

namespace RemoteFlow.Infrastructure.Security;

public static class CredentialStoreKeys
{
    private const string _prefix = "remoteflow/connection";

    public static string ForConnection(Guid connectionId, CredentialKind kind)
    {
        if (connectionId == Guid.Empty)
        {
            throw new ArgumentException("The connection ID is required.", nameof(connectionId));
        }

        var kindName = kind switch
        {
            CredentialKind.None => throw new ArgumentOutOfRangeException(
                nameof(kind), kind, "A concrete credential kind is required."),
            CredentialKind.Password => "password",
            CredentialKind.PrivateKeyPassphrase => "private-key-passphrase",
            CredentialKind.RdpPassword => "rdp-password",
            CredentialKind.StorageSecretKey => "storage-secret-key",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "A concrete credential kind is required."),
        };
        return $"{_prefix}/{connectionId:D}/{kindName}";
    }

    public static async Task DeleteConnectionCredentialsAsync(
        this ICredentialProvider provider,
        Guid connectionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        foreach (var kind in Enum.GetValues<CredentialKind>().Where(kind => kind != CredentialKind.None))
        {
            await provider.DeleteAsync(ForConnection(connectionId, kind), cancellationToken).ConfigureAwait(false);
        }
    }
}
