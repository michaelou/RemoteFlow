using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Threading;

namespace RemoteFlow.UI.Services;

public sealed record PasteWarningResult(bool Proceed, bool DontAskAgain);

public interface IPasteWarningService
{
    Task<PasteWarningResult> ConfirmAsync(
        int lineCount,
        int utf8ByteCount,
        CancellationToken cancellationToken = default);
}

public sealed class PasteWarningDialogService : IPasteWarningService
{
    public async Task<PasteWarningResult> ConfirmAsync(
        int lineCount,
        int utf8ByteCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lineCount);
        ArgumentOutOfRangeException.ThrowIfNegative(utf8ByteCount);
        cancellationToken.ThrowIfCancellationRequested();
        return await Dispatcher.UIThread.InvokeAsync(() => ShowCoreAsync(lineCount, utf8ByteCount, cancellationToken));
    }

    private static async Task<PasteWarningResult> ShowCoreAsync(
        int lineCount,
        int utf8ByteCount,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (global::Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime
            { MainWindow.IsVisible: true } desktop)
        {
            return new PasteWarningResult(false, false);
        }

        var result = new PasteWarningResult(false, false);
        var remember = new CheckBox { Content = "Don't ask again" };
        var cancel = new Button { Content = "Cancel" };
        var paste = new Button { Content = "Paste" };
        var dialog = new Window
        {
            Title = "Paste multiple lines?",
            Width = 500,
            MinWidth = 400,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new global::Avalonia.Thickness(24),
                Spacing = 16,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"The clipboard contains {lineCount} lines ({utf8ByteCount:N0} UTF-8 bytes). Review it before pasting into the terminal.",
                        TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
                    },
                    remember,
                    new StackPanel
                    {
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Children = { cancel, paste },
                    },
                },
            },
        };
        cancel.Click += (_, _) => dialog.Close();
        paste.Click += (_, _) =>
        {
            result = new PasteWarningResult(true, remember.IsChecked == true);
            dialog.Close();
        };
        await dialog.ShowDialog(desktop.MainWindow);
        return result;
    }
}
