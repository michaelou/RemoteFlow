using RemoteFlow.Application.Abstractions;
using RemoteFlow.Domain.Entities;
using RemoteFlow.Domain.Enums;

namespace RemoteFlow.Rdp.Windows;

internal interface IEmbeddedRdpCredentialSource
{
    Task<SecretHandle?> GetAsync(CancellationToken cancellationToken);
}

internal sealed class EmbeddedRdpCredentialSource(
    Connection connection,
    IEnumerable<ICredentialProvider> providers) : IEmbeddedRdpCredentialSource
{
    private readonly Connection _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    private readonly IReadOnlyList<ICredentialProvider> _providers =
        [.. providers ?? throw new ArgumentNullException(nameof(providers))];

    public Task<SecretHandle?> GetAsync(CancellationToken cancellationToken)
    {
        var credential = _connection.Credential;
        if (credential.IsEmpty || credential.Kind != CredentialKind.RdpPassword)
        {
            return Task.FromResult<SecretHandle?>(null);
        }

        var provider = _providers.FirstOrDefault(candidate =>
            candidate.IsAvailable &&
            string.Equals(candidate.Name, credential.StoreProvider, StringComparison.OrdinalIgnoreCase));
        return provider is null
            ? Task.FromResult<SecretHandle?>(null)
            : provider.GetAsync(credential.StoreKey, cancellationToken);
    }
}

internal sealed class NoEmbeddedRdpCredentialSource : IEmbeddedRdpCredentialSource
{
    public static NoEmbeddedRdpCredentialSource Instance { get; } = new();

    private NoEmbeddedRdpCredentialSource()
    {
    }

    public Task<SecretHandle?> GetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<SecretHandle?>(null);
    }
}
