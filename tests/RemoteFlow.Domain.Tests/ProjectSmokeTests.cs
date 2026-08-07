using Xunit;

namespace RemoteFlow.Domain.Tests;

public sealed class ProjectSmokeTests
{
    [Fact]
    public void DomainAssemblyLoads()
    {
        Assert.Equal("RemoteFlow.Domain", typeof(AssemblyMarker).Assembly.GetName().Name);
    }
}
