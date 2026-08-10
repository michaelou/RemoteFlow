using RemoteFlow.Application.Abstractions;
using RemoteFlow.Domain.Common;
using RemoteFlow.Domain.Entities;
using RemoteFlow.Domain.Enums;

namespace RemoteFlow.Application.Services;

public sealed class NoEmbeddedRdpSessionProvider : IEmbeddedRdpSessionProvider
{
    public static NoEmbeddedRdpSessionProvider Instance { get; } = new();

    private NoEmbeddedRdpSessionProvider()
    {
    }

    public bool SupportsEmbeddedSessions => false;

    public Task<Result<IEmbeddedRdpSession>> CreateAsync(
        Connection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(connection.Protocol != ProtocolType.Rdp
            ? Result<IEmbeddedRdpSession>.Failure(RemoteFlowError.Validation(
                "embedded_rdp.not_an_rdp_connection",
                "The connection is not an RDP connection."))
            : Result<IEmbeddedRdpSession>.Failure(RemoteFlowError.Unavailable(
                "embedded_rdp.unsupported_platform",
                "Embedded RDP is not available on this platform. Use an external RDP client instead.")));
    }
}
