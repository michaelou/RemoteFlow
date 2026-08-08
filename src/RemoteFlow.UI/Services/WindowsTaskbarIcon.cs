using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;

namespace RemoteFlow.UI.Services;

/// <summary>
/// Puts the executable's own icon on a window at the sizes the Windows shell asks for.
/// </summary>
/// <remarks>
/// <see cref="Window.Icon"/> already carries remoteflow.ico and Avalonia does hand it to Windows, but the
/// icon it builds for the large slot measures 24x24 while the taskbar asks for <c>SM_CXICON</c> — 32x32 at
/// 100% scaling — and draws a generic placeholder rather than accepting a different size. The title bar,
/// which uses the small slot, has always been right; only the taskbar button was wrong. Extracting the
/// icon straight out of RemoteFlow.exe yields exactly the sizes the shell asks for. The XAML window icon
/// stays: it is what every other platform uses, and what the window falls back to here.
/// </remarks>
public static class WindowsTaskbarIcon
{
    private const uint _wmSetIcon = 0x0080;
    private const int _iconBig = 1;
    private const int _iconSmall = 0;

    /// <summary>Applies the executable's icon to <paramref name="window"/>.</summary>
    /// <returns><see langword="true"/> when both icon slots were set; <see langword="false"/> off Windows,
    /// before the window has a native handle, or when the executable carries no icon.</returns>
    public static bool Apply(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        var executablePath = Environment.ProcessPath;
        return handle != IntPtr.Zero &&
            !string.IsNullOrEmpty(executablePath) &&
            ApplyCore(handle, executablePath);
    }

    [SupportedOSPlatform("windows")]
    private static bool ApplyCore(IntPtr window, string executablePath)
    {
        // The handles stay alive for as long as the window does, so they are never destroyed: the process
        // is exiting by the time they stop being needed, and Windows reclaims them then.
        if (Native.ExtractIconEx(executablePath, 0, out var large, out var small, 1) <= 0 ||
            large == IntPtr.Zero ||
            small == IntPtr.Zero)
        {
            return false;
        }

        // Posted rather than sent: Avalonia posts its own WM_SETICON, and a synchronous send would run
        // ahead of a message already sitting in the queue and be overwritten by it. Queueing behind it
        // is what makes this correction stick.
        return Native.PostMessage(window, _wmSetIcon, _iconBig, large) &&
            Native.PostMessage(window, _wmSetIcon, _iconSmall, small);
    }

    [SupportedOSPlatform("windows")]
    private static class Native
    {
        // Source-generated interop needs unsafe code, which this repository does not enable.
#pragma warning disable SYSLIB1054
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        internal static extern int ExtractIconEx(
            string file,
            int iconIndex,
            out IntPtr largeIcon,
            out IntPtr smallIcon,
            int iconCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
#pragma warning restore SYSLIB1054
    }
}
