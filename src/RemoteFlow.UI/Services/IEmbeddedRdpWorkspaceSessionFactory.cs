using RemoteFlow.Domain.Common;
using RemoteFlow.Domain.Entities;
using RemoteFlow.UI.ViewModels.Terminal;

namespace RemoteFlow.UI.Services;

public interface IEmbeddedRdpWorkspaceSession : IWorkspaceSessionViewModel
{
    void PrepareForConnect();

    Task ConnectAsync(CancellationToken cancellationToken = default);
}

/// <summary>Bridges the cross-platform open workflow to a platform-owned embedded RDP tab.</summary>
public interface IEmbeddedRdpWorkspaceSessionFactory
{
    bool IsAvailableOnCurrentPlatform { get; }

    bool SupportsEmbeddedSessions { get; }

    Task<Result<IEmbeddedRdpWorkspaceSession>> CreateAsync(
        Connection connection,
        CancellationToken cancellationToken = default);
}

public sealed class NoEmbeddedRdpWorkspaceSessionFactory : IEmbeddedRdpWorkspaceSessionFactory
{
    public static NoEmbeddedRdpWorkspaceSessionFactory Instance { get; } = new();

    private NoEmbeddedRdpWorkspaceSessionFactory()
    {
    }

    public bool IsAvailableOnCurrentPlatform => false;

    public bool SupportsEmbeddedSessions => false;

    public Task<Result<IEmbeddedRdpWorkspaceSession>> CreateAsync(
        Connection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Result<IEmbeddedRdpWorkspaceSession>.Failure(RemoteFlowError.Unavailable(
            "embedded_rdp.unsupported_platform",
            "Embedded RDP is not available on this platform.")));
    }
}
