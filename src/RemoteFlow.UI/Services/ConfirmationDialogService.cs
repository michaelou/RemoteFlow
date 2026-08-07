using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace RemoteFlow.UI.Services;

public interface IConfirmationDialogService
{
    Task<bool> ConfirmAsync(
        string title,
        string message,
        string confirmLabel,
        CancellationToken cancellationToken = default);
}

public sealed class ConfirmationDialogService : IConfirmationDialogService
{
    public async Task<bool> ConfirmAsync(
        string title,
        string message,
        string confirmLabel,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmLabel);
        cancellationToken.ThrowIfCancellationRequested();
        return await Dispatcher.UIThread.InvokeAsync(() => ShowCoreAsync(
            title,
            message,
            confirmLabel,
            cancellationToken));
    }

    private static async Task<bool> ShowCoreAsync(
        string title,
        string message,
        string confirmLabel,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (global::Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime
            { MainWindow.IsVisible: true } desktop)
        {
            return false;
        }

        var result = false;
        var cancelButton = new Button { Content = "Cancel" };
        var confirmButton = new Button { Content = confirmLabel };
        var dialog = new Window
        {
            Title = title,
            Width = 520,
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
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    new StackPanel
                    {
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Children = { cancelButton, confirmButton },
                    },
                },
            },
        };
        cancelButton.Click += (_, _) => dialog.Close();
        confirmButton.Click += (_, _) =>
        {
            result = true;
            dialog.Close();
        };
        await dialog.ShowDialog(desktop.MainWindow);
        return result;
    }
}
