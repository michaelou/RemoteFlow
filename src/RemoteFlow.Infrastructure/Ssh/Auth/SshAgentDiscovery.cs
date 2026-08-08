using System.Diagnostics;
using RemoteFlow.Application.Abstractions;

namespace RemoteFlow.Infrastructure.Ssh.Auth;

public sealed record SshAgentEndpoint(string Name, string Address, bool IsAvailable);

public interface ISshAgentDiscovery
{
    IReadOnlyList<SshAgentEndpoint> Discover();
}

public sealed class SshAgentDiscovery(ISystemPlatform platform) : ISshAgentDiscovery
{
    private readonly ISystemPlatform _platform = platform ?? throw new ArgumentNullException(nameof(platform));

    public IReadOnlyList<SshAgentEndpoint> Discover()
    {
        var endpoints = new List<SshAgentEndpoint>();
        var configuredSocket = _platform.GetEnvironmentVariable("SSH_AUTH_SOCK");
        if (!string.IsNullOrWhiteSpace(configuredSocket))
        {
            endpoints.Add(new("SSH_AUTH_SOCK", configuredSocket, File.Exists(configuredSocket)));
        }

        if (_platform.OperatingSystem == OperatingSystemFamily.Windows)
        {
            const string openSshPipe = @"\\.\pipe\openssh-ssh-agent";
            endpoints.Add(new("Windows OpenSSH", openSshPipe, File.Exists(openSshPipe)));
            var pageantAvailable = Process.GetProcessesByName("pageant").Length > 0;
            endpoints.Add(new("Pageant", "Pageant IPC", pageantAvailable));
        }

        var onePassword = _platform.OperatingSystem switch
        {
            OperatingSystemFamily.MacOs => Path.Combine(
                _platform.HomeDirectory,
                "Library",
                "Group Containers",
                "2BUA8C4S2C.com.1password",
                "t",
                "agent.sock"),
            OperatingSystemFamily.Linux => Path.Combine(_platform.HomeDirectory, ".1password", "agent.sock"),
            OperatingSystemFamily.Windows => null,
            _ => throw new ArgumentOutOfRangeException(),
        };
        if (onePassword is not null && !endpoints.Any(item => string.Equals(item.Address, onePassword, StringComparison.Ordinal)))
        {
            endpoints.Add(new("1Password", onePassword, File.Exists(onePassword)));
        }

        return endpoints;
    }
}
