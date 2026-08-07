using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Services;
using RemoteFlow.TestSupport;
using Xunit;

namespace RemoteFlow.Application.Tests;

public sealed class ShellProfileServiceTests
{
    [Fact]
    public async Task WindowsDetectionPrefersPowerShellSevenThenWindowsPowerShellThenCmd()
    {
        var token = TestContext.Current.CancellationToken;
        var platform = new FakePlatform(OperatingSystemFamily.Windows);
        platform.AddExecutable("pwsh.exe", "C:\\Tools\\pwsh.exe");
        platform.AddExecutable("powershell.exe", "C:\\Windows\\powershell.exe");
        platform.AddExecutable("cmd.exe", "C:\\Windows\\cmd.exe");
        var service = new ShellProfileService(new InMemorySettingsStore(), platform);

        Assert.Equal("C:\\Tools\\pwsh.exe", (await service.GetDefaultProfileAsync(token)).ShellPath);

        platform.RemoveExecutable("pwsh.exe");
        var second = new ShellProfileService(new InMemorySettingsStore(), platform);
        Assert.Equal("C:\\Windows\\powershell.exe", (await second.GetDefaultProfileAsync(token)).ShellPath);

        platform.RemoveExecutable("powershell.exe");
        var third = new ShellProfileService(new InMemorySettingsStore(), platform);
        Assert.Equal("C:\\Windows\\cmd.exe", (await third.GetDefaultProfileAsync(token)).ShellPath);
    }

    [Fact]
    public async Task PosixDetectionUsesShellThenPasswdAndFallsBackToBinSh()
    {
        var token = TestContext.Current.CancellationToken;
        var platform = new FakePlatform(OperatingSystemFamily.Linux)
        {
            LoginShell = "/bin/zsh",
        };
        platform.Environment["SHELL"] = "/opt/fish";
        platform.Files.UnionWith(["/opt/fish", "/bin/zsh", "/bin/sh"]);
        var service = new ShellProfileService(new InMemorySettingsStore(), platform);
        Assert.Equal("/opt/fish", (await service.GetDefaultProfileAsync(token)).ShellPath);

        _ = platform.Files.Remove("/opt/fish");
        var passwd = new ShellProfileService(new InMemorySettingsStore(), platform);
        Assert.Equal("/bin/zsh", (await passwd.GetDefaultProfileAsync(token)).ShellPath);

        _ = platform.Files.Remove("/bin/zsh");
        var fallback = new ShellProfileService(new InMemorySettingsStore(), platform);
        Assert.Equal("/bin/sh", (await fallback.GetDefaultProfileAsync(token)).ShellPath);
    }

    [Fact]
    public async Task NamedProfilesDefaultAndSpawnOptionsRoundTripExactly()
    {
        var token = TestContext.Current.CancellationToken;
        var settings = new InMemorySettingsStore();
        var platform = new FakePlatform(OperatingSystemFamily.Linux);
        platform.Files.UnionWith(["/bin/bash", "/bin/zsh"]);
        var service = new ShellProfileService(settings, platform);
        ShellProfile[] profiles =
        [
            new()
            {
                Id = "bash",
                DisplayName = "Bash",
                ShellPath = "/bin/bash",
                Arguments = ["--noprofile"],
                WorkingDirectory = "/work/bash",
                EnvironmentVariables = new Dictionary<string, string> { ["PROFILE_MARKER"] = "bash" },
                Icon = "B",
            },
            new()
            {
                Id = "zsh",
                DisplayName = "Z shell",
                ShellPath = "/bin/zsh",
                Arguments = ["-d", "-f"],
                WorkingDirectory = "/work/zsh",
                EnvironmentVariables = new Dictionary<string, string> { ["PROFILE_MARKER"] = "zsh" },
                Icon = "Z",
            },
        ];

        await service.SaveProfilesAsync(profiles, "zsh", token);
        var restarted = new ShellProfileService(settings, platform);
        var selected = await restarted.GetDefaultProfileAsync(token);
        var options = restarted.CreateSpawnOptions(selected);

        Assert.Equal("zsh", selected.Id);
        Assert.Equal("/bin/zsh", options.ShellPath);
        Assert.Equal(["-d", "-f"], options.Arguments);
        Assert.Equal("/work/zsh", options.WorkingDirectory);
        Assert.Equal("zsh", options.EnvironmentVariables["PROFILE_MARKER"]);
        Assert.Equal("xterm-256color", options.EnvironmentVariables["TERM"]);
        Assert.Equal("truecolor", options.EnvironmentVariables["COLORTERM"]);
    }

    [Fact]
    public void BadExecutableProducesActionableProfileMessage()
    {
        var service = new ShellProfileService(
            new InMemorySettingsStore(),
            new FakePlatform(OperatingSystemFamily.Windows));
        var profile = new ShellProfile
        {
            Id = "missing",
            DisplayName = "Broken profile",
            ShellPath = "C:\\Missing\\shell.exe",
            WorkingDirectory = "C:\\work",
        };

        var exception = Assert.Throws<FileNotFoundException>(() => service.CreateSpawnOptions(profile));

        Assert.Contains("Broken profile", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Settings > Terminal", exception.Message, StringComparison.Ordinal);
    }

    private sealed class FakePlatform(OperatingSystemFamily operatingSystem) : ISystemPlatform
    {
        private readonly Dictionary<string, string> _executables = new(StringComparer.OrdinalIgnoreCase);

        public OperatingSystemFamily OperatingSystem { get; } = operatingSystem;
        public string CurrentDirectory { get; set; } = operatingSystem == OperatingSystemFamily.Windows ? "C:\\work" : "/work";
        public string HomeDirectory { get; set; } = operatingSystem == OperatingSystemFamily.Windows ? "C:\\Users\\tester" : "/home/tester";
        public Dictionary<string, string> Environment { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? LoginShell { get; set; }

        public string? GetEnvironmentVariable(string name)
        {
            return Environment.GetValueOrDefault(name);
        }

        public string? FindExecutable(string name)
        {
            return _executables.GetValueOrDefault(name) ?? (Files.Contains(name) ? name : null);
        }

        public bool FileExists(string path)
        {
            return Files.Contains(path);
        }

        public string? GetLoginShellFromPasswd()
        {
            return LoginShell;
        }

        public void AddExecutable(string name, string path)
        {
            _executables[name] = path;
            _ = Files.Add(path);
        }

        public void RemoveExecutable(string name)
        {
            if (_executables.Remove(name, out var path))
            {
                _ = Files.Remove(path);
            }
        }
    }
}
