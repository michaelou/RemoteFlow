using System.IO.Pipelines;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    public async Task MixedSessionsShareSelectionCyclingReorderAndCloseLifecycle()
    {
        var token = TestContext.Current.CancellationToken;
        await using var workspace = new TerminalWorkspaceViewModel(new RecordingPtyService(), new UiDispatcher());
        var terminal = await workspace.AddLocalSessionAsync(token);
        var firstRdp = new FakeWorkspaceSession("DC01", "RDP");
        var secondRdp = new FakeWorkspaceSession("SQL01", "RDP");
        workspace.AddWorkspaceSession(firstRdp);
        workspace.AddWorkspaceSession(secondRdp);

        workspace.SelectSession(1);
        Assert.Same(terminal, workspace.SelectedSession);
        workspace.CycleSession();
        Assert.Same(firstRdp, workspace.SelectedSession);
        workspace.CycleSession();
        Assert.Same(secondRdp, workspace.SelectedSession);
        workspace.CycleSession();
        Assert.Same(terminal, workspace.SelectedSession);

        workspace.MoveSession(secondRdp, terminal!);
        Assert.Same(secondRdp, workspace.Sessions[0]);
        workspace.SelectSession(secondRdp);
        Assert.True(await workspace.CloseSessionAsync(secondRdp, skipConfirmation: true, token));
        Assert.True(secondRdp.IsDisposed);
        Assert.Same(terminal, workspace.SelectedSession);
    }

    [AvaloniaFact]
    public async Task EverySessionContentStaysAttachedAndOnlySelectionIsVisible()
    {
        var token = TestContext.Current.CancellationToken;
        await using var workspace = new TerminalWorkspaceViewModel(new RecordingPtyService(), new UiDispatcher());
        var terminal = await workspace.AddLocalSessionAsync(token);
        var sessions = Enumerable.Range(1, 2)
            .Select(index => new FakeWorkspaceSession($"RDP {index}", "RDP"))
            .ToArray();
        foreach (var session in sessions)
        {
            workspace.AddWorkspaceSession(session);
        }

        var view = new RemoteFlow.UI.Views.Terminal.TerminalWorkspace { DataContext = workspace };
        var window = new Window { Content = view };
        window.Show();

        var hosts = view.GetVisualDescendants()
            .OfType<RemoteFlow.UI.Views.Terminal.WorkspaceSessionContentHost>()
            .ToArray();
        Assert.Equal(3, hosts.Length);
        Assert.All(sessions, session => Assert.Equal(1, session.CreateContentCount));
        var visibleHost = Assert.Single(hosts, host => host.IsVisible);
        Assert.Same(sessions[1], visibleHost.Session);

        workspace.SelectSession(terminal);
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(3, hosts.Length);
        Assert.Same(terminal, Assert.Single(hosts, host => host.IsVisible).Session);
        Assert.NotNull(view.GetVisualDescendants().OfType<SvcSystems.UI.Terminal.TerminalControl>().SingleOrDefault());
        Assert.All(sessions, session => Assert.Equal(1, session.CreateContentCount));
        window.Close();
    }

    [AvaloniaFact]
    public async Task NativeSessionFocusEscapeMovesFocusToItsVisibleTab()
    {
        await using var workspace = new TerminalWorkspaceViewModel(new RecordingPtyService(), new UiDispatcher());
        var session = new FakeWorkspaceSession("DC01", "RDP");
        workspace.AddWorkspaceSession(session);
        var view = new RemoteFlow.UI.Views.Terminal.TerminalWorkspace { DataContext = workspace };
        var window = new Window { Content = view };
        window.Show();

        var handled = RemoteFlow.UI.Views.Terminal.WorkspaceSessionContentHost.RequestFocusEscape(
            session.LastContent!);
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var tab = view.GetVisualDescendants()
            .OfType<Border>()
            .Single(border => border.Focusable && ReferenceEquals(border.DataContext, session));
        Assert.True(handled);
        Assert.True(tab.Focusable);
        Assert.Contains("session-tab", tab.Classes);
        window.Close();
    }

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
            Assert.Equal(index + 1000, Assert.IsType<TerminalSessionViewModel>(workspace.Sessions[index]).ProcessId);
        }

        Assert.True(await workspace.RequestCloseAllAsync(token));
        Assert.Empty(workspace.Sessions);
        Assert.All(pty.Sessions, session => Assert.True(session.IsDisposed));
        Assert.Equal(1, confirmation.CallCount);
    }

    [AvaloniaFact]
    public async Task RequestCloseAllDisposesRdpSessionsAlongsideTerminals()
    {
        var token = TestContext.Current.CancellationToken;
        var pty = new RecordingPtyService();
        var confirmation = new RecordingConfirmationService(true);
        await using var workspace = new TerminalsPageViewModel(
            pty,
            new UiDispatcher(),
            new InMemorySettingsStore(),
            confirmation);
        _ = await workspace.AddLocalSessionAsync(token);
        var rdp = new FakeWorkspaceSession("DC01", "RDP");
        workspace.AddWorkspaceSession(rdp);

        Assert.True(await workspace.RequestCloseAllAsync(token));

        Assert.Empty(workspace.Sessions);
        Assert.True(rdp.IsDisposed);
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
        // Waiting for the frame to be applied, rather than sleeping and hoping, is what makes "the title
        // did not change" mean the title was offered and refused, not merely that it had not arrived.
        var framesBefore = session.OutputFramesApplied;
        await channel.PublishAsync("\u001b]0;ignored title\u001b\\", token);
        await UntilAsync(() => session.OutputFramesApplied > framesBefore, token);

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

    private sealed class FakeWorkspaceSession(string title, string protocol) : ObservableObject,
        IWorkspaceSessionViewModel,
        IWorkspaceSessionContentProvider
    {
        public string Title { get; } = title;

        public string TabTitle => Title;

        public EnvironmentKind Environment => EnvironmentKind.Production;

        public string AccentColorHex => "#FF7B72";

        public string TabBackgroundHex => "#121821";

        public string ChromeTintHex => "#101418";

        public string EnvironmentCue => "PROD !";

        public string ProtocolCue { get; } = protocol;

        public string StatusText => "Connected";

        public string TabAccessibleName => $"{Title}, {ProtocolCue}, production, Connected";

        public string CloseTabAccessibleName => $"Close {ProtocolCue} session {Title}";

        public bool IsActive { get; private set; }

        public bool IsLive => true;

        public bool IsEnded => false;

        public bool CanOpenInSystemTerminal => false;

        public string? EndedMessage => null;

        public string RecoveryActionLabel => "Reconnect";

        public IAsyncRelayCommand RetryCommand { get; } = new AsyncRelayCommand(() => Task.CompletedTask);

        public int CreateContentCount { get; private set; }

        public Border? LastContent { get; private set; }

        public bool IsDisposed { get; private set; }

        public void SetActive(bool isActive)
        {
            IsActive = isActive;
            OnPropertyChanged(nameof(IsActive));
        }

        public Control CreateSessionContent()
        {
            CreateContentCount++;
            LastContent = new Border { DataContext = this };
            return LastContent;
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
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
