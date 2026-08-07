using Xunit;

namespace RemoteFlow.Ssh.IntegrationTests;

public sealed class ProjectSmokeTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public void InfrastructureAssemblyLoads()
    {
        Assert.Equal("RemoteFlow.Infrastructure", typeof(Infrastructure.AssemblyMarker).Assembly.GetName().Name);
    }
}
