using RemoteFlow.Application.Abstractions;

namespace RemoteFlow.Infrastructure.Platform;

public enum AppPlatform
{
    Windows,
    MacOS,
    Linux,
}

public sealed class AppPaths : IAppPaths
{
    private const string _productName = "RemoteFlow";
    private const string _linuxProductName = "remoteflow";

    public AppPaths()
        : this(CurrentPlatform(), Environment.GetFolderPath, Environment.GetEnvironmentVariable)
    {
    }

    public AppPaths(
        AppPlatform platform,
        Func<Environment.SpecialFolder, string> getFolderPath,
        Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getFolderPath);
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        (ConfigDirectory, DataDirectory, CacheDirectory, LogDirectory) = platform switch
        {
            AppPlatform.Windows => ResolveWindows(getFolderPath),
            AppPlatform.MacOS => ResolveMacOS(getFolderPath),
            AppPlatform.Linux => ResolveLinux(getFolderPath, getEnvironmentVariable),
            _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, "Unsupported platform."),
        };
    }

    public string ConfigDirectory { get; }

    public string DataDirectory { get; }

    public string CacheDirectory { get; }

    public string LogDirectory { get; }

    public void EnsureDirectories()
    {
        foreach (var path in new[] { ConfigDirectory, DataDirectory, CacheDirectory, LogDirectory }
                     .Distinct(StringComparer.Ordinal))
        {
            _ = Directory.CreateDirectory(path);
        }
    }

    private static AppPlatform CurrentPlatform()
    {
        return OperatingSystem.IsWindows()
            ? AppPlatform.Windows
            : OperatingSystem.IsMacOS()
                ? AppPlatform.MacOS
                : OperatingSystem.IsLinux()
                    ? AppPlatform.Linux
                    : throw new PlatformNotSupportedException("RemoteFlow supports Windows, macOS, and Linux.");
    }

    private static (string Config, string Data, string Cache, string Logs) ResolveWindows(
        Func<Environment.SpecialFolder, string> getFolderPath)
    {
        var roaming = RequiredFolder(getFolderPath, Environment.SpecialFolder.ApplicationData);
        var local = RequiredFolder(getFolderPath, Environment.SpecialFolder.LocalApplicationData);
        var shared = Path.Combine(roaming, _productName);
        return (shared, shared, Path.Combine(local, _productName, "Cache"), Path.Combine(local, _productName, "Logs"));
    }

    private static (string Config, string Data, string Cache, string Logs) ResolveMacOS(
        Func<Environment.SpecialFolder, string> getFolderPath)
    {
        var home = RequiredFolder(getFolderPath, Environment.SpecialFolder.UserProfile);
        var shared = Path.Combine(home, "Library", "Application Support", _productName);
        return (
            shared,
            shared,
            Path.Combine(home, "Library", "Caches", _productName),
            Path.Combine(home, "Library", "Logs", _productName));
    }

    private static (string Config, string Data, string Cache, string Logs) ResolveLinux(
        Func<Environment.SpecialFolder, string> getFolderPath,
        Func<string, string?> getEnvironmentVariable)
    {
        var home = RequiredFolder(getFolderPath, Environment.SpecialFolder.UserProfile);
        var config = XdgPath(getEnvironmentVariable, "XDG_CONFIG_HOME", Path.Combine(home, ".config"));
        var data = XdgPath(getEnvironmentVariable, "XDG_DATA_HOME", Path.Combine(home, ".local", "share"));
        var cache = XdgPath(getEnvironmentVariable, "XDG_CACHE_HOME", Path.Combine(home, ".cache"));
        var state = XdgPath(getEnvironmentVariable, "XDG_STATE_HOME", Path.Combine(home, ".local", "state"));
        return (
            Path.Combine(config, _linuxProductName),
            Path.Combine(data, _linuxProductName),
            Path.Combine(cache, _linuxProductName),
            Path.Combine(state, _linuxProductName, "logs"));
    }

    private static string RequiredFolder(
        Func<Environment.SpecialFolder, string> getFolderPath,
        Environment.SpecialFolder folder)
    {
        var path = getFolderPath(folder);
        return string.IsNullOrWhiteSpace(path)
            ? throw new InvalidOperationException($"The operating system did not provide the {folder} directory.")
            : Path.GetFullPath(path);
    }

    private static string XdgPath(
        Func<string, string?> getEnvironmentVariable,
        string name,
        string fallback)
    {
        var value = getEnvironmentVariable(name);
        return Path.GetFullPath(string.IsNullOrWhiteSpace(value) ? fallback : value);
    }
}
