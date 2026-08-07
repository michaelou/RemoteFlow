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
        }
    }
}
