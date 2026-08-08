using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RemoteFlow.Infrastructure.Platform;

/// <summary>
/// Claims an explicit taskbar identity on Windows.
/// </summary>
/// <remarks>
/// Without one, a process inherits the identity of whatever started it — the .NET host when the app is
/// launched through <c>dotnet run</c> or a debugger — and the shell draws that host's icon on the taskbar
/// button even though the window itself carries the right icon.
/// </remarks>
public static class WindowsShellIdentity
{
    /// <summary>The identity RemoteFlow's taskbar button, jump list, and pinned shortcut group under.</summary>
    public const string ApplicationId = "RemoteFlow.Desktop";

    /// <summary>Applies <paramref name="appUserModelId"/> to the current process.</summary>
    /// <returns><see langword="true"/> when the identity was set; <see langword="false"/> off Windows.</returns>
    public static bool Apply(string appUserModelId = ApplicationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appUserModelId);
        return OperatingSystem.IsWindows() &&
            Native.SetCurrentProcessExplicitAppUserModelID(appUserModelId) == 0;
    }

    [SupportedOSPlatform("windows")]
    private static class Native
    {
#pragma warning disable SYSLIB1054
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        internal static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
#pragma warning restore SYSLIB1054
    }
}
