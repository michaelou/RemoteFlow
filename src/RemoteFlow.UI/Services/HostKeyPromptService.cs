using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using RemoteFlow.Application.Abstractions.Ssh;
using RemoteFlow.UI.Views.Security;

namespace RemoteFlow.UI.Services;

public sealed class HostKeyPromptService : IHostKeyPrompt
{
    public async ValueTask<bool> ConfirmTrustAsync(
        HostKeyTrustPrompt prompt,
        CancellationToken cancellationToken = default)
    {
        return await PromptAsync(prompt, cancellationToken).ConfigureAwait(false) != HostKeyPromptDecision.Reject;
    }

    public async ValueTask<HostKeyPromptDecision> PromptAsync(
        HostKeyTrustPrompt prompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        cancellationToken.ThrowIfCancellationRequested();
        return await Dispatcher.UIThread.InvokeAsync(() => ShowCoreAsync(prompt, cancellationToken));
    }

    private static async Task<HostKeyPromptDecision> ShowCoreAsync(
        HostKeyTrustPrompt prompt,
        CancellationToken cancellationToken)
    {
        if (global::Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime
            { MainWindow.IsVisible: true } desktop)
        {
            return HostKeyPromptDecision.Reject;
        }

        var dialog = new HostKeyPromptWindow(prompt);
        using var registration = cancellationToken.Register(() => Dispatcher.UIThread.Post(dialog.Close));
        await dialog.ShowDialog(desktop.MainWindow);
        cancellationToken.ThrowIfCancellationRequested();
        return dialog.Decision;
    }
}
