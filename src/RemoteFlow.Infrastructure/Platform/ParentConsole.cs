using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RemoteFlow.Infrastructure.Platform;

/// <summary>
/// Borrows the console of the process that launched this one, so a GUI executable can answer a
/// command-line question.
/// </summary>
/// <remarks>
/// RemoteFlow.Desktop is a <c>WinExe</c>: Windows gives it no console, and anything written to standard
/// output from a command prompt goes nowhere. Attaching to the parent's console makes <c>--version</c>
/// visible when it is run the way a person would actually run it. Redirected output (a pipe, a file,
/// <c>dotnet run</c>) already works and is left alone.
/// </remarks>
public static class ParentConsole
{
    /// <summary>Attaches the current process to its parent's console.</summary>
    /// <returns><see langword="true"/> when a console was attached; <see langword="false"/> off Windows,
    /// when one is already present, when output is redirected, or when the parent has none (launched from
    /// Explorer).</returns>
    public static bool TryAttach()
    {
        return OperatingSystem.IsWindows() &&
            !Console.IsOutputRedirected &&
            Native.AttachConsole(Native.AttachParentProcess);
    }

    [SupportedOSPlatform("windows")]
    private static class Native
    {
        /// <summary>Attach to the console of the parent process.</summary>
        internal const uint AttachParentProcess = 0xFFFFFFFF;

        // LibraryImport would need AllowUnsafeBlocks, which this repository does not enable; the taskbar
        // identity interop next door suppresses the same analyzer for the same reason.
#pragma warning disable SYSLIB1054
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AttachConsole(uint processId);
#pragma warning restore SYSLIB1054
    }
}
