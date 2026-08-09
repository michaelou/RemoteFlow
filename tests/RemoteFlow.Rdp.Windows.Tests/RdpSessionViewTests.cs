using RemoteFlow.Rdp.Windows.Hosting;
using Xunit;

namespace RemoteFlow.Rdp.Windows.Tests;

public sealed class RdpSessionViewTests
{
    [Fact]
    public async Task DesignTimeConstructionAndDisposalDoNotActivateCom()
    {
        var view = new RdpSessionView();

        Assert.Null(view.Session);
        Assert.Equal(IntPtr.Zero, view.ContainerWindowHandle);

        await view.DisposeAsync();
        await view.DisposeAsync();
    }
}
