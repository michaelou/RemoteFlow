using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Threading;
using RemoteFlow.Application.Abstractions;

namespace RemoteFlow.UI.Services;

public sealed class ErrorDialogService : IErrorDialogService
{
    public async Task ShowAsync(string title, string message, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        cancellationToken.ThrowIfCancellationRequested();
        await Dispatcher.UIThread.InvokeAsync(() => ShowCoreAsync(title, message, cancellationToken));
    }

    private static async Task ShowCoreAsync(string title, string message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var closeButton = new Button
        {
            Content = "Close",
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var dialog = new Window
        {
            Title = title,
            Width = 560,
            MinWidth = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(24),
                Spacing = 20,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    closeButton,
                },
            },
        };
        closeButton.Click += (_, _) => dialog.Close();

        if (global::Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime
            { MainWindow.IsVisible: true } desktop)
        {
            await dialog.ShowDialog(desktop.MainWindow);
            return;
        }

        // There is not always a window to own it. Startup runs behind the splash, and a failure there
        // happens before the main window is built at all — which is the one moment an error absolutely
        // has to be shown, because the alternative is a process that vanishes without a word. Shown
        // ownerless, and awaited through its Closed event, since only ShowDialog returns a task.
        var closed = new TaskCompletionSource();
        dialog.Closed += (_, _) => closed.TrySetResult();
        // Above the splash, which is topmost while it is up.
        dialog.Topmost = true;
        dialog.Show();
        await closed.Task;
    }
}
