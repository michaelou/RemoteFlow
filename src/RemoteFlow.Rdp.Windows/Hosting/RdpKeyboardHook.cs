using System.Runtime.InteropServices;
using RemoteFlow.Rdp.Windows.Interop;

namespace RemoteFlow.Rdp.Windows.Hosting;

/// <summary>Intercepts the one documented focus-escape key before a child HWND bypasses Avalonia.</summary>
internal sealed class RdpKeyboardHook : IDisposable
{
    private readonly Win32Hosting.HookProcedure _callback;
    private readonly IntPtr _controlRoot;
    private readonly Action _leaveSurface;
    private IntPtr _hook;
    private int _disposed;

    public RdpKeyboardHook(IntPtr controlRoot, Action leaveSurface)
    {
        if (controlRoot == IntPtr.Zero)
        {
            throw new ArgumentException("The RDP control root must be a live window.", nameof(controlRoot));
        }

        _controlRoot = controlRoot;
        _leaveSurface = leaveSurface ?? throw new ArgumentNullException(nameof(leaveSurface));
        _callback = OnMessage;
        _hook = Win32Hosting.SetWindowsHookEx(
            Win32Hosting.WhGetMessage,
            _callback,
            IntPtr.Zero,
            Win32Hosting.GetCurrentThreadId());
        if (_hook == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"The RDP focus-escape hook could not be installed: {Marshal.GetLastPInvokeErrorMessage()}");
        }
    }

    internal static bool ShouldConsume(
        uint message,
        int virtualKey,
        bool shiftDown,
        bool targetIsInsideControl)
    {
        return targetIsInsideControl &&
            !shiftDown &&
            virtualKey == Win32Hosting.VkF6 &&
            message is Win32Hosting.WmKeyDown or Win32Hosting.WmKeyUp;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_hook != IntPtr.Zero)
        {
            _ = Win32Hosting.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private IntPtr OnMessage(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code >= 0 && lParam != IntPtr.Zero)
            {
                var message = Marshal.PtrToStructure<NativeMessage>(lParam);
                var inside = message.Window == _controlRoot ||
                    Win32Hosting.IsChild(_controlRoot, message.Window);
                var shiftDown = Win32Hosting.GetKeyState(Win32Hosting.VkShift) < 0;
                if (ShouldConsume(message.Message, message.WParam.ToInt32(), shiftDown, inside))
                {
                    var isKeyDown = message.Message == Win32Hosting.WmKeyDown;
                    message.Message = Win32Hosting.WmNull;
                    message.WParam = IntPtr.Zero;
                    message.LParam = IntPtr.Zero;
                    Marshal.StructureToPtr(message, lParam, fDeleteOld: false);
                    if (isKeyDown)
                    {
                        _leaveSurface();
                    }
                }
            }
        }
        catch (Exception)
        {
            // A thread hook must never unwind into the Windows message pump.
        }

        return Win32Hosting.CallNextHookEx(_hook, code, wParam, lParam);
    }
}
