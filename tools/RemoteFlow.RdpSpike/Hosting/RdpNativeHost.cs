using Avalonia.Controls;
using Avalonia.Platform;
using RemoteFlow.RdpSpike.Interop;

namespace RemoteFlow.RdpSpike.Hosting;

/// <summary>Puts a container's HWND inside the Avalonia visual tree.
///
/// Two Avalonia constraints shape this class and neither is worked around:
/// native content always draws above every Avalonia visual, so nothing can float over the session; and
/// the base class destroys the native control whenever the host detaches from the visual tree. The second
/// one is what a TabControl does on every tab switch, so <see cref="DestroyNativeControlCore"/> is
/// overridden to reparent to <see cref="OffscreenHolder"/> instead of destroying anything.</summary>
internal sealed class RdpNativeHost : NativeControlHost
{
    private IActiveXContainer? _container;

    /// <summary>Raised after each attach and detach, so the window can show the count rather than the
    /// spike claiming a survival it never counted.</summary>
    public event EventHandler<string>? LifecycleChanged;

    public int AttachCount { get; private set; }

    public int DetachCount { get; private set; }

    /// <summary>Hands this host the container to show. The container owns the control; the host only ever
    /// moves its window. Call before the host enters the visual tree: the base class asks for the native
    /// handle once on attach and does not ask again.</summary>
    public void Attach(IActiveXContainer container)
    {
        _container = container;
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
        _ = Win32.SetParent(_container.Handle, parent.Handle);
        _ = Win32.ShowWindow(_container.Handle, Win32.SW_SHOW);
        LifecycleChanged?.Invoke(this, $"attach #{AttachCount}: reparented to 0x{parent.Handle:X}");
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
        _ = Win32.ShowWindow(_container.Handle, Win32.SW_HIDE);
        _ = Win32.SetParent(_container.Handle, OffscreenHolder.Handle);
        LifecycleChanged?.Invoke(this, $"detach #{DetachCount}: parked on the offscreen holder, not destroyed");
    }

    protected override Avalonia.Size ArrangeOverride(Avalonia.Size finalSize)
    {
        var arranged = base.ArrangeOverride(finalSize);
        if (_container is not null)
        {
            var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
            _container.SetSize(
                (int)Math.Round(arranged.Width * scaling),
                (int)Math.Round(arranged.Height * scaling));
        }

        return arranged;
    }
}
