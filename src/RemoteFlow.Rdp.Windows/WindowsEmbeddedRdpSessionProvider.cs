using RemoteFlow.Application.Abstractions;
using RemoteFlow.Domain.Common;
using RemoteFlow.Domain.Entities;
using RemoteFlow.Domain.Enums;

namespace RemoteFlow.Rdp.Windows;

/// <summary>The Windows capability registration. Native session activation is implemented separately.</summary>
public sealed class WindowsEmbeddedRdpSessionProvider : IEmbeddedRdpSessionProvider
{
    public static WindowsEmbeddedRdpSessionProvider Instance { get; } = new();

    private WindowsEmbeddedRdpSessionProvider()
    {
    }

    public bool SupportsEmbeddedSessions => true;

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
                "embedded_rdp.activation_unavailable",
                "The embedded RDP control could not be activated.")));
    }
}
