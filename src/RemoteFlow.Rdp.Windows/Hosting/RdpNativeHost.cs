using Avalonia.Controls;
using Avalonia.Platform;
using RemoteFlow.Rdp.Windows.Interop;

namespace RemoteFlow.Rdp.Windows.Hosting;

/// <summary>Hosts the outer container HWND and parks it instead of destroying it on visual detach.</summary>
internal sealed class RdpNativeHost : NativeControlHost
{
    private IRdpControlContainer? _container;

    public int AttachCount { get; private set; }

    public int DetachCount { get; private set; }

    internal IntPtr ContainerWindowHandle => _container?.Handle ?? IntPtr.Zero;

    public void Attach(IRdpControlContainer container)
    {
        _container = container ?? throw new ArgumentNullException(nameof(container));
    }

    public void Release()
    {
        _container = null;
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        ArgumentNullException.ThrowIfNull(parent);
        if (_container is null || _container.Handle == IntPtr.Zero)
        {
            return base.CreateNativeControlCore(parent);
        }

        AttachCount++;
        _ = Win32Hosting.SetParent(_container.Handle, parent.Handle);
        _ = Win32Hosting.ShowWindow(_container.Handle, Win32Hosting.SwShow);
        return new PlatformHandle(_container.Handle, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        ArgumentNullException.ThrowIfNull(control);
        if (_container is null || control.Handle != _container.Handle)
        {
            base.DestroyNativeControlCore(control);
            return;
        }

        DetachCount++;
        _ = Win32Hosting.ShowWindow(_container.Handle, Win32Hosting.SwHide);
        _ = Win32Hosting.SetParent(_container.Handle, OffscreenHolder.Handle);
    }

    protected override Avalonia.Size ArrangeOverride(Avalonia.Size finalSize)
    {
        var arranged = base.ArrangeOverride(finalSize);
        if (_container is not null)
        {
            var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1d;
            _container.SetSize(
                Math.Max((int)Math.Round(arranged.Width * scaling), 1),
                Math.Max((int)Math.Round(arranged.Height * scaling), 1));
        }

        return arranged;
    }
}
