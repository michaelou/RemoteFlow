using Xunit;

namespace RemoteFlow.Application.Tests;

public sealed class ProjectSmokeTests
{
    [Fact]
    public void ApplicationAssemblyLoads()
    {
        Assert.Equal("RemoteFlow.Application", typeof(AssemblyMarker).Assembly.GetName().Name);
    }
}
