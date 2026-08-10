using RemoteFlow.Application.Abstractions;
using RemoteFlow.Domain.Common;
using RemoteFlow.Domain.Entities;
using RemoteFlow.UI.Services;

namespace RemoteFlow.Rdp.Windows;

public sealed class WindowsRdpWorkspaceSessionFactory(IEmbeddedRdpSessionProvider provider) :
    IEmbeddedRdpWorkspaceSessionFactory
{
    private readonly IEmbeddedRdpSessionProvider _provider = provider ?? throw new ArgumentNullException(nameof(provider));

    public bool IsAvailableOnCurrentPlatform => true;

    public bool SupportsEmbeddedSessions => _provider.SupportsEmbeddedSessions;

    public async Task<Result<IEmbeddedRdpWorkspaceSession>> CreateAsync(
        Connection connection,
        CancellationToken cancellationToken = default)
    {
        var result = await _provider.CreateAsync(connection, cancellationToken).ConfigureAwait(false);
        return result.IsFailure
            ? Result<IEmbeddedRdpWorkspaceSession>.Failure(result.Error)
            : Result<IEmbeddedRdpWorkspaceSession>.Success(new RdpSessionViewModel(
                result.Value,
                connection.Name,
                connection.Environment,
                connection.ColorOverrideHex));
    }
}
