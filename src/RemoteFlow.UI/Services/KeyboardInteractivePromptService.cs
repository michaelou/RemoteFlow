using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using RemoteFlow.Application.Abstractions.Ssh;
using RemoteFlow.UI.Views.Security;

namespace RemoteFlow.UI.Services;

public sealed class KeyboardInteractivePromptService : IKeyboardInteractivePrompt
{
    public async ValueTask<IReadOnlyList<string>> RespondAsync(
        IReadOnlyList<SshAuthenticationPrompt> prompts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompts);
        cancellationToken.ThrowIfCancellationRequested();
        return await Dispatcher.UIThread.InvokeAsync(() => ShowCoreAsync(prompts, cancellationToken));
    }

    private static async Task<IReadOnlyList<string>> ShowCoreAsync(
        IReadOnlyList<SshAuthenticationPrompt> prompts,
        CancellationToken cancellationToken)
    {
        if (global::Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime
            { MainWindow.IsVisible: true } desktop)
        {
            return [];
        }
        var dialog = new KeyboardInteractivePromptWindow(prompts);
        using var registration = cancellationToken.Register(() => Dispatcher.UIThread.Post(dialog.Close));
        await dialog.ShowDialog(desktop.MainWindow);
        cancellationToken.ThrowIfCancellationRequested();
        return dialog.Responses;
    }
}
