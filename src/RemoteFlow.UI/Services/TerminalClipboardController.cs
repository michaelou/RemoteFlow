using System.Text;
using Avalonia;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.UI.ViewModels.Terminal;

namespace RemoteFlow.UI.Services;

public sealed record TerminalClipboardActionResult(bool Performed, string? ErrorMessage)
{
    public static TerminalClipboardActionResult Success { get; } = new(true, null);

    public static TerminalClipboardActionResult Cancelled { get; } = new(false, null);

    public static TerminalClipboardActionResult Failure(string message)
    {
        return new TerminalClipboardActionResult(false, message);
    }
}

public sealed class TerminalClipboardController(
    IClipboardService clipboard,
    ISettingsStore settings,
    IPasteWarningService warning)
{
    private static readonly byte[] _pasteStart = [0x1B, (byte)'[', (byte)'2', (byte)'0', (byte)'0', (byte)'~'];
    private static readonly byte[] _pasteEnd = [0x1B, (byte)'[', (byte)'2', (byte)'0', (byte)'1', (byte)'~'];
    private readonly Dictionary<TerminalSessionViewModel, EventHandler<AvaloniaPropertyChangedEventArgs>> _subscriptions = [];

    public void Attach(TerminalSessionViewModel session, Action<string?> reportError)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(reportError);
        if (_subscriptions.ContainsKey(session))
        {
            return;
        }

        async void OnModelPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
        {
            if (args.Property.Name != nameof(session.Model.HasSelection) || !session.Model.HasSelection ||
                !await settings.Get(SettingKeys.CopyOnSelect).ConfigureAwait(true))
            {
                return;
            }

            var result = await CopyAsync(session, clearSelection: false).ConfigureAwait(true);
            reportError(result.ErrorMessage);
        }

        EventHandler<AvaloniaPropertyChangedEventArgs> handler = OnModelPropertyChanged;
        session.Model.PropertyChanged += handler;
        _subscriptions.Add(session, handler);
    }

    public void Detach(TerminalSessionViewModel session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (_subscriptions.Remove(session, out var handler))
        {
            session.Model.PropertyChanged -= handler;
        }
    }

    public async Task<TerminalClipboardActionResult> CopyAsync(
        TerminalSessionViewModel session,
        bool clearSelection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.Model.HasSelection)
        {
            return TerminalClipboardActionResult.Cancelled;
        }

        var result = await clipboard.WriteTextAsync(
            PrepareCopyText(session.Model.SelectedText),
            cancellationToken).ConfigureAwait(true);
        if (!result.Succeeded)
        {
            return TerminalClipboardActionResult.Failure(
                result.ErrorMessage ?? "Clipboard text could not be written.");
        }

        if (clearSelection)
        {
            session.Model.ClearSelection();
        }

        return TerminalClipboardActionResult.Success;
    }

    public async Task<TerminalClipboardActionResult> PasteAsync(
        TerminalSessionViewModel session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var read = await clipboard.ReadTextAsync(cancellationToken).ConfigureAwait(true);
        if (!read.Succeeded)
        {
            return TerminalClipboardActionResult.Failure(
                read.ErrorMessage ?? "Clipboard text could not be read.");
        }

        if (string.IsNullOrEmpty(read.Text))
        {
            return TerminalClipboardActionResult.Cancelled;
        }

        var normalized = NormalizeNewlines(read.Text);
        var byteCount = Encoding.UTF8.GetByteCount(normalized);
        var lineCount = normalized.Count(character => character == '\n') + 1;
        var shouldWarn = (lineCount > 1 || byteCount > 4096) &&
            !await settings.Get(SettingKeys.SuppressPasteWarning, cancellationToken).ConfigureAwait(true);
        if (shouldWarn)
        {
            var confirmation = await warning.ConfirmAsync(lineCount, byteCount, cancellationToken).ConfigureAwait(true);
            if (!confirmation.Proceed)
            {
                return TerminalClipboardActionResult.Cancelled;
            }

            if (confirmation.DontAskAgain)
            {
                await settings.Set(SettingKeys.SuppressPasteWarning, true, cancellationToken).ConfigureAwait(true);
            }
        }

        await session.SendInputAsync(CreateBracketedPaste(normalized), cancellationToken).ConfigureAwait(true);
        return TerminalClipboardActionResult.Success;
    }

    public static string PrepareCopyText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return string.Join('\n', NormalizeNewlines(text).Split('\n').Select(line => line.TrimEnd(' ', '\t')));
    }

    public static byte[] CreateBracketedPaste(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var content = Encoding.UTF8.GetBytes(NormalizeNewlines(text));
        return [.. _pasteStart, .. content, .. _pasteEnd];
    }

    private static string NormalizeNewlines(string text)
    {
        return text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
    }
}
