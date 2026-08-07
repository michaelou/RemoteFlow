using RemoteFlow.TestSupport;
using Xunit;

namespace RemoteFlow.Ssh.IntegrationTests;

public sealed class SshServerFixture : IAsyncLifetime
{
    public SshTestServer Server { get; } = new();

    public ValueTask InitializeAsync()
    {
        return new ValueTask(Server.StartAsync(TestContext.Current.CancellationToken));
    }

    public ValueTask DisposeAsync()
    {
        return Server.DisposeAsync();
    }
}

[CollectionDefinition]
public sealed class SshServerTestGroup : ICollectionFixture<SshServerFixture>;
