using Xunit;

namespace RemoteFlow.Infrastructure.Tests;

public sealed class ProjectSmokeTests
{
    [Fact]
    public void InfrastructureAssemblyLoads()
    {
        Assert.Equal("RemoteFlow.Infrastructure", typeof(AssemblyMarker).Assembly.GetName().Name);
    }
}
