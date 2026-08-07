using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Services;
using RemoteFlow.Domain.Abstractions;
using RemoteFlow.Domain.Entities;
using RemoteFlow.Domain.Enums;
using RemoteFlow.Domain.ValueObjects;
using RemoteFlow.Infrastructure.Platform;
using Xunit;

namespace RemoteFlow.Infrastructure.Tests;

public sealed class SystemTerminalLauncherTests
{
    [Fact]
    public async Task WindowsTerminalSshUsesExactArgvAndNeverPassesPassword()
    {
        var token = TestContext.Current.CancellationToken;
        var platform = new FakePlatform(OperatingSystemFamily.Windows)
        {
            HomeDirectory = "C:\\Users\\operator",
        };
        platform.Executables["wt.exe"] = "C:\\Apps\\wt.exe";
        platform.Executables["ssh.exe"] = "C:\\Windows\\ssh.exe";
        var runner = new RecordingProcessRunner();
        var launcher = new SystemTerminalLauncher(platform, runner);
        var connection = CreateSshConnection();

        var result = await launcher.OpenSshAsync(connection, token);

        Assert.True(result.Succeeded);
        var request = Assert.Single(runner.Requests);
        Assert.Equal("C:\\Apps\\wt.exe", request.FileName);
        Assert.Equal(
            ["-d", "C:\\Users\\operator", "C:\\Windows\\ssh.exe", "-p", "2202", "-i", "C:\\keys\\id_ed25519", "deploy@example.test"],
            request.Arguments);
        Assert.Null(request.EnvironmentVariables);
        Assert.DoesNotContain("TOP_SECRET_PASSWORD", string.Join('|', request.Arguments), StringComparison.Ordinal);
        Assert.DoesNotContain(connection.Credential.StoreKey, string.Join('|', request.Arguments), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WindowsFallbackOrderUsesPwshBeforeConhost()
    {
        var platform = new FakePlatform(OperatingSystemFamily.Windows);
        platform.Executables["pwsh.exe"] = "C:\\pwsh.exe";
        platform.Executables["conhost.exe"] = "C:\\conhost.exe";
        var runner = new RecordingProcessRunner();
        var launcher = new SystemTerminalLauncher(platform, runner);
        var profile = new ShellProfile
        {
            Id = "cmd",
            DisplayName = "Command Prompt",
            ShellPath = "C:\\cmd.exe",
            Arguments = ["/K"],
            WorkingDirectory = "C:\\work",
        };

        var result = await launcher.OpenLocalAsync(profile, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var request = Assert.Single(runner.Requests);
        Assert.Equal("C:\\pwsh.exe", request.FileName);
        Assert.Equal(["-NoExit", "-Command", "&", "C:\\cmd.exe", "/K"], request.Arguments);
    }

    [Fact]
    public async Task LinuxUsesTerminalEnvironmentBeforeFallbackList()
    {
        var platform = new FakePlatform(OperatingSystemFamily.Linux);
        platform.Environment["TERMINAL"] = "alacritty";
        platform.Executables["alacritty"] = "/usr/bin/alacritty";
        platform.Executables["x-terminal-emulator"] = "/usr/bin/x-terminal-emulator";
        var runner = new RecordingProcessRunner();
        var launcher = new SystemTerminalLauncher(platform, runner);

        var result = await launcher.OpenLocalAsync(new ShellProfile
        {
            Id = "bash",
            DisplayName = "Bash",
            ShellPath = "/bin/bash",
            Arguments = ["-l"],
            WorkingDirectory = "/work",
        }, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var request = Assert.Single(runner.Requests);
        Assert.Equal("/usr/bin/alacritty", request.FileName);
        Assert.Equal(["-e", "/bin/bash", "-l"], request.Arguments);
    }

    [Fact]
    public async Task MacUsesTerminalAppWhenPresent()
    {
        var platform = new FakePlatform(OperatingSystemFamily.MacOs);
        _ = platform.Files.Add("/Applications/Terminal.app");
        platform.Executables["open"] = "/usr/bin/open";
        var runner = new RecordingProcessRunner();
        var launcher = new SystemTerminalLauncher(platform, runner);

        var result = await launcher.OpenLocalAsync(new ShellProfile
        {
            Id = "zsh",
            DisplayName = "Zsh",
            ShellPath = "/bin/zsh",
            WorkingDirectory = "/work",
        }, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(["-a", "Terminal", "--args", "/bin/zsh"], Assert.Single(runner.Requests).Arguments);
    }

    [Fact]
    public async Task NoTerminalProducesActionableInstallMessage()
    {
        var launcher = new SystemTerminalLauncher(
            new FakePlatform(OperatingSystemFamily.Linux),
            new RecordingProcessRunner());

        var result = await launcher.OpenLocalAsync(new ShellProfile
        {
            Id = "shell",
            DisplayName = "Shell",
            ShellPath = "/bin/sh",
            WorkingDirectory = "/work",
        }, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("Install", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("xterm", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    private static Connection CreateSshConnection()
    {
        var connection = Connection.Create(
            SystemGuidProvider.Instance,
            "Production",
            "example.test",
            2202,
            ProtocolType.Ssh,
            DateTimeOffset.UtcNow).Value;
        _ = connection.SetDetails(
            "deploy",
            AuthMethod.Password,
            null,
            EnvironmentKind.Production,
            null,
            SystemGuidProvider.Instance);
        var ssh = SshOptions.Default();
        _ = ssh.Configure(privateKeyPath: "C:\\keys\\id_ed25519");
        _ = connection.SetOptions(ssh, SftpOptions.Default(), RdpOptions.Default(), SystemGuidProvider.Instance);
        _ = connection.SetCredential(
            CredentialRef.Create(
                CredentialKind.Password,
                "credential/TOP_SECRET_PASSWORD",
                "test").Value,
            SystemGuidProvider.Instance);
        return connection;
    }

    private sealed class RecordingProcessRunner : IProcessRunner
    {
        public List<ProcessLaunchRequest> Requests { get; } = [];

        public Task RunAsync(ProcessLaunchRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.CompletedTask;
        }
    }

    private sealed class FakePlatform(OperatingSystemFamily operatingSystem) : ISystemPlatform
    {
        public OperatingSystemFamily OperatingSystem { get; } = operatingSystem;
        public string CurrentDirectory { get; set; } = operatingSystem == OperatingSystemFamily.Windows ? "C:\\work" : "/work";
        public string HomeDirectory { get; set; } = operatingSystem == OperatingSystemFamily.Windows ? "C:\\Users\\test" : "/home/test";
        public Dictionary<string, string> Environment { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Executables { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? GetEnvironmentVariable(string name)
        {
            return Environment.GetValueOrDefault(name);
        }

        public string? FindExecutable(string name)
        {
            return Executables.GetValueOrDefault(name);
        }

        public bool FileExists(string path)
        {
            return Files.Contains(path);
        }

        public string? GetLoginShellFromPasswd()
        {
            return null;
        }
    }
}
