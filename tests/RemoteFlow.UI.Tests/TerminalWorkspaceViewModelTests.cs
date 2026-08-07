using System.IO.Pipelines;
using System.Text;
using Avalonia.Headless.XUnit;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Domain.Enums;
using RemoteFlow.TestSupport;
using RemoteFlow.UI.Services;
using RemoteFlow.UI.ViewModels.Terminal;
using Xunit;

namespace RemoteFlow.UI.Tests;

public sealed class TerminalWorkspaceViewModelTests
{
    [AvaloniaFact]
    public async Task TenSessionsRunIndependentlyAndClosingAllDisposesEveryProcess()
    {
        var token = TestContext.Current.CancellationToken;
        var pty = new RecordingPtyService();
        var confirmation = new RecordingConfirmationService(true);
        await using var workspace = new TerminalsPageViewModel(
            pty,
            new UiDispatcher(),
            new InMemorySettingsStore(),
            confirmation);

        for (var index = 0; index < 10; index++)
        {
            _ = await workspace.AddLocalSessionAsync(token);
        }

        var publications = pty.Sessions.Select((session, index) =>
            session.PublishAsync($"\u001b]2;session-{index}\a", token));
        await Task.WhenAll(publications);
        await UntilAsync(
            () => workspace.Sessions.Select((session, index) =>
                session.Title == $"session-{index}").All(found => found),
            token);

        for (var index = 0; index < 10; index++)
        {
            Assert.Equal($"session-{index}", workspace.Sessions[index].Title);
            Assert.Equal(index + 1000, workspace.Sessions[index].ProcessId);
        }

        Assert.True(await workspace.RequestCloseAllAsync(token));
        Assert.Empty(workspace.Sessions);
        Assert.All(pty.Sessions, session => Assert.True(session.IsDisposed));
        Assert.Equal(1, confirmation.CallCount);
    }

    [AvaloniaFact]
    public async Task ReorderAndKeyboardStyleSelectionPersistForTheRun()
    {
        var token = TestContext.Current.CancellationToken;
        var pty = new RecordingPtyService();
        await using var workspace = new TerminalsPageViewModel(pty, new UiDispatcher());
        for (var index = 0; index < 30; index++)
        {
            _ = await workspace.AddLocalSessionAsync(token);
        }

        var first = workspace.Sessions[0];
        var tenth = workspace.Sessions[9];
        workspace.MoveSession(tenth, first);
        workspace.SelectSession(1);

        Assert.Same(tenth, workspace.Sessions[0]);
        Assert.Same(tenth, workspace.SelectedSession);
        workspace.CycleSession();
        Assert.Same(first, workspace.SelectedSession);
        Assert.Equal(30, workspace.Sessions.Count);
    }

    [AvaloniaFact]
    public async Task OscTitleUpdatesLiveButUserOverrideWins()
    {
        var token = TestContext.Current.CancellationToken;
        var channel = new FakePtySession(42);
        await using var session = new TerminalSessionViewModel(channel, new UiDispatcher());

        await channel.PublishAsync("\u001b]2;build server\a", token);
        await UntilAsync(() => session.Title == "build server", token);
        session.SetTitleOverride("Pinned title");
        await channel.PublishAsync("\u001b]0;ignored title\u001b\\", token);
        await Task.Delay(25, token);

        Assert.Equal("Pinned title", session.Title);
        session.SetTitleOverride(null);
        await channel.PublishAsync("\u001b]2;live again\a", token);
        await UntilAsync(() => session.Title == "live again", token);
    }

    [AvaloniaFact]
    public async Task ProductionTabHasTextCueAndAccessibleAccent()
    {
        var channel = new FakePtySession(7);
        await using var session = new TerminalSessionViewModel(
            channel,
            new UiDispatcher(),
            initialTitle: "payments",
            environment: EnvironmentKind.Production);

        Assert.Equal("PROD !", session.EnvironmentCue);
        Assert.True(session.IsProduction);
        Assert.True(ContrastRatio(session.AccentColorHex, "#0B0F14") >= 4.5);
    }

    [AvaloniaFact]
    public async Task WindowClosePromptListsAllLiveSessionsOnce()
    {
        var token = TestContext.Current.CancellationToken;
        var confirmation = new RecordingConfirmationService(false);
        await using var workspace = new TerminalsPageViewModel(
            new RecordingPtyService(),
            new UiDispatcher(),
            new InMemorySettingsStore(),
            confirmation);
        _ = await workspace.AddLocalSessionAsync(token);
        var second = await workspace.AddLocalSessionAsync(token);
        second!.SetTitleOverride("database migration");

        Assert.False(await workspace.RequestCloseAllAsync(token));
        Assert.Equal(1, confirmation.CallCount);
        Assert.Contains("Local shell", confirmation.LastMessage, StringComparison.Ordinal);
        Assert.Contains("database migration", confirmation.LastMessage, StringComparison.Ordinal);
        Assert.Equal(2, workspace.Sessions.Count);
    }

    private static async Task UntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The asynchronous condition was not reached within five seconds.");
            await Task.Delay(10, cancellationToken);
        }
    }

    private static double ContrastRatio(string foreground, string background)
    {
        static double Luminance(string color)
        {
            static double Channel(int value)
            {
                var component = value / 255d;
                return component <= 0.03928
                    ? component / 12.92
                    : Math.Pow((component + 0.055) / 1.055, 2.4);
            }

            var red = Convert.ToInt32(color.Substring(1, 2), 16);
            var green = Convert.ToInt32(color.Substring(3, 2), 16);
            var blue = Convert.ToInt32(color.Substring(5, 2), 16);
            return (0.2126 * Channel(red)) + (0.7152 * Channel(green)) + (0.0722 * Channel(blue));
        }

        var first = Luminance(foreground);
        var second = Luminance(background);
        return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
    }

    private sealed class RecordingConfirmationService(bool result) : IConfirmationDialogService
    {
        public int CallCount { get; private set; }

        public string LastMessage { get; private set; } = string.Empty;

        public Task<bool> ConfirmAsync(
            string title,
            string message,
            string confirmLabel,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastMessage = message;
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingPtyService : IPtyService
    {
        public List<FakePtySession> Sessions { get; } = [];

        public Task<IPtySession> SpawnAsync(
            PtySpawnOptions options,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var session = new FakePtySession(Sessions.Count + 1000);
            Sessions.Add(session);
            return Task.FromResult<IPtySession>(session);
        }
    }

    private sealed class FakePtySession(int processId) : IPtySession
    {
        private readonly Pipe _pipe = new();
        private readonly TaskCompletionSource<int?> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ProcessId { get; } = processId;

        public bool IsDisposed { get; private set; }

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
            if (IsDisposed)
            {
                return;
            }

            IsDisposed = true;
            await _pipe.Writer.CompleteAsync();
            if (_exited.TrySetResult(null))
            {
                Closed?.Invoke(this, new ChannelClosedEventArgs(null, true));
            }
        }

        public async Task PublishAsync(string text, CancellationToken cancellationToken)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            _ = await _pipe.Writer.WriteAsync(bytes, cancellationToken);
        }
    }
}
