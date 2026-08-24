using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.UI.Views.Security;

namespace RemoteFlow.UI.Services;

public sealed class VaultUnlockPromptService : IVaultUnlockPrompt
{
    public async ValueTask<VaultUnlockPromptResult?> PromptAsync(
        VaultUnlockPromptRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return await Dispatcher.UIThread.InvokeAsync(() => ShowCoreAsync(request, cancellationToken));
    }

    private static async Task<VaultUnlockPromptResult?> ShowCoreAsync(
        VaultUnlockPromptRequest request,
        CancellationToken cancellationToken)
    {
        // No window to own the dialog means no way to ask, which reads as declining rather than as an error:
        // the caller's job is to carry on with the vault shut, and it already knows how.
        if (global::Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime
            { MainWindow.IsVisible: true } desktop)
        {
            return null;
        }

        var dialog = new VaultUnlockWindow(request);
        using var registration = cancellationToken.Register(() => Dispatcher.UIThread.Post(dialog.Close));
        await dialog.ShowDialog(desktop.MainWindow);
        cancellationToken.ThrowIfCancellationRequested();
        return dialog.Result;
    }
}
