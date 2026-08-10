using Microsoft.Extensions.DependencyInjection;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Domain.Entities;
using RemoteFlow.Domain.Enums;
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
            if (mode == ConnectionOpenMode.Default)
            {
                var defaultConnection = await _services.GetRequiredService<IConnectionRepository>()
                    .GetByIdAsync(connectionId, cancellationToken).ConfigureAwait(true);
                if (defaultConnection?.Protocol == ProtocolType.Rdp)
                {
                    return await LaunchRdpAsync(connectionId, ConnectionOpenMode.Rdp, cancellationToken)
                        .ConfigureAwait(true);
                }
            }

            if (mode is ConnectionOpenMode.Rdp or ConnectionOpenMode.RdpExternal)
            {
                return await LaunchRdpAsync(connectionId, mode, cancellationToken).ConfigureAwait(true);
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

    /// <summary>RDP opens either as a retained workspace tab or in the platform client. The external
    /// branch deliberately preserves the old launcher path unchanged.</summary>
    private async Task<ConnectionOpenResult> LaunchRdpAsync(
        Guid connectionId,
        ConnectionOpenMode mode,
        CancellationToken cancellationToken)
    {
        var connection = await _services.GetRequiredService<IConnectionRepository>()
            .GetByIdAsync(connectionId, cancellationToken).ConfigureAwait(true);
        if (connection is null)
        {
            return ConnectionOpenResult.Failure("The connection no longer exists.");
        }

        var embeddedFactory = _services.GetRequiredService<IEmbeddedRdpWorkspaceSessionFactory>();
        if (mode == ConnectionOpenMode.RdpExternal || !embeddedFactory.IsAvailableOnCurrentPlatform)
        {
            return await LaunchExternalRdpAsync(connection, cancellationToken).ConfigureAwait(true);
        }

        var defaultMode = await _services.GetRequiredService<ISettingsStore>()
            .Get(SettingKeys.WindowsRdpOpenMode, cancellationToken).ConfigureAwait(true);
        if (defaultMode == WindowsRdpOpenMode.External)
        {
            return await LaunchExternalRdpAsync(connection, cancellationToken).ConfigureAwait(true);
        }

        if (!embeddedFactory.SupportsEmbeddedSessions)
        {
            return EmbeddedFailure("Embedded RDP is unavailable on this Windows installation.");
        }

        var created = await embeddedFactory.CreateAsync(connection, cancellationToken).ConfigureAwait(true);
        if (created.IsFailure)
        {
            return EmbeddedFailure(created.Error.Message);
        }

        var workspace = _services.GetRequiredService<ViewModels.Terminal.TerminalsPageViewModel>();
        _services.GetRequiredService<INavigationService>().Navigate("terminals");
        workspace.AddWorkspaceSession(created.Value);
        try
        {
            created.Value.PrepareForConnect();
            await created.Value.ConnectAsync(cancellationToken).ConfigureAwait(true);
            return ConnectionOpenResult.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = await workspace.CloseSessionAsync(created.Value, skipConfirmation: true, CancellationToken.None)
                .ConfigureAwait(true);
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _ = await workspace.CloseSessionAsync(created.Value, skipConfirmation: true, cancellationToken)
                .ConfigureAwait(true);
            return EmbeddedFailure($"The embedded RDP session could not start: {exception.Message}");
        }
    }

    private async Task<ConnectionOpenResult> LaunchExternalRdpAsync(
        Connection connection,
        CancellationToken cancellationToken)
    {
        var launcher = _services.GetService<IRdpLauncher>();
        if (launcher is null)
        {
            return ConnectionOpenResult.Failure("No RDP launcher is available.");
        }

        var result = await launcher.LaunchAsync(connection, cancellationToken).ConfigureAwait(true);
        return result.Succeeded
            ? ConnectionOpenResult.Success()
            : ConnectionOpenResult.Failure(result.Message);
    }

    private static ConnectionOpenResult EmbeddedFailure(string message)
    {
        return ConnectionOpenResult.RecoverableFailure(
            $"{message} You can still use the external RDP client.",
            "Open in external RDP client",
            ConnectionOpenMode.RdpExternal);
    }
}
