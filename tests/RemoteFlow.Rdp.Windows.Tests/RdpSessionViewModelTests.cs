using RemoteFlow.Application.Abstractions;
using RemoteFlow.Domain.Enums;
using RemoteFlow.UI.ViewModels.Terminal;
using Xunit;

namespace RemoteFlow.Rdp.Windows.Tests;

public sealed class RdpSessionViewModelTests
{
    [Fact]
    public async Task StatusRecoveryAndAccessibleNameTrackTheEmbeddedSession()
    {
        var session = new FakeEmbeddedRdpSession();
        await using var viewModel = new RdpSessionViewModel(session, "DC01", EnvironmentKind.Production);

        Assert.Equal("Created", viewModel.StatusText);
        Assert.Equal("DC01, RDP, production, Created", viewModel.TabAccessibleName);
        Assert.False(viewModel.RetryCommand.CanExecute(null));

        session.Transition(EmbeddedRdpSessionState.Connecting);
        Assert.Equal("Connecting", viewModel.StatusText);
        session.Transition(EmbeddedRdpSessionState.Connected);
        Assert.Equal("Connected", viewModel.StatusText);
        session.Transition(EmbeddedRdpSessionState.Reconnecting);
        Assert.Equal("Reconnecting", viewModel.StatusText);
        session.Transition(EmbeddedRdpSessionState.Disconnected, "The server ended the session.");

        Assert.Equal("Disconnected", viewModel.StatusText);
        Assert.Equal("Reconnect", viewModel.RecoveryActionLabel);
        Assert.Equal("The server ended the session.", viewModel.EndedMessage);
        Assert.True(viewModel.RetryCommand.CanExecute(null));
        await viewModel.RetryCommand.ExecuteAsync(null);
        Assert.Equal(1, session.ReconnectCount);
        Assert.Equal("Reconnecting", viewModel.StatusText);

        session.Transition(EmbeddedRdpSessionState.Failed, "Authentication failed.");
        Assert.Equal("Failed", viewModel.StatusText);
        Assert.Equal("Retry", viewModel.RecoveryActionLabel);
        Assert.Contains("Failed", viewModel.TabAccessibleName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FiveRdpSessionsRemainIndependentAndClosingDisposesEachOne()
    {
        var token = TestContext.Current.CancellationToken;
        await using var workspace = new TerminalWorkspaceViewModel();
        var sessions = Enumerable.Range(1, 5)
            .Select(index => new FakeEmbeddedRdpSession(EmbeddedRdpSessionState.Connected))
            .ToArray();
        var tabs = sessions.Select((session, index) =>
            new RdpSessionViewModel(session, $"RDP {index + 1}", EnvironmentKind.Staging)).ToArray();
        foreach (var tab in tabs)
        {
            workspace.AddWorkspaceSession(tab);
        }

        for (var index = 0; index < tabs.Length; index++)
        {
            workspace.SelectSession(index + 1);
            Assert.Same(tabs[index], workspace.SelectedSession);
            _ = Assert.Single(tabs, tab => tab.IsActive);
        }

        foreach (var tab in tabs.ToArray())
        {
            Assert.True(await workspace.CloseSessionAsync(tab, skipConfirmation: true, token));
        }

        Assert.Empty(workspace.Sessions);
        Assert.All(sessions, session =>
        {
            Assert.True(session.IsDisposed);
            Assert.Equal(1, session.DisconnectCount);
        });
    }

    private sealed class FakeEmbeddedRdpSession(
        EmbeddedRdpSessionState state = EmbeddedRdpSessionState.Created) : IEmbeddedRdpSession
    {
        public EmbeddedRdpSessionState State { get; private set; } = state;

        public string? StatusMessage { get; private set; }

        public int ReconnectCount { get; private set; }

        public int DisconnectCount { get; private set; }

        public bool IsDisposed { get; private set; }

        public event EventHandler<EmbeddedRdpSessionStateChangedEventArgs>? StateChanged;

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            Transition(EmbeddedRdpSessionState.Connecting);
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            DisconnectCount++;
            Transition(EmbeddedRdpSessionState.Disconnected);
            return Task.CompletedTask;
        }

        public Task ReconnectAsync(CancellationToken cancellationToken = default)
        {
            ReconnectCount++;
            Transition(EmbeddedRdpSessionState.Reconnecting);
            return Task.CompletedTask;
        }

        public void Resize(int width, int height, double scaling)
        {
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }

        public void Transition(EmbeddedRdpSessionState state, string? message = null)
        {
            var previous = State;
            State = state;
            StatusMessage = message;
            StateChanged?.Invoke(this, new EmbeddedRdpSessionStateChangedEventArgs(previous, state, message));
        }
    }
}
