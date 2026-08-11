using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Win32;
using RemoteFlow.Application.Abstractions;

namespace RemoteFlow.Infrastructure.Updates;

/// <summary>Decides whether this copy of RemoteFlow is one the installer can upgrade in place.
///
/// The answer comes from the registry rather than from the path, because the path cannot tell an install
/// from a zip extracted into the same place, and cannot find an install the user directed somewhere else.
/// The value read here is the one Inno Setup itself reads when it works out where an upgrade goes, so
/// agreeing with it is what makes "the update lands where I am" provable instead of hoped-for.</summary>
public sealed class AppInstallInfo : IAppInstallInfo
{
    /// <summary>The uninstall key Inno Setup writes. The GUID is <c>AppId</c> from
    /// <c>build/windows/RemoteFlow.iss</c> and must never change; <c>_is1</c> is Inno's own suffix, and the
    /// key is under HKCU rather than HKLM because the install is per-user.</summary>
    public const string UninstallKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{6A084A9C-3CFB-4C8F-A7A8-AA5B34D9C91F}_is1";

    /// <summary>Reads the running process's own directory and the real registry.</summary>
    public AppInstallInfo(ISystemPlatform platform)
        : this(platform, AppContext.BaseDirectory, ReadInstalledPath)
    {
    }

    /// <summary>Takes the directory and the registry answer, so tests can cover every shape without a hive
    /// to write to and without depending on how this machine happens to be installed.</summary>
    public AppInstallInfo(ISystemPlatform platform, string baseDirectory, Func<string?> readInstalledPath)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(baseDirectory);
        ArgumentNullException.ThrowIfNull(readInstalledPath);

        InstallDirectory = Normalize(baseDirectory);
        (Shape, Explanation) = Resolve(platform, InstallDirectory, readInstalledPath);
    }

    public InstallShape Shape { get; }

    public string InstallDirectory { get; }

    public string? Explanation { get; }

    private static (InstallShape Shape, string? Explanation) Resolve(
        ISystemPlatform platform,
        string directory,
        Func<string?> readInstalledPath)
    {
        if (platform.OperatingSystem != OperatingSystemFamily.Windows)
        {
            return (InstallShape.Development,
                "RemoteFlow publishes prebuilt artefacts for Windows only. On this platform it is built " +
                "from source, so there is nothing for it to replace itself with.");
        }

        if (LooksLikeBuildOutput(directory))
        {
            return (InstallShape.Development,
                "This is a build output directory rather than an install, so there is nothing here for an " +
                "installer to upgrade.");
        }

        var installed = readInstalledPath();
        if (string.IsNullOrWhiteSpace(installed))
        {
            return (InstallShape.Portable,
                "This is a portable copy of RemoteFlow. Nothing on this machine records where it lives, so " +
                "it will not be replaced behind your back — download the new zip and extract it over this " +
                "folder.");
        }

        if (!string.Equals(Normalize(installed), directory, StringComparison.OrdinalIgnoreCase))
        {
            return (InstallShape.Portable, string.Format(
                CultureInfo.CurrentCulture,
                "This copy is running from {0}, but the installed RemoteFlow on this machine is at {1}. " +
                "Updating from here would replace that one instead.",
                directory,
                Normalize(installed)));
        }

        return (InstallShape.Installer, null);
    }

    /// <summary>A build output directory is an install nobody performed, and running an installer over one
    /// would leave the developer with two copies and a confusing uninstall entry.</summary>
    private static bool LooksLikeBuildOutput(string directory)
    {
        return directory.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}Debug", StringComparison.OrdinalIgnoreCase) ||
            directory.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}Release", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Inno writes "Inno Setup: App Path" without a trailing separator and "InstallLocation" with
    /// one, and <see cref="AppContext.BaseDirectory"/> always has one, so both sides are trimmed before
    /// they are compared.</summary>
    private static string Normalize(string path)
    {
        var trimmed = path.Trim();
        return trimmed.Length == 0
            ? trimmed
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
    }

    private static string? ReadInstalledPath()
    {
        return OperatingSystem.IsWindows() ? Native.ReadInstalledPath() : null;
    }

    [SupportedOSPlatform("windows")]
    private static class Native
    {
        internal static string? ReadInstalledPath()
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath);
            if (key is null)
            {
                return null;
            }

            // App Path is the value Inno reads back when it decides where an upgrade goes; InstallLocation
            // is the one Add/Remove Programs shows. They agree, but the first is the one that matters.
            return key.GetValue("Inno Setup: App Path") as string
                ?? key.GetValue("InstallLocation") as string;
        }
    }
}
