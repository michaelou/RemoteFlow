using System.IO.Pipelines;
using System.Text;
using Avalonia.Headless.XUnit;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.UI.Services;
using RemoteFlow.UI.ViewModels.Terminal;
using Xunit;

namespace RemoteFlow.UI.Tests;

/// <summary>
/// What the terminal shows while a line is being edited in the middle.
/// </summary>
/// <remarks>
/// <para>
/// Every stream fed here is what <c>bash</c> actually writes, captured from a pseudoterminal at
/// <c>TERM=xterm-256color</c>: readline opens a gap with <c>CSI Ps @</c> and then prints the inserted
/// characters, rather than reprinting the tail of the line. XTerm.NET 1.0.15 answers that sequence with a
/// forward copy of the row over itself, which repeats the cell at the cursor across the rest of the row —
/// see <c>InsertCharacterCorrection</c>. The shell's own line buffer is never involved, so the command that
/// ran on Enter was always right and only the screen was wrong; these assertions are therefore about the
/// buffer the renderer reads, not about what the shell received.
/// </para>
/// <para>
/// A replay rather than a live shell: the defect is in what the emulator does with a fixed sequence of
/// bytes, and a real readline would also make the assertions depend on the developer's own
/// <c>~/.bashrc</c> and on the width the pseudoterminal was given.
/// </para>
/// </remarks>
public sealed class TerminalInsertCharacterTests
{
    private const string _escape = "\u001b";

    [AvaloniaFact]
    public async Task TypingInTheMiddleOfALineLeavesTheRestOfItAlone()
    {
        var token = TestContext.Current.CancellationToken;
        var channel = new ReplayChannel();
        await using var session = new TerminalSessionViewModel(channel, new UiDispatcher());

        // bash for: type "abcdefgh", press Left three times, type "X".
        await PublishAsync(session, channel, $"abcdefgh\b\b\b{_escape}[1@X", token);

        Assert.Equal("abcdeXfgh", Row(session, 0));
    }

    [AvaloniaFact]
    public async Task TypingWhereTheCursorSitsOnASpaceKeepsTheTailOfTheLine()
    {
        var token = TestContext.Current.CancellationToken;
        var channel = new ReplayChannel();
        await using var session = new TerminalSessionViewModel(channel, new UiDispatcher());

        // The shape a paste leaves behind: the cell being pushed right is a space, so the smear was a run of
        // spaces and the rest of the line looked deleted.
        await PublishAsync(session, channel, $"ab cdefgh{new string('\b', 7)}{_escape}[1@Z", token);

        Assert.Equal("abZ cdefgh", Row(session, 0));
    }

    [AvaloniaFact]
    public async Task PastingSeveralCharactersMidLineInsertsThatManyColumns()
    {
        var token = TestContext.Current.CancellationToken;
        var channel = new ReplayChannel();
        await using var session = new TerminalSessionViewModel(channel, new UiDispatcher());

        await PublishAsync(session, channel, $"abcdefgh\b\b\b{_escape}[3@XYZ", token);

        Assert.Equal("abcdeXYZfgh", Row(session, 0));
    }

    [AvaloniaFact]
    public async Task ASequenceSplitAcrossTwoOutputFramesIsStillRecognised()
    {
        var token = TestContext.Current.CancellationToken;
        var channel = new ReplayChannel();
        await using var session = new TerminalSessionViewModel(channel, new UiDispatcher());

        // The pseudoterminal ends a read wherever it likes, so the sequence arrives in halves. Feeding the
        // first half to the emulator would leave its parser inside a sequence it must never see.
        await PublishAsync(session, channel, $"abcdefgh\b\b\b{_escape}[", token);
        await PublishAsync(session, channel, "1@X", token);

        Assert.Equal("abcdeXfgh", Row(session, 0));
    }

    [AvaloniaFact]
    public async Task DeletingInTheMiddleOfALineStillClosesTheGap()
    {
        var token = TestContext.Current.CancellationToken;
        var channel = new ReplayChannel();
        await using var session = new TerminalSessionViewModel(channel, new UiDispatcher());

        // Delete-characters shifts left, which the emulator gets right; the correction must not have taken
        // it, or anything else that ends in a byte other than @, out of the stream.
        await PublishAsync(session, channel, $"abcdefgh\b\b\b{_escape}[1P", token);

        Assert.Equal("abcdegh", Row(session, 0));
    }

    [AvaloniaFact]
    public async Task AnInsertWiderThanTheRowPushesTheTailOffIt()
    {
        var token = TestContext.Current.CancellationToken;
        var channel = new ReplayChannel();
        await using var session = new TerminalSessionViewModel(channel, new UiDispatcher());

        // More columns than the row has left: everything after the cursor leaves the row rather than the
        // copy running off the end of it.
        await PublishAsync(session, channel, $"abcdefgh{new string('\b', 6)}{_escape}[200@", token);

        Assert.Equal("ab", Row(session, 0));
    }

    [AvaloniaFact]
    public async Task AnInsertAtTheLastColumnLeavesTheRowAlone()
    {
        var token = TestContext.Current.CancellationToken;
        var channel = new ReplayChannel();
        await using var session = new TerminalSessionViewModel(channel, new UiDispatcher());
        var columns = session.Model.Terminal.Cols;

        // Printing in the last column leaves the cursor one past it, waiting to wrap. There is no column to
        // shift into from there, and reading that position as if it were inside the row would corrupt it.
        await PublishAsync(session, channel, $"{_escape}[{columns}Gx{_escape}[1@", token);

        Assert.Equal(new string(' ', columns - 1) + "x", Row(session, 0));
    }

    private static string Row(TerminalSessionViewModel session, int row)
    {
        var buffer = session.Model.Terminal.Buffer;
        return buffer.Lines[buffer.YBase + row]?.TranslateToString(true) ?? string.Empty;
    }

    private static async Task PublishAsync(
        TerminalSessionViewModel session,
        ReplayChannel channel,
        string text,
        CancellationToken cancellationToken)
    {
        var framesBefore = session.OutputFramesApplied;
        await channel.PublishAsync(text, cancellationToken);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (session.OutputFramesApplied == framesBefore)
        {
            Assert.True(DateTime.UtcNow < deadline, "The output frame was not applied within five seconds.");
            await Task.Delay(10, cancellationToken);
        }
    }

    /// <summary>A channel that replays captured shell output and accepts no input.</summary>
    private sealed class ReplayChannel : ITerminalChannel
    {
        private readonly Pipe _pipe = new();
        private readonly TaskCompletionSource<int?> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PipeReader Output => _pipe.Reader;

        public Task<int?> Exited => _exited.Task;

        public event EventHandler<ChannelClosedEventArgs>? Closed;

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

        public async Task PublishAsync(string text, CancellationToken cancellationToken)
        {
            _ = await _pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken);
        }
    }
}
