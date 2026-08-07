using Xunit;

namespace RemoteFlow.Persistence.Tests;

public sealed class ProjectSmokeTests
{
    [Fact]
    public void PersistenceAssemblyLoads()
    {
        Assert.Equal("RemoteFlow.Persistence", typeof(AssemblyMarker).Assembly.GetName().Name);
    }
}
