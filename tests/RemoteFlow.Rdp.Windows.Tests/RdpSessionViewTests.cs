using RemoteFlow.Application.Abstractions;
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

    [Fact]
    public void HiddenViewportKeepsLatestResizeAndAppliesItExactlyOnceWhenShown()
    {
        var session = new RecordingSession();
        var controller = new RdpViewportResizeController(session);

        controller.RequestResize(900, 600, 1d);
        controller.RequestResize(1200, 800, 1.5d);
        Assert.Empty(session.Resizes);

        controller.SetVisible(true);
        controller.SetVisible(true);

        Assert.Equal([(1200, 800, 1.5d)], session.Resizes);
    }

    [Fact]
    public void VisibleViewportForwardsMaximizeAndRestoreSizes()
    {
        var session = new RecordingSession();
        var controller = new RdpViewportResizeController(session);
        controller.SetVisible(true);

        controller.RequestResize(1920, 1080, 1d);
        controller.RequestResize(1280, 720, 1d);

        Assert.Equal([(1920, 1080, 1d), (1280, 720, 1d)], session.Resizes);
    }

    private sealed class RecordingSession : IEmbeddedRdpSession
    {
        public EmbeddedRdpSessionState State => EmbeddedRdpSessionState.Connected;

        public string? StatusMessage => null;

        public List<(int Width, int Height, double Scaling)> Resizes { get; } = [];

        public event EventHandler<EmbeddedRdpSessionStateChangedEventArgs>? StateChanged
        {
            add { }
            remove { }
        }

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task ReconnectAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public void Resize(int width, int height, double scaling)
        {
            Resizes.Add((width, height, scaling));
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
