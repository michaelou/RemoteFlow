using Microsoft.Extensions.DependencyInjection;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.UI.Navigation;

namespace RemoteFlow.UI.Services;

public sealed class SshConnectionSessionOpener(
    ISessionManager sessions,
    IServiceProvider services) : IConnectionSessionOpener
{
    private readonly ISessionManager _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    private readonly IServiceProvider _services = services ?? throw new ArgumentNullException(nameof(services));

    public async Task<bool> OpenAsync(
        Guid connectionId,
        ConnectionOpenMode mode,
        CancellationToken cancellationToken = default)
    {
        if (mode != ConnectionOpenMode.Default)
        {
            return false;
        }
        try
        {
            _services.GetRequiredService<INavigationService>().Navigate("terminals");
            _ = await _sessions.OpenAsync(connectionId, cancellationToken).ConfigureAwait(true);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
