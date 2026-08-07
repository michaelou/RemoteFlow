using System.Text;
using Avalonia.Headless.XUnit;
using RemoteFlow.Application.Abstractions.Ssh;
using RemoteFlow.TestSupport;
using RemoteFlow.UI.Services;
using RemoteFlow.UI.ViewModels.Terminal;
using Xunit;

namespace RemoteFlow.UI.Tests;

public sealed class SshTerminalCycleTests
{
    [AvaloniaFact]
    public async Task ViewModelDrivesConnectTypeAndDisconnectThroughFakeWithoutNetwork()
    {
        var token = TestContext.Current.CancellationToken;
        var transport = new FakeSshTransport();
        var connected = await transport.ConnectAsync(new SshConnectRequest
        {
            Host = "unit.test",
            Username = "alice",
        }, token);
        var shellResult = await connected.Value.OpenShellAsync(new TerminalSpec(), token);
        var shell = (FakeSshShell)shellResult.Value;
        await using var viewModel = new TerminalSessionViewModel(shell, new ImmediateDispatcher());

        await viewModel.SendInputAsync(Encoding.UTF8.GetBytes("echoed"), token);
        await ((FakeSshConnection)connected.Value).DisconnectAsync();
        await viewModel.Completion.WaitAsync(TimeSpan.FromSeconds(5), token);

        Assert.Equal("echoed", Encoding.UTF8.GetString(Assert.Single(shell.Writes)));
        Assert.Equal(1, viewModel.Model.Search("echoed"));
        Assert.True(viewModel.IsEnded);
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
}
