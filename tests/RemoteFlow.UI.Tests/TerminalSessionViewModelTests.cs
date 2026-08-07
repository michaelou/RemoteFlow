using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using Avalonia.Headless.XUnit;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Domain.Enums;
using RemoteFlow.UI.Services;
using RemoteFlow.UI.ViewModels.Terminal;
using Xunit;

namespace RemoteFlow.UI.Tests;

public sealed class TerminalSessionViewModelTests
{
    [Fact]
    public void StatefulDecoderPreservesEmojiSplitAcrossTwoReads()
    {
        var decoder = new Utf8StreamDecoder();
        var emoji = Encoding.UTF8.GetBytes("🙂");

        var first = decoder.Decode(new ReadOnlySequence<byte>(emoji.AsMemory(0, 2)));
        var second = decoder.Decode(new ReadOnlySequence<byte>(emoji.AsMemory(2, 2)));

        Assert.Equal(string.Empty, first);
        Assert.Equal("🙂", second);
    }

    [AvaloniaFact]
    public async Task SplitOutputFeedsTheModelAndClosureShowsExitCode()
    {
        var token = TestContext.Current.CancellationToken;
        var channel = new FakeTerminalChannel();
        var dispatcher = new ImmediateDispatcher();
        await using var viewModel = new TerminalSessionViewModel(channel, dispatcher);
        var emoji = Encoding.UTF8.GetBytes("🙂");

        await channel.PublishAsync(emoji.AsMemory(0, 2), token);
        await channel.PublishAsync(emoji.AsMemory(2, 2), token);
        await channel.CompleteAsync(23);
        await viewModel.Completion.WaitAsync(TimeSpan.FromSeconds(5), token);

        Assert.Equal(1, viewModel.Model.Search("🙂"));
        Assert.Equal(SessionState.Closed, viewModel.State);
        Assert.Equal("Session ended (exit code 23).", viewModel.EndedMessage);
        Assert.True(dispatcher.InvocationCount > 0);
    }

    [AvaloniaFact]
    public async Task ModelUserInputReachesChannelUnchanged()
    {
        var token = TestContext.Current.CancellationToken;
        var channel = new FakeTerminalChannel();
        await using var viewModel = new TerminalSessionViewModel(channel, new ImmediateDispatcher());

        viewModel.Model.Send("hello");
        await channel.WriteReceived.Task.WaitAsync(TimeSpan.FromSeconds(5), token);

        Assert.Equal(Encoding.UTF8.GetBytes("hello"), channel.Written.WrittenSpan.ToArray());
    }

    [AvaloniaFact]
    public async Task RapidResizeUsesOneTrailingCallWithTheLatestDimensions()
    {
        var token = TestContext.Current.CancellationToken;
        var channel = new FakeTerminalChannel();
        await using var viewModel = new TerminalSessionViewModel(channel, new ImmediateDispatcher());

        viewModel.RequestResize(80, 24);
        await Task.Delay(20, token);
        viewModel.RequestResize(100, 30);
        await Task.Delay(20, token);
        viewModel.RequestResize(132, 43);

        await channel.ResizeReceived.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
        await Task.Delay(TerminalSessionViewModel.ResizeDebounce + TimeSpan.FromMilliseconds(30), token);

        Assert.Equal(1, channel.ResizeCallCount);
        Assert.Equal((132, 43), channel.LastResize);
    }

    [AvaloniaFact]
    public async Task SustainedFloodDropsOldestOutputAndMarksTruncation()
    {
        var token = TestContext.Current.CancellationToken;
        var channel = new FakeTerminalChannel();
        await using var viewModel = new TerminalSessionViewModel(channel, new ImmediateDispatcher());
        var flood = new byte[TerminalSessionViewModel.MaximumPendingOutputBytes + (512 * 1024)];
        Array.Fill(flood, (byte)'x');

        await channel.PublishAsync(flood, token);
        await channel.CompleteAsync(0);
        await viewModel.Completion.WaitAsync(TimeSpan.FromSeconds(10), token);

        Assert.True(viewModel.DroppedOutputBytes >= 512 * 1024);
        Assert.Equal(1, viewModel.Model.Search("RemoteFlow: output truncated"));
    }

    [AvaloniaFact]
    public async Task TenThousandLineScrollbackStaysWithinInitialMemoryBudget()
    {
        var channel = new FakeTerminalChannel();
        await using var viewModel = new TerminalSessionViewModel(channel, new ImmediateDispatcher());
        var before = GC.GetTotalMemory(forceFullCollection: true);

        viewModel.Model.Feed(string.Join("\r\n", Enumerable.Range(1, 10_000).Select(index => $"line {index:D5}")));
        var retained = GC.GetTotalMemory(forceFullCollection: true) - before;

        Assert.True(viewModel.Model.Terminal.Buffer.Lines.Length <= 10_030);
        Assert.True(retained < 100 * 1024 * 1024, $"10,000-line scrollback retained {retained:N0} bytes.");
    }

    [AvaloniaFact]
    public async Task DisposingViewModelDisposesPendingChannelWithoutTaskFailure()
    {
        var token = TestContext.Current.CancellationToken;
        var channel = new FakeTerminalChannel();
        var viewModel = new TerminalSessionViewModel(channel, new ImmediateDispatcher());

        await viewModel.DisposeAsync();
        await viewModel.Completion.WaitAsync(TimeSpan.FromSeconds(5), token);

        Assert.True(channel.IsDisposed);
        Assert.Equal(SessionState.Closed, viewModel.State);
        Assert.Equal("Session ended (terminated).", viewModel.EndedMessage);
    }

    [Fact]
    public async Task TerminalsPageStartsChannelThroughPtyService()
    {
        var token = TestContext.Current.CancellationToken;
        var channel = new FakeTerminalChannel();
        var service = new RecordingPtyService(channel);
        using var page = new TerminalsPageViewModel(service, new ImmediateDispatcher());

        await page.InitializeAsync(token);

        Assert.NotNull(page.Session);
        Assert.NotNull(service.Options);
        Assert.Equal("xterm-256color", service.Options.EnvironmentVariables["TERM"]);
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public int InvocationCount { get; private set; }

        public ValueTask InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InvocationCount++;
            action();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingPtyService(ITerminalChannel channel) : IPtyService
    {
        public PtySpawnOptions? Options { get; private set; }

        public Task<IPtySession> SpawnAsync(
            PtySpawnOptions options,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Options = options;
            return Task.FromResult<IPtySession>(new SessionAdapter(channel));
        }
    }

    private sealed class SessionAdapter(ITerminalChannel channel) : IPtySession
    {
        public int ProcessId => 42;
        public PipeReader Output => channel.Output;
        public Task<int?> Exited => channel.Exited;

        public event EventHandler<ChannelClosedEventArgs>? Closed
        {
            add => channel.Closed += value;
            remove => channel.Closed -= value;
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            return channel.WriteAsync(data, cancellationToken);
        }

        public ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default)
        {
            return channel.ResizeAsync(columns, rows, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            return channel.DisposeAsync();
        }
    }

    private sealed class FakeTerminalChannel : ITerminalChannel
    {
        private readonly Pipe _pipe = new();
        private readonly TaskCompletionSource<int?> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PipeReader Output => _pipe.Reader;
        public Task<int?> Exited => _exited.Task;
        public ArrayBufferWriter<byte> Written { get; } = new();
        public TaskCompletionSource WriteReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ResizeReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsDisposed { get; private set; }
        public int ResizeCallCount { get; private set; }
        public (int Columns, int Rows) LastResize { get; private set; }

        public event EventHandler<ChannelClosedEventArgs>? Closed;

        public ValueTask WriteAsync(
            ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Written.Write(data.Span);
            _ = WriteReceived.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResizeCallCount++;
            LastResize = (columns, rows);
            _ = ResizeReceived.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            if (IsDisposed)
            {
                return;
            }

            IsDisposed = true;
            await CompleteAsync(null);
        }

        public async Task PublishAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            _ = await _pipe.Writer.WriteAsync(data, cancellationToken);
        }

        public async Task CompleteAsync(int? exitCode)
        {
            await _pipe.Writer.CompleteAsync();
            if (_exited.TrySetResult(exitCode))
            {
                Closed?.Invoke(this, new ChannelClosedEventArgs(exitCode, exitCode is null));
            }
        }
    }
}
