using System.IO.Pipelines;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Services;
using RemoteFlow.TestSupport;
using RemoteFlow.UI.Input;
using RemoteFlow.UI.Services;
using RemoteFlow.UI.ViewModels.Terminal;
using Xunit;

namespace RemoteFlow.UI.Tests;

public sealed class TerminalInputRoutingTests
{
    /// <summary>
    /// TerminalControl encodes ordinary strokes itself and raises UserInput. If the router also
    /// claimed them the PTY would receive every keystroke twice.
    /// </summary>
    [Theory]
    [InlineData(Key.A, KeyModifiers.None)]
    [InlineData(Key.A, KeyModifiers.Shift)]
    [InlineData(Key.D1, KeyModifiers.None)]
    [InlineData(Key.Up, KeyModifiers.None)]
    [InlineData(Key.Home, KeyModifiers.None)]
    [InlineData(Key.F5, KeyModifiers.None)]
    [InlineData(Key.C, KeyModifiers.Control)]
    [InlineData(Key.D, KeyModifiers.Control)]
    public void TerminalStrokesAreLeftToTheTerminalControl(Key key, KeyModifiers modifiers)
    {
        var router = new TerminalInputRouter(new KeymapService());
        using var workspace = new TerminalsPageViewModel();

        Assert.Null(router.Resolve(KeyDown(key, modifiers), workspace));
    }

    [Theory]
    [InlineData(Key.T, KeyModifiers.Control | KeyModifiers.Shift, KeymapCommand.NewTerminal)]
    [InlineData(Key.W, KeyModifiers.Control | KeyModifiers.Shift, KeymapCommand.CloseTerminal)]
    [InlineData(Key.F, KeyModifiers.Control | KeyModifiers.Shift, KeymapCommand.FindTerminal)]
    [InlineData(Key.Tab, KeyModifiers.Control, KeymapCommand.CycleTerminal)]
    [InlineData(Key.F11, KeyModifiers.None, KeymapCommand.ToggleFullscreen)]
    public void ApplicationShortcutsAreClaimedByTheRouter(
        Key key,
        KeyModifiers modifiers,
        KeymapCommand expected)
    {
        var router = new TerminalInputRouter(new KeymapService());
        using var workspace = new TerminalsPageViewModel();

        Assert.Equal(expected, router.Resolve(KeyDown(key, modifiers), workspace));
    }

    [AvaloniaFact]
    public async Task CtrlCPolicyIsCachedSoRoutingCanDecideSynchronously()
    {
        var token = TestContext.Current.CancellationToken;
        var settings = new InMemorySettingsStore();
        await settings.Set(SettingKeys.CtrlCPolicy, CtrlCPolicy.CopyWhenSelected, token);
        await using var workspace = new TerminalsPageViewModel(
            new StubPtyService(),
            new UiDispatcher(),
            settings,
            new AlwaysConfirm());

        Assert.Equal(CtrlCPolicy.SigintAlways, workspace.CtrlCPolicy);

        await workspace.InitializeAsync(token);

        Assert.Equal(CtrlCPolicy.CopyWhenSelected, workspace.CtrlCPolicy);
    }

    [Theory]
    [InlineData(@"C:\Projects\Personal\RemoteFlow", "RemoteFlow")]
    [InlineData(@"Administrator: C:\Windows\system32\cmd.exe", "cmd.exe")]
    [InlineData("~/projects/remoteflow", "remoteflow")]
    [InlineData("/var/log", "log")]
    [InlineData("operator@build-01: ~", "operator@build-01: ~")]
    [InlineData("Local shell", "Local shell")]
    [InlineData("", "Terminal")]
    public void TabTitlesAreCondensedToSomethingThatFitsATab(string title, string expected)
    {
        Assert.Equal(expected, TerminalSessionViewModel.CondenseTitle(title));
    }

    private static KeyEventArgs KeyDown(Key key, KeyModifiers modifiers)
    {
        return new KeyEventArgs
        {
            Key = key,
            KeyModifiers = modifiers,
            RoutedEvent = InputElement.KeyDownEvent,
        };
    }

    private sealed class AlwaysConfirm : IConfirmationDialogService
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

    private sealed class StubPtyService : IPtyService
    {
        public Task<IPtySession> SpawnAsync(
            PtySpawnOptions options,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IPtySession>(new StubPtySession());
        }
    }

    private sealed class StubPtySession : IPtySession
    {
        private readonly Pipe _pipe = new();
        private readonly TaskCompletionSource<int?> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ProcessId => 1;

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
    }
}
