using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Sftp;
using RemoteFlow.UI.ViewModels.Sftp;
using RemoteFlow.UI.Views.Sftp;

namespace RemoteFlow.UI.Services;

public interface IRemoteEditConflictDialogService
{
    Task<RemoteEditConflictResolution> ShowAsync(
        RemoteEditConflict conflict,
        CancellationToken cancellationToken = default);
}

public sealed class RemoteEditConflictDialogService : IRemoteEditConflictDialogService
{
    public async Task<RemoteEditConflictResolution> ShowAsync(
        RemoteEditConflict conflict,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        cancellationToken.ThrowIfCancellationRequested();
        return await Dispatcher.UIThread.InvokeAsync(() => ShowCoreAsync(conflict, cancellationToken));
    }

    private static async Task<RemoteEditConflictResolution> ShowCoreAsync(
        RemoteEditConflict conflict,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (global::Avalonia.Application.Current?.ApplicationLifetime is not
            IClassicDesktopStyleApplicationLifetime { MainWindow.IsVisible: true } desktop)
        {
            return RemoteEditConflictResolution.Cancel;
        }
        var dialog = new ConflictDialog(new RemoteEditConflictDialogViewModel(conflict));
        await dialog.ShowDialog(desktop.MainWindow);
        return dialog.Resolution;
    }
}

public sealed class RemoteEditConflictResolver(
    ISettingsStore settings,
    IRemoteEditConflictDialogService dialogs,
    IConfirmationDialogService confirmation) : IRemoteEditConflictResolver
{
    public async Task<RemoteEditConflictResolution> ResolveAsync(
        RemoteEditConflict conflict,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        var configured = await settings.Get(SettingKeys.RemoteEditConflictDefault, cancellationToken)
            .ConfigureAwait(false);
        var resolution = configured switch
        {
            RemoteEditConflictDefault.Overwrite => RemoteEditConflictResolution.OverwriteRemote,
            RemoteEditConflictDefault.KeepBoth => RemoteEditConflictResolution.KeepBoth,
            RemoteEditConflictDefault.Discard => RemoteEditConflictResolution.DiscardLocal,
            RemoteEditConflictDefault.Prompt => await dialogs.ShowAsync(conflict, cancellationToken)
                .ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unknown remote edit conflict default: {configured}."),
        };
        if (resolution != RemoteEditConflictResolution.DiscardLocal)
        {
            return resolution;
        }

        var confirmed = await confirmation.ConfirmAsync(
            "Discard local changes?",
            $"Replace the local editing copy of '{conflict.RemotePath}' with the current remote file? " +
                "The local changes cannot be recovered.",
            "Discard local changes",
            cancellationToken).ConfigureAwait(false);
        return confirmed
            ? RemoteEditConflictResolution.DiscardLocal
            : RemoteEditConflictResolution.Cancel;
    }
}
