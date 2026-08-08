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
        if (mode == ConnectionOpenMode.Rdp)
        {
            return false;
        }
        try
        {
            var navigation = _services.GetRequiredService<INavigationService>();
            if (mode == ConnectionOpenMode.Sftp)
            {
                var workspace = _services.GetRequiredService<ViewModels.Sftp.SftpWorkspaceViewModel>();
                navigation.Navigate("sftp");
                await workspace.AttachAsync(connectionId, cancellationToken).ConfigureAwait(true);
                return workspace.IsConnected && workspace.ErrorMessage is null;
            }

            navigation.Navigate("terminals");
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
