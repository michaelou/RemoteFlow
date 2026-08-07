using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using RemoteFlow.Application.Abstractions;

namespace RemoteFlow.UI.Services;

public sealed class AvaloniaClipboardService : IClipboardService
{
    public async Task<ClipboardReadResult> ReadTextAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await Dispatcher.UIThread.InvokeAsync(() => ReadCoreAsync(cancellationToken));
    }

    public async Task<ClipboardWriteResult> WriteTextAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();
        return await Dispatcher.UIThread.InvokeAsync(() => WriteCoreAsync(text, cancellationToken));
    }

    private static async Task<ClipboardReadResult> ReadCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var clipboard = GetClipboard();
        if (clipboard is null)
        {
            return ClipboardReadResult.Failure("Clipboard access is unavailable.");
        }

        try
        {
            return ClipboardReadResult.Success(await clipboard.TryGetTextAsync());
        }
        catch (Exception exception)
        {
            return ClipboardReadResult.Failure($"Clipboard text could not be read: {exception.Message}");
        }
    }

    private static async Task<ClipboardWriteResult> WriteCoreAsync(
        string text,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var clipboard = GetClipboard();
        if (clipboard is null)
        {
            return ClipboardWriteResult.Failure("Clipboard access is unavailable.");
        }

        try
        {
            await clipboard.SetTextAsync(text);
            return ClipboardWriteResult.Success;
        }
        catch (Exception exception)
        {
            return ClipboardWriteResult.Failure($"Clipboard text could not be written: {exception.Message}");
        }
    }

    private static IClipboard? GetClipboard()
    {
        return global::Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow?.Clipboard
            : null;
    }
}
