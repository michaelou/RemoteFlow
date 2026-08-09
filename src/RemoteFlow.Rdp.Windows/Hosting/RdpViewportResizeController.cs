using RemoteFlow.Application.Abstractions;

namespace RemoteFlow.Rdp.Windows.Hosting;

internal sealed class RdpViewportResizeController(Action<int, int, double> resize)
{
    private readonly Action<int, int, double> _resize = resize ?? throw new ArgumentNullException(nameof(resize));
    private PendingResize? _pending;
    private bool _isVisible;

    public RdpViewportResizeController(IEmbeddedRdpSession session)
        : this((session ?? throw new ArgumentNullException(nameof(session))).Resize)
    {
    }

    public void RequestResize(int width, int height, double scaling)
    {
        if (width <= 0 || height <= 0 || scaling <= 0)
        {
            return;
        }

        var resize = new PendingResize(width, height, scaling);
        if (!_isVisible)
        {
            _pending = resize;
            return;
        }

        _pending = null;
        _resize(resize.Width, resize.Height, resize.Scaling);
    }

    public void SetVisible(bool isVisible)
    {
        if (_isVisible == isVisible)
        {
            return;
        }

        _isVisible = isVisible;
        if (!_isVisible || _pending is not { } pending)
        {
            return;
        }

        _pending = null;
        _resize(pending.Width, pending.Height, pending.Scaling);
    }

    private readonly record struct PendingResize(int Width, int Height, double Scaling);
}
