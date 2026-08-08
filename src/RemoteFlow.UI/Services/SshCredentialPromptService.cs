using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.UI.Views.Security;

namespace RemoteFlow.UI.Services;

public sealed class SshCredentialPromptService : ISshCredentialPrompt
{
    public async ValueTask<SshCredentialPromptResult?> PromptAsync(
        SshCredentialPromptRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return await Dispatcher.UIThread.InvokeAsync(() => ShowCoreAsync(request, cancellationToken));
    }

    private static async Task<SshCredentialPromptResult?> ShowCoreAsync(
        SshCredentialPromptRequest request,
        CancellationToken cancellationToken)
    {
        if (global::Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime
            { MainWindow.IsVisible: true } desktop)
        {
            return null;
        }
        var dialog = new SshCredentialPromptWindow(request);
        using var registration = cancellationToken.Register(() => Dispatcher.UIThread.Post(dialog.Close));
        await dialog.ShowDialog(desktop.MainWindow);
        cancellationToken.ThrowIfCancellationRequested();
        return dialog.Result;
    }
}
