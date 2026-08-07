using Xunit;

namespace RemoteFlow.UI.Tests;

public sealed class ProjectSmokeTests
{
    [Fact]
    public void UiAssemblyLoads()
    {
        Assert.Equal("RemoteFlow.UI", typeof(AssemblyMarker).Assembly.GetName().Name);
    }
}
