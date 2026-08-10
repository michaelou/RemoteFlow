using RemoteFlow.Rdp.Windows.Interop;
using Xunit;

namespace RemoteFlow.Rdp.Windows.Tests;

public sealed class ProjectBoundaryTests
{
    [Fact]
    public void WindowsRdpAssemblyLoads()
    {
        Assert.Equal("RemoteFlow.Rdp.Windows", typeof(AssemblyMarker).Assembly.GetName().Name);
    }

    [Fact]
    public void NativeControlBoundaryIsInternal()
    {
        Assert.False(typeof(INativeRdpControl).IsPublic);
    }
}
