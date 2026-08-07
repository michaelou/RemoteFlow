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
        await WaitUntilAsync(() => viewModel.Model.Search("echoed") == 1, token);
        await ((FakeSshConnection)connected.Value).DisconnectAsync();
        await viewModel.Completion.WaitAsync(TimeSpan.FromSeconds(5), token);

        Assert.Equal("echoed", Encoding.UTF8.GetString(Assert.Single(shell.Writes)));
        Assert.True(viewModel.IsEnded);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
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
}
