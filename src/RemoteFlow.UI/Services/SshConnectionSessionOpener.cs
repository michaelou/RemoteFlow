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

    public async Task<ConnectionOpenResult> OpenAsync(
        Guid connectionId,
        ConnectionOpenMode mode,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (mode == ConnectionOpenMode.Rdp)
            {
                return await LaunchRdpAsync(connectionId, cancellationToken).ConfigureAwait(true);
            }

            var navigation = _services.GetRequiredService<INavigationService>();
            if (mode == ConnectionOpenMode.Sftp)
            {
                var workspace = _services.GetRequiredService<ViewModels.Sftp.SftpWorkspaceViewModel>();
                navigation.Navigate("sftp");
                await workspace.AttachAsync(connectionId, cancellationToken).ConfigureAwait(true);
                return workspace.IsConnected && workspace.ErrorMessage is null
                    ? ConnectionOpenResult.Success()
                    : ConnectionOpenResult.Failure(workspace.ErrorMessage);
            }

            navigation.Navigate("terminals");
            _ = await _sessions.OpenAsync(connectionId, cancellationToken).ConfigureAwait(true);
            return ConnectionOpenResult.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ConnectionOpenResult.Failure();
        }
        catch (Exception exception)
        {
            return ConnectionOpenResult.Failure(exception.Message);
        }
    }

    /// <summary>An RDP session leaves RemoteFlow entirely: the platform client takes it, so there is no
    /// tab to navigate to and nothing to attach.</summary>
    private async Task<ConnectionOpenResult> LaunchRdpAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var launcher = _services.GetService<IRdpLauncher>();
        if (launcher is null)
        {
            return ConnectionOpenResult.Failure("No RDP launcher is available.");
        }

        var connection = await _services.GetRequiredService<IConnectionRepository>()
            .GetByIdAsync(connectionId, cancellationToken).ConfigureAwait(true);
        if (connection is null)
        {
            return ConnectionOpenResult.Failure("The connection no longer exists.");
        }

        var result = await launcher.LaunchAsync(connection, cancellationToken).ConfigureAwait(true);
        return result.Succeeded
            ? ConnectionOpenResult.Success()
            : ConnectionOpenResult.Failure(result.Message);
    }
}
