using System.Diagnostics;
using System.IO.Pipelines;
using System.Text;
using Avalonia.Headless.XUnit;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.UI.Services;
using RemoteFlow.UI.ViewModels.Terminal;
using Xunit;

namespace RemoteFlow.UI.Tests;

public sealed class TerminalSearchTests
{
    [AvaloniaFact]
    public async Task NativeSearchHighlightsAllMatchesAndNavigatesBothDirections()
    {
        await using var session = new TerminalSessionViewModel(new FakeTerminalChannel(), new ImmediateDispatcher());
        session.Model.Feed("alpha one\r\nalpha two\r\nALPHA three\r\n");

        session.OpenFind();
        session.SearchText = "alpha";

        Assert.Equal(3, session.SearchMatchCount);
        Assert.Equal(3, session.Model.SearchResultCount);
        session.FindNext();
        Assert.Equal("2 / 3", session.SearchStatus);
        session.FindPrevious();
        Assert.Equal("1 / 3", session.SearchStatus);
    }

    [AvaloniaFact]
    public async Task CaseSensitiveRegexUsesBufferSnapshotAndInvalidPatternIsReported()
    {
        await using var session = new TerminalSessionViewModel(new FakeTerminalChannel(), new ImmediateDispatcher());
        session.Model.Feed("Error1 error2 Error3 warning\r\n");
        session.OpenFind();
        session.IsSearchCaseSensitive = true;
        session.IsSearchRegex = true;
        session.SearchText = "Error\\d";

        Assert.Equal(2, session.SearchMatchCount);
        Assert.Null(session.SearchError);
        session.FindNext();
        Assert.Equal("1 / 2", session.SearchStatus);

        session.SearchText = "[unterminated";

        Assert.NotNull(session.SearchError);
        Assert.Equal("Invalid regular expression", session.SearchStatus);
        Assert.Equal(0, session.SearchMatchCount);
    }

    [AvaloniaFact]
    public async Task IncomingOutputPreservesScrolledViewportButFollowsWhenAlreadyAtBottom()
    {
        var token = TestContext.Current.CancellationToken;
        var channel = new FakeTerminalChannel();
        await using var session = new TerminalSessionViewModel(channel, new ImmediateDispatcher());
        session.Model.Feed(string.Join("\r\n", Enumerable.Range(1, 200).Select(index => $"history {index}")));
        session.Model.ScrollToYDisp(20);
        var viewport = session.Model.Terminal.Buffer.YDisp;
        Assert.False(session.Model.Terminal.Buffer.IsAtBottom);

        await channel.PublishAsync("\r\nPRESERVE_VIEWPORT\r\n", token);
        await UntilAsync(
            () => session.Model.SearchService.GetSnapshot().Text.Contains("PRESERVE_VIEWPORT", StringComparison.Ordinal),
            token);

        Assert.Equal(viewport, session.Model.Terminal.Buffer.YDisp);

        session.Model.Terminal.Buffer.ScrollToBottom();
        Assert.True(session.Model.Terminal.Buffer.IsAtBottom);
        await channel.PublishAsync("\r\nFOLLOW_BOTTOM\r\n", token);
        await UntilAsync(
            () => session.Model.SearchService.GetSnapshot().Text.Contains("FOLLOW_BOTTOM", StringComparison.Ordinal),
            token);

        Assert.True(session.Model.Terminal.Buffer.IsAtBottom);
    }

    [AvaloniaFact]
    public async Task TenThousandLinesCanBeScrolledWithoutStutter()
    {
        await using var session = new TerminalSessionViewModel(new FakeTerminalChannel(), new ImmediateDispatcher());
        session.Model.Feed(string.Join("\r\n", Enumerable.Range(1, 10_000).Select(index => $"line {index:D5}")));
        var started = Stopwatch.GetTimestamp();

        for (var index = 0; index < 250; index++)
        {
            session.Model.ScrollLines(index % 2 == 0 ? -3 : 3);
        }

        var elapsed = Stopwatch.GetElapsedTime(started);
        Assert.True(elapsed < TimeSpan.FromSeconds(2), $"Scrolling took {elapsed.TotalMilliseconds:F0} ms.");
    }

    private static async Task UntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The output did not reach the terminal within five seconds.");
            await Task.Delay(10, cancellationToken);
        }
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public ValueTask InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            action();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeTerminalChannel : ITerminalChannel
    {
        private readonly Pipe _pipe = new();
        private readonly TaskCompletionSource<int?> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PipeReader Output => _pipe.Reader;
        public Task<int?> Exited => _exited.Task;
        public event EventHandler<ChannelClosedEventArgs>? Closed;

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default)
        {
            return ValueTask.CompletedTask;
        }

        public async Task PublishAsync(string text, CancellationToken cancellationToken)
        {
            _ = await _pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken);
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
