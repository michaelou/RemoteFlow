using System.Runtime.InteropServices;
using RemoteFlow.RdpSpike.Interop;

namespace RemoteFlow.RdpSpike.Hosting;

/// <summary>A hidden top-level window that owns a session's HWND while no Avalonia view is showing it.
///
/// This is the answer to Avalonia's documented behaviour that NativeControlHost destroys and recreates its
/// native control on detach: the host reparents into this window instead of destroying, so the ActiveX
/// control keeps its window, its connection, and its decoder state across a tab switch.
///
/// It is deliberately a normal popup window rather than a message-only window. A message-only parent has
/// no desktop lineage, and an RDP control whose window tree leaves the desktop is a bigger gamble than a
/// window that is merely never shown.</summary>
internal static class OffscreenHolder
{
    private const string _windowClassName = "RemoteFlowRdpSpikeHolder";

    private static readonly Lazy<IntPtr> _window = new(Create);

    private static Win32.WndProc? _windowProcedure;

    /// <summary>The holder window, created on first use and kept for the life of the process.</summary>
    public static IntPtr Handle => _window.Value;

    private static IntPtr Create()
    {
        // Held in a static so the delegate outlives the window class registration.
        _windowProcedure = Win32.DefWindowProc;
        var windowClass = new Win32.WndClassEx
        {
            CbSize = (uint)Marshal.SizeOf<Win32.WndClassEx>(),
            LpfnWndProc = _windowProcedure,
            HInstance = Win32.GetModuleHandle(null),
            LpszClassName = _windowClassName,
        };

        var registered = Win32.RegisterClassEx(windowClass);
        return registered == 0
            ? throw new InvalidOperationException(
                $"RegisterClassEx failed: {Marshal.GetLastPInvokeErrorMessage()}")
            : Win32.CreateWindowEx(
            0,
            _windowClassName,
            "RemoteFlow RDP spike offscreen holder",
            Win32.WS_POPUP,
            0,
            0,
            1,
            1,
            IntPtr.Zero,
            IntPtr.Zero,
            Win32.GetModuleHandle(null),
            IntPtr.Zero);
    }
}
