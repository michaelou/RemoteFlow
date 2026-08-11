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

    /// <summary>
    /// The control refuses a desktop outside the range it supports, and a refused resize latches SmartSizing
    /// on the session for the rest of its life — there is no path that clears it. A tile in a dense grid is
    /// narrower than the floor and an ultrawide monitor is past the ceiling, so both ends are clamped: a
    /// cropped desktop is recoverable, and losing DPI-aware resize for the whole session is not.
    /// </summary>
    [Theory]
    [InlineData(0d, 0)]
    [InlineData(-4d, 0)]
    [InlineData(120.4d, 200)]
    [InlineData(199.6d, 200)]
    [InlineData(640d, 640)]
    [InlineData(4096d, 4096)]
    [InlineData(5120d, 4096)]
    public void AViewportOutsideTheControlsRangeIsClampedRatherThanRefused(double physicalPixels, int expected)
    {
        Assert.Equal(expected, RdpSessionView.ClampViewport(physicalPixels));
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

    [Fact]
    public void HiddenTabAppliesLatestMonitorDpiExactlyOnceWhenShown()
    {
        var session = new RecordingSession();
        var controller = new RdpViewportResizeController(session);
        controller.SetVisible(true);
        controller.RequestResize(1000, 700, 1d);
        controller.SetVisible(false);

        controller.RequestResize(1600, 1000, 1.5d);
        controller.RequestResize(2000, 1400, 2d);
        controller.SetVisible(true);
        controller.SetVisible(true);

        Assert.Equal([(1000, 700, 1d), (2000, 1400, 2d)], session.Resizes);
    }

    [Theory]
    [InlineData(0x0100u, 0x75, false, true, true)]
    [InlineData(0x0101u, 0x75, false, true, true)]
    [InlineData(0x0100u, 0x75, true, true, false)]
    [InlineData(0x0100u, 0x75, false, false, false)]
    [InlineData(0x0100u, 0x09, false, true, false)]
    public void OnlyUnmodifiedF6InsideRdpIsReservedForFocusEscape(
        uint message,
        int virtualKey,
        bool shiftDown,
        bool insideControl,
        bool expected)
    {
        Assert.Equal(
            expected,
            RdpKeyboardHook.ShouldConsume(message, virtualKey, shiftDown, insideControl));
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
