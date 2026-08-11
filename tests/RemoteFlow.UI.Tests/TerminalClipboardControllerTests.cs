using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Services;
using RemoteFlow.TestSupport;
using RemoteFlow.UI.Services;
using RemoteFlow.UI.ViewModels.Terminal;
using Xunit;

namespace RemoteFlow.UI.Tests;

public sealed class TerminalClipboardControllerTests
{
    [AvaloniaFact]
    public async Task MultilinePasteUsesBracketedModeAndNormalizesEveryNewline()
    {
        var token = TestContext.Current.CancellationToken;
        var clipboard = new FakeClipboardService { ReadResult = ClipboardReadResult.Success("if (ok)\r\n  first();\r  second();") };
        var warning = new FakePasteWarningService(new PasteWarningResult(true, false));
        var controller = new TerminalClipboardController(clipboard, new InMemorySettingsStore(), warning);
        var channel = new FakeTerminalChannel();
        await using var session = new TerminalSessionViewModel(channel, new UiDispatcher());

        var result = await controller.PasteAsync(session, token);

        Assert.True(result.Performed);
        Assert.Equal(1, warning.CallCount);
        Assert.Equal(
            "\u001b[200~if (ok)\n  first();\n  second();\u001b[201~",
            Encoding.UTF8.GetString(channel.Written.WrittenSpan));
    }

    [Fact]
    public void CopyTrimsTrailingWhitespaceWithoutChangingUnicodeOrInteriorSpacing()
    {
        const string input = "wide \u754C and e\u0301   \r\ninside   spacing\t \rfinal  ";

        var result = TerminalClipboardController.PrepareCopyText(input);

        Assert.Equal("wide \u754C and e\u0301\ninside   spacing\nfinal", result);
        Assert.Equal(
            Encoding.UTF8.GetBytes("wide \u754C and e\u0301\ninside   spacing\nfinal"),
            Encoding.UTF8.GetBytes(result));
    }

    [AvaloniaFact]
    public async Task RememberedPasteWarningIsPersistedAndNotShownAgain()
    {
        var token = TestContext.Current.CancellationToken;
        var clipboard = new FakeClipboardService { ReadResult = ClipboardReadResult.Success("one\ntwo") };
        var settings = new InMemorySettingsStore();
        var warning = new FakePasteWarningService(new PasteWarningResult(true, true));
        var controller = new TerminalClipboardController(clipboard, settings, warning);
        var channel = new FakeTerminalChannel();
        await using var session = new TerminalSessionViewModel(channel, new UiDispatcher());

        Assert.True((await controller.PasteAsync(session, token)).Performed);
        Assert.True((await controller.PasteAsync(session, token)).Performed);

        Assert.Equal(1, warning.CallCount);
        Assert.True(await settings.Get(SettingKeys.SuppressPasteWarning, token));
    }

    [AvaloniaFact]
    public async Task ClipboardDenialReturnsAnErrorInsteadOfThrowing()
    {
        var token = TestContext.Current.CancellationToken;
        var clipboard = new FakeClipboardService
        {
            ReadResult = ClipboardReadResult.Failure("Clipboard permission was denied."),
        };
        var controller = new TerminalClipboardController(
            clipboard,
            new InMemorySettingsStore(),
            new FakePasteWarningService(new PasteWarningResult(true, false)));
        await using var session = new TerminalSessionViewModel(new FakeTerminalChannel(), new UiDispatcher());

        var result = await controller.PasteAsync(session, token);

        Assert.False(result.Performed);
        Assert.Equal("Clipboard permission was denied.", result.ErrorMessage);
    }

    [AvaloniaFact]
    public async Task CopyOnSelectIsOffByDefaultAndCopiesWhenEnabled()
    {
        var token = TestContext.Current.CancellationToken;
        var clipboard = new FakeClipboardService();
        var settings = new InMemorySettingsStore();
        var controller = new TerminalClipboardController(
            clipboard,
            settings,
            new FakePasteWarningService(new PasteWarningResult(true, false)));
        await using var session = new TerminalSessionViewModel(new FakeTerminalChannel(), new UiDispatcher());
        controller.Attach(session, _ => { });
        session.Model.Feed("selected text");

        session.Model.SelectAll();
        await Task.Delay(50, token);
        Assert.Equal(0, clipboard.WriteCount);

        session.Model.ClearSelection();
        await settings.Set(SettingKeys.CopyOnSelect, true, token);
        session.Model.SelectAll();
        await clipboard.WriteObserved.Task.WaitAsync(TimeSpan.FromSeconds(5), token);

        Assert.Equal(1, clipboard.WriteCount);
        Assert.Contains("selected text", clipboard.WrittenText, StringComparison.Ordinal);
        controller.Detach(session);
    }

    [AvaloniaFact]
    public async Task ModelProvidesWordAndLogicalRowSelectionForDoubleAndTripleClick()
    {
        await using var session = new TerminalSessionViewModel(new FakeTerminalChannel(), new UiDispatcher());
        session.Model.Feed("alpha beta\r\nsecond row");

        session.Model.SelectWordOrExpression(0, 2);
        Assert.Equal("alpha", session.Model.SelectedText);
        session.Model.SelectRow(1);
        Assert.Equal("second row", session.Model.SelectedText.TrimEnd());
    }

    [AvaloniaFact]
    public async Task LargeSingleLinePasteAlsoRequiresConfirmation()
    {
        var token = TestContext.Current.CancellationToken;
        var clipboard = new FakeClipboardService { ReadResult = ClipboardReadResult.Success(new string('x', 4097)) };
        var warning = new FakePasteWarningService(new PasteWarningResult(false, false));
        var controller = new TerminalClipboardController(clipboard, new InMemorySettingsStore(), warning);
        var channel = new FakeTerminalChannel();
        await using var session = new TerminalSessionViewModel(channel, new UiDispatcher());

        var result = await controller.PasteAsync(session, token);

        Assert.False(result.Performed);
        Assert.Equal(1, warning.CallCount);
        Assert.Empty(channel.Written.WrittenSpan.ToArray());
    }

    /// <summary>
    /// A chord reaches the application as two events: the modifier going down, then the key. The terminal
    /// clears its selection for any key it is handed, so the bare modifier used to wipe the selection before
    /// the copy could read it — and every keyboard copy did nothing at all after selecting with the mouse.
    /// Ctrl+Insert is the shortcut this was reported against; Ctrl+Shift+C was equally broken.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(Key.Insert, KeyModifiers.Control, "Ctrl+Insert")]
    [InlineData(Key.C, KeyModifiers.Control | KeyModifiers.Shift, "Ctrl+Shift+C")]
    public async Task ACopyShortcutStillHasASelectionToCopyAfterItsModifierGoesDown(
        Key key,
        KeyModifiers modifiers,
        string gesture)
    {
        var token = TestContext.Current.CancellationToken;
        var clipboard = new FakeClipboardService();
        var session = await ShowWorkspaceWithSelectionAsync(clipboard, token);
        var terminal = session.View.GetVisualDescendants()
            .OfType<SvcSystems.UI.Terminal.TerminalControl>()
            .Single();

        // Exactly what the keyboard sends: Ctrl down, then Shift when the gesture has it, then the key.
        Press(terminal, Key.LeftCtrl, KeyModifiers.Control);
        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            Press(terminal, Key.LeftShift, KeyModifiers.Control | KeyModifiers.Shift);
        }

        Assert.True(
            session.Session.Model.HasSelection,
            $"{gesture}: the selection was gone before the key of the chord arrived.");
        Press(terminal, key, modifiers);
        await clipboard.WriteObserved.Task.WaitAsync(TimeSpan.FromSeconds(5), token);

        // Select-all takes the empty rows below the text with it; what matters is that the text arrived.
        Assert.Equal("selected output", clipboard.WrittenText.TrimEnd('\n'));
        session.Window.Close();
    }

    /// <summary>Typing over a selection still replaces it, which is what the suppression above must not undo:
    /// only a modifier on its own is kept from the terminal, and it sends the shell nothing anyway.</summary>
    [AvaloniaFact]
    public async Task AnOrdinaryKeyStillClearsTheSelection()
    {
        var token = TestContext.Current.CancellationToken;
        var session = await ShowWorkspaceWithSelectionAsync(new FakeClipboardService(), token);
        var terminal = session.View.GetVisualDescendants()
            .OfType<SvcSystems.UI.Terminal.TerminalControl>()
            .Single();

        Press(terminal, Key.A, KeyModifiers.None);

        Assert.False(session.Session.Model.HasSelection);
        session.Window.Close();
    }

    private static void Press(Control target, Key key, KeyModifiers modifiers)
    {
        target.RaiseEvent(new KeyEventArgs
        {
            Key = key,
            KeyModifiers = modifiers,
            RoutedEvent = InputElement.KeyDownEvent,
            Source = target,
        });
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task<(Window Window, RemoteFlow.UI.Views.Terminal.TerminalWorkspace View,
        TerminalSessionViewModel Session)> ShowWorkspaceWithSelectionAsync(
        FakeClipboardService clipboard,
        CancellationToken cancellationToken)
    {
        var controller = new TerminalClipboardController(
            clipboard,
            new InMemorySettingsStore(),
            new FakePasteWarningService(new PasteWarningResult(true, false)));
        var workspace = new TerminalsPageViewModel(
            new UnusedPtyService(),
            new UiDispatcher(),
            new InMemorySettingsStore(),
            new AlwaysConfirmService(),
            new KeymapService(),
            controller);
        var channel = new FakeTerminalChannel();
        var session = new TerminalSessionViewModel(channel, new UiDispatcher());
        workspace.AddWorkspaceSession(session);
        var view = new RemoteFlow.UI.Views.Terminal.TerminalWorkspace { DataContext = workspace };
        var window = new Window { Width = 900, Height = 600, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        await channel.PublishAsync("selected output", cancellationToken);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (session.OutputFramesApplied == 0 && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10, cancellationToken);
        }

        session.Model.SelectAll();
        Dispatcher.UIThread.RunJobs();
        Assert.True(session.Model.HasSelection, "The test could not create a selection to copy.");
        return (window, view, session);
    }

    private sealed class FakeClipboardService : IClipboardService
    {
        public ClipboardReadResult ReadResult { get; set; } = ClipboardReadResult.Success(null);

        public int WriteCount { get; private set; }

        public string WrittenText { get; private set; } = string.Empty;

        public TaskCompletionSource WriteObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ClipboardReadResult> ReadTextAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ReadResult);
        }

        public Task<ClipboardWriteResult> WriteTextAsync(
            string text,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteCount++;
            WrittenText = text;
            _ = WriteObserved.TrySetResult();
            return Task.FromResult(ClipboardWriteResult.Success);
        }
    }

    private sealed class FakePasteWarningService(PasteWarningResult result) : IPasteWarningService
    {
        public int CallCount { get; private set; }

        public Task<PasteWarningResult> ConfirmAsync(
            int lineCount,
            int utf8ByteCount,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class UnusedPtyService : IPtyService
    {
        public Task<IPtySession> SpawnAsync(
            PtySpawnOptions options,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("This test adds its session directly.");
        }
    }

    private sealed class AlwaysConfirmService : IConfirmationDialogService
    {
        public Task<bool> ConfirmAsync(
            string title,
            string message,
            string confirmLabel,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(true);
        }
    }

    private sealed class FakeTerminalChannel : ITerminalChannel
    {
        private readonly Pipe _pipe = new();
        private readonly TaskCompletionSource<int?> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ArrayBufferWriter<byte> Written { get; } = new();

        public async Task PublishAsync(string text, CancellationToken cancellationToken)
        {
            _ = await _pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken);
        }

        public PipeReader Output => _pipe.Reader;

        public Task<int?> Exited => _exited.Task;

        public event EventHandler<ChannelClosedEventArgs>? Closed;

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Written.Write(data.Span);
            return ValueTask.CompletedTask;
        }

        public ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            await _pipe.Writer.CompleteAsync();
            if (_exited.TrySetResult(null))
            {
                Closed?.Invoke(this, new ChannelClosedEventArgs(null, true));
            }
        }
    }
}
