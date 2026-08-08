using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Ssh;
using RemoteFlow.Infrastructure.Ssh;
using RemoteFlow.TestSupport;
using Xunit;

namespace RemoteFlow.Infrastructure.Tests;

public sealed class ConfiguredSshTransportTests
{
    [Fact]
    public async Task SettingIsReadForEachNewSession()
    {
        var token = TestContext.Current.CancellationToken;
        var settings = new InMemorySettingsStore();
        var tmds = new FakeSshTransport();
        var sshNet = new FakeSshTransport();
        var transport = new ConfiguredSshTransport(settings, tmds, sshNet);
        var request = new SshConnectRequest
        {
            Host = "example.test",
            Username = "operator",
        };

        var first = await transport.ConnectAsync(request, token);
        await settings.Set(SettingKeys.SshTransport, SshTransport.SshNet, token);
        var second = await transport.ConnectAsync(request, token);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        _ = Assert.Single(tmds.ConnectRequests);
        _ = Assert.Single(sshNet.ConnectRequests);
        await first.Value.DisposeAsync();
        await second.Value.DisposeAsync();
    }
}
