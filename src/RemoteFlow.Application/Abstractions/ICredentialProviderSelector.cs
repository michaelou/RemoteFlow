namespace RemoteFlow.Application.Abstractions;

public interface ICredentialProviderSelector
{
    Task<ICredentialProvider> SelectAsync(CancellationToken cancellationToken = default);
}
