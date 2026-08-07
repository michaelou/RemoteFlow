using System.Buffers;
using RemoteFlow.Application.Abstractions.Ssh;
using RemoteFlow.TestSupport;
using Xunit;

namespace RemoteFlow.Application.Tests;

public sealed class SshTransportContractTests
{
    [Theory]
    [InlineData(SshError.DnsFailure)]
    [InlineData(SshError.ConnectionRefused)]
    [InlineData(SshError.Timeout)]
    [InlineData(SshError.AuthFailed)]
    [InlineData(SshError.HostKeyUnknown)]
    [InlineData(SshError.HostKeyMismatch)]
    [InlineData(SshError.HostKeyRevoked)]
    [InlineData(SshError.ChannelClosed)]
    [InlineData(SshError.NetworkChanged)]
    [InlineData(SshError.Cancelled)]
    public async Task FakeTransportRepresentsEveryNormalizedError(SshError error)
    {
        var transport = new FakeSshTransport();
        transport.FailNextConnect(error);

        var result = await transport.ConnectAsync(Request(), TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(error, result.Failure.Error);
    }

    [Fact]
    public async Task FakeTransportSupportsEchoResizeExecAndDisconnect()
    {
        var token = TestContext.Current.CancellationToken;
        var transport = new FakeSshTransport();
        var connected = await transport.ConnectAsync(Request(), token);
        var connection = connected.Value;
        var opened = await connection.OpenShellAsync(new TerminalSpec { Columns = 80, Rows = 24 }, token);
        var shell = (FakeSshShell)opened.Value;
        var disconnected = new TaskCompletionSource<SshDisconnectedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Disconnected += (_, eventArgs) => disconnected.TrySetResult(eventArgs);

        await shell.WriteAsync("hello"u8.ToArray(), token);
        var read = await shell.Output.ReadAsync(token);
        Assert.Equal("hello"u8.ToArray(), read.Buffer.ToArray());
        shell.Output.AdvanceTo(read.Buffer.End);
        await shell.ResizeAsync(132, 43, token);
        var exec = await connection.ExecuteAsync("whoami", token);
        await ((FakeSshConnection)connection).DisconnectAsync();

        Assert.Equal((132, 43), Assert.Single(shell.Resizes));
        Assert.Equal("whoami", exec.Value.StandardOutput);
        Assert.Equal(SshError.NetworkChanged, (await disconnected.Task).Error);
        Assert.True(shell.Exited.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData(SshError.AuthFailed)]
    [InlineData(SshError.HostKeyUnknown)]
    [InlineData(SshError.HostKeyMismatch)]
    public async Task FakeTransportInjectsRequiredConnectionFailures(SshError error)
    {
        var transport = new FakeSshTransport();
        transport.FailNextConnect(error);

        var result = await transport.ConnectAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(error, result.Failure.Error);
        Assert.Null(transport.LastConnection);
    }

    [Fact]
    public async Task CancellationIsObservedByEveryAsyncBoundary()
    {
        var transport = new FakeSshTransport();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        _ = await Assert.ThrowsAsync<OperationCanceledException>(
            () => transport.ConnectAsync(Request(), cancellation.Token));
    }

    private static SshConnectRequest Request()
    {
        return new SshConnectRequest
        {
            Host = "example.test",
            Username = "tester",
            Authentication = new SshAuthMaterial.Password("secret"),
        };
    }
}
