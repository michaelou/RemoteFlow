using System.Runtime.InteropServices;
using RemoteFlow.Rdp.Windows.Interop;

namespace RemoteFlow.Rdp.Windows.Hosting;

/// <summary>A hidden desktop-lineage popup that owns a session container while its view is detached.</summary>
internal static class OffscreenHolder
{
    private const string _windowClassName = "RemoteFlowRdpOffscreenHolder";
    private static readonly Lazy<IntPtr> _window = new(Create);
    private static Win32Hosting.WindowProcedure? _windowProcedure;

    public static IntPtr Handle => _window.Value;

    private static IntPtr Create()
    {
        _windowProcedure = Win32Hosting.DefWindowProc;
        var windowClass = new Win32Hosting.WindowClassEx
        {
            Size = (uint)Marshal.SizeOf<Win32Hosting.WindowClassEx>(),
            Procedure = _windowProcedure,
            Instance = Win32Hosting.GetModuleHandle(null),
            ClassName = _windowClassName,
        };
        var registered = Win32Hosting.RegisterClassEx(windowClass);
        if (registered == 0)
        {
            throw new InvalidOperationException($"RegisterClassEx failed: {Marshal.GetLastPInvokeErrorMessage()}");
        }

        var handle = Win32Hosting.CreateWindowEx(
            0,
            _windowClassName,
            "RemoteFlow RDP offscreen holder",
            Win32Hosting.WsPopup,
            0,
            0,
            1,
            1,
            IntPtr.Zero,
            IntPtr.Zero,
            Win32Hosting.GetModuleHandle(null),
            IntPtr.Zero);
        return handle == IntPtr.Zero
            ? throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastPInvokeErrorMessage()}")
            : handle;
    }
}
