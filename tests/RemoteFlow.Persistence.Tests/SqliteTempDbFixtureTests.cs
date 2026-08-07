using RemoteFlow.TestSupport;
using Xunit;

namespace RemoteFlow.Persistence.Tests;

public sealed class SqliteTempDbFixtureTests
{
    [Fact]
    public async Task DisposeDeletesDatabaseAndTemporaryDirectory()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await SqliteTempDbFixture.CreateAsync(cancellationToken);
        var databasePath = fixture.DatabasePath;
        var directory = fixture.DataDirectory;
        Assert.True(File.Exists(databasePath));

        await fixture.DisposeAsync();

        Assert.False(File.Exists(databasePath));
        Assert.False(Directory.Exists(directory));
    }
}
