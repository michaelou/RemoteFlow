using RemoteFlow.Domain.Entities;

namespace RemoteFlow.Application.Abstractions;

/// <summary>An RDP client found on this machine.</summary>
public sealed record RdpClientInfo(string Name, string Path, string? Version)
{
    public string Description => string.IsNullOrWhiteSpace(Version) ? Name : $"{Name} {Version}";
}

public enum RdpLaunchStatus
{
    Launched = 0,

    /// <summary>Nothing on this machine can open an RDP session.</summary>
    ClientNotFound = 1,

    /// <summary>The connection is not an RDP connection, so there is nothing to launch.</summary>
    NotAnRdpConnection = 2,

    /// <summary>RemoteFlow does not launch RDP on this operating system.</summary>
    UnsupportedPlatform = 3,

    /// <summary>The client is installed but starting it failed.</summary>
    Failed = 4,
}

/// <summary>The outcome of a launch. A launch never throws for an expected failure — a missing client,
/// a client that refuses to start — it reports one of these instead.</summary>
public sealed record RdpLaunchResult(RdpLaunchStatus Status, string? Message = null)
{
    public bool Succeeded => Status == RdpLaunchStatus.Launched;

    public static RdpLaunchResult Launched { get; } = new(RdpLaunchStatus.Launched);

    public static RdpLaunchResult ClientNotFound(string message)
    {
        return new(RdpLaunchStatus.ClientNotFound, message);
    }

    public static RdpLaunchResult NotAnRdpConnection(string message)
    {
        return new(RdpLaunchStatus.NotAnRdpConnection, message);
    }

    public static RdpLaunchResult UnsupportedPlatform(string message)
    {
        return new(RdpLaunchStatus.UnsupportedPlatform, message);
    }

    public static RdpLaunchResult Failed(string message)
    {
        return new(RdpLaunchStatus.Failed, message);
    }
}

/// <summary>Opens a connection in the platform's own RDP client. This is the external path: it generates
/// the right client invocation and hands the session over, independently of embedded RDP support.</summary>
public interface IRdpLauncher
{
    /// <summary>What to install, in the words of this platform, when <see cref="DetectClientsAsync"/>
    /// comes back empty.</summary>
    string MissingClientGuidance { get; }

    Task<RdpLaunchResult> LaunchAsync(Connection connection, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RdpClientInfo>> DetectClientsAsync(CancellationToken cancellationToken = default);

    /// <summary>Removes launch files left behind by a crash. Safe to call at startup and only then —
    /// a launch cleans up after itself.</summary>
    Task SweepStaleFilesAsync(CancellationToken cancellationToken = default);
}
