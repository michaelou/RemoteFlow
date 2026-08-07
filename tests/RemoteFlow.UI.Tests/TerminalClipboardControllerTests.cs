using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using Avalonia.Headless.XUnit;
using RemoteFlow.Application.Abstractions;
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

    private sealed class FakeTerminalChannel : ITerminalChannel
    {
        private readonly Pipe _pipe = new();
        private readonly TaskCompletionSource<int?> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ArrayBufferWriter<byte> Written { get; } = new();

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
