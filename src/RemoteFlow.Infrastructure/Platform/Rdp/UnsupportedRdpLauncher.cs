using RemoteFlow.Application.Abstractions;
using RemoteFlow.Domain.Entities;

namespace RemoteFlow.Infrastructure.Platform.Rdp;

/// <summary>Stands in on the platforms RemoteFlow does not launch RDP on. It says so plainly rather than
/// failing somewhere further down, and it points at the clients a person can use in the meantime.</summary>
public sealed class UnsupportedRdpLauncher(ISystemPlatform platform) : IRdpLauncher
{
    private readonly ISystemPlatform _platform = platform ?? throw new ArgumentNullException(nameof(platform));

    public string MissingClientGuidance => _platform.OperatingSystem switch
    {
        OperatingSystemFamily.MacOs =>
            "RemoteFlow does not launch RDP on macOS yet. Install Windows App from the Mac App Store and " +
            "connect to this host from there.",
        OperatingSystemFamily.Linux =>
            "RemoteFlow does not launch RDP on Linux yet. Install a client such as FreeRDP " +
            "(apt install freerdp3-x11, dnf install freerdp, pacman -S freerdp) or Remmina, and connect " +
            "to this host from there.",
        OperatingSystemFamily.Windows => "Remote Desktop Connection was not found on this machine.",
        _ => "RemoteFlow does not launch RDP on this operating system.",
    };

    public Task<RdpLaunchResult> LaunchAsync(Connection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(RdpLaunchResult.UnsupportedPlatform(MissingClientGuidance));
    }

    public Task<IReadOnlyList<RdpClientInfo>> DetectClientsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<RdpClientInfo>>([]);
    }

    public Task SweepStaleFilesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
