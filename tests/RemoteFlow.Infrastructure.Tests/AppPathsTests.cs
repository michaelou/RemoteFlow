using RemoteFlow.Infrastructure.Platform;
using RemoteFlow.TestSupport;
using Xunit;

namespace RemoteFlow.Infrastructure.Tests;

public sealed class AppPathsTests
{
    private const string _home = "/home/tester";

    [Fact]
    public void SecureRandomAndItsFakeReturnTheRequestedByteCount()
    {
        var actual = new SecureRandom().GetBytes(32);
        var deterministic = new FakeSecureRandom(1, 2, 3, 4).GetBytes(3);

        Assert.Equal(32, actual.Length);
        Assert.Equal([1, 2, 3], deterministic);
    }

    [Fact]
    public void WindowsUsesRoamingForConfigAndDataAndLocalForCacheAndLogs()
    {
        var paths = new AppPaths(AppPlatform.Windows, WindowsFolder, _ => null);

        Assert.Equal(Path.GetFullPath("C:/Users/tester/AppData/Roaming/RemoteFlow"), paths.ConfigDirectory);
        Assert.Equal(paths.ConfigDirectory, paths.DataDirectory);
        Assert.Equal(Path.GetFullPath("C:/Users/tester/AppData/Local/RemoteFlow/Cache"), paths.CacheDirectory);
        Assert.Equal(Path.GetFullPath("C:/Users/tester/AppData/Local/RemoteFlow/Logs"), paths.LogDirectory);
    }

    [Fact]
    public void MacOSUsesLibraryDirectories()
    {
        var paths = new AppPaths(AppPlatform.MacOS, UnixFolder, _ => null);

        Assert.Equal(Path.GetFullPath($"{_home}/Library/Application Support/RemoteFlow"), paths.ConfigDirectory);
        Assert.Equal(paths.ConfigDirectory, paths.DataDirectory);
        Assert.Equal(Path.GetFullPath($"{_home}/Library/Caches/RemoteFlow"), paths.CacheDirectory);
        Assert.Equal(Path.GetFullPath($"{_home}/Library/Logs/RemoteFlow"), paths.LogDirectory);
    }

    [Fact]
    public void LinuxUsesXdgOverrides()
    {
        var variables = new Dictionary<string, string?>
        {
            ["XDG_CONFIG_HOME"] = "/xdg/config",
            ["XDG_DATA_HOME"] = "/xdg/data",
            ["XDG_CACHE_HOME"] = "/xdg/cache",
            ["XDG_STATE_HOME"] = "/xdg/state",
        };
        var paths = new AppPaths(
            AppPlatform.Linux,
            UnixFolder,
            name => variables.GetValueOrDefault(name));

        Assert.Equal(Path.GetFullPath("/xdg/config/remoteflow"), paths.ConfigDirectory);
        Assert.Equal(Path.GetFullPath("/xdg/data/remoteflow"), paths.DataDirectory);
        Assert.Equal(Path.GetFullPath("/xdg/cache/remoteflow"), paths.CacheDirectory);
        Assert.Equal(Path.GetFullPath("/xdg/state/remoteflow/logs"), paths.LogDirectory);
    }

    [Fact]
    public void LinuxFallsBackToFreedesktopDefaults()
    {
        var paths = new AppPaths(AppPlatform.Linux, UnixFolder, _ => null);

        Assert.Equal(Path.GetFullPath($"{_home}/.config/remoteflow"), paths.ConfigDirectory);
        Assert.Equal(Path.GetFullPath($"{_home}/.local/share/remoteflow"), paths.DataDirectory);
        Assert.Equal(Path.GetFullPath($"{_home}/.cache/remoteflow"), paths.CacheDirectory);
        Assert.Equal(Path.GetFullPath($"{_home}/.local/state/remoteflow/logs"), paths.LogDirectory);
    }

    [Fact]
    public void EnsureDirectoriesCreatesEveryDistinctDirectory()
    {
        using var directory = TemporaryDirectory.Create();
        var paths = new AppPaths(
            AppPlatform.Linux,
            _ => directory.Path,
            _ => null);

        paths.EnsureDirectories();

        Assert.True(Directory.Exists(paths.ConfigDirectory));
        Assert.True(Directory.Exists(paths.DataDirectory));
        Assert.True(Directory.Exists(paths.CacheDirectory));
        Assert.True(Directory.Exists(paths.LogDirectory));
    }

    private static string WindowsFolder(Environment.SpecialFolder folder)
    {
        return folder == Environment.SpecialFolder.ApplicationData
            ? "C:/Users/tester/AppData/Roaming"
            : folder == Environment.SpecialFolder.LocalApplicationData
                ? "C:/Users/tester/AppData/Local"
                : throw new ArgumentOutOfRangeException(nameof(folder));
    }

    private static string UnixFolder(Environment.SpecialFolder folder)
    {
        return folder == Environment.SpecialFolder.UserProfile
            ? _home
            : throw new ArgumentOutOfRangeException(nameof(folder));
    }
}
