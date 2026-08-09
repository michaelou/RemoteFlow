using System.Globalization;
using System.Runtime.InteropServices;
using RemoteFlow.RdpSpike.Interop;

namespace RemoteFlow.RdpSpike.Diagnostics;

internal sealed record KeyMessage(string Message, IntPtr Target, uint VirtualKey, bool InsideControl);

/// <summary>Watches keyboard messages on the UI thread before they are dispatched.
///
/// Question 5 asks which RemoteFlow keybindings stop working while the RDP control has focus. Guessing is
/// no use: what settles it is whether a keystroke is delivered to a window inside the control, in which
/// case Avalonia never sees it and no Avalonia-level key handler can fire. A thread-local WH_GETMESSAGE
/// hook sees exactly that, and injects nothing into any other process.</summary>
internal sealed class KeyboardProbe : IDisposable
{
    private readonly Win32.HookProc _callback;
    private readonly List<KeyMessage> _recent = [];
    private readonly Lock _gate = new();

    private IntPtr _hook;
    private IntPtr _controlWindow;

    public KeyboardProbe()
    {
        _callback = OnMessage;
        _hook = Win32.SetWindowsHookEx(Win32.WH_GETMESSAGE, _callback, IntPtr.Zero, Win32.GetCurrentThreadId());
        if (_hook == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"SetWindowsHookEx(WH_GETMESSAGE) failed: {Marshal.GetLastPInvokeErrorMessage()}");
        }
    }

    /// <summary>Keys seen by a window inside the control. Avalonia cannot observe these.</summary>
    public int InsideControlCount { get; private set; }

    /// <summary>Keys seen by any other window on the thread, which is where Avalonia's own input lives.</summary>
    public int ElsewhereCount { get; private set; }

    /// <summary>The window the probe treats as the control root; everything below it counts as inside.</summary>
    public void Watch(IntPtr controlWindow)
    {
        _controlWindow = controlWindow;
    }

    public IReadOnlyList<KeyMessage> Recent()
    {
        lock (_gate)
        {
            return [.. _recent];
        }
    }

    public string Summary =>
        $"keys inside control={InsideControlCount.ToString(CultureInfo.InvariantCulture)}, " +
        $"elsewhere={ElsewhereCount.ToString(CultureInfo.InvariantCulture)}";

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            _ = Win32.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private IntPtr OnMessage(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && lParam != IntPtr.Zero)
        {
            var message = Marshal.PtrToStructure<Msg>(lParam);
            if (message.Message is Win32.WM_KEYDOWN or Win32.WM_SYSKEYDOWN or Win32.WM_CHAR or Win32.WM_SYSCHAR)
            {
                Record(message);
            }
        }

        return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void Record(Msg message)
    {
        var inside = _controlWindow != IntPtr.Zero &&
            (message.Hwnd == _controlWindow || Win32.IsChild(_controlWindow, message.Hwnd));
        if (inside)
        {
            InsideControlCount++;
        }
        else
        {
            ElsewhereCount++;
        }

        var name = message.Message switch
        {
            Win32.WM_KEYDOWN => "WM_KEYDOWN",
            Win32.WM_SYSKEYDOWN => "WM_SYSKEYDOWN",
            Win32.WM_CHAR => "WM_CHAR",
            _ => "WM_SYSCHAR",
        };

        lock (_gate)
        {
            _recent.Add(new KeyMessage(name, message.Hwnd, (uint)message.WParam, inside));
            if (_recent.Count > 40)
            {
                _recent.RemoveAt(0);
            }
        }
    }
}
