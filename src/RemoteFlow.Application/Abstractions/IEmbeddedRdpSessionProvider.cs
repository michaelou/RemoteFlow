using RemoteFlow.Domain.Common;
using RemoteFlow.Domain.Entities;

namespace RemoteFlow.Application.Abstractions;

public enum EmbeddedRdpSessionState
{
    Created = 0,
    Connecting = 1,
    Connected = 2,
    Reconnecting = 3,
    Disconnected = 4,
    Failed = 5,
}

public sealed class EmbeddedRdpSessionStateChangedEventArgs(
    EmbeddedRdpSessionState previousState,
    EmbeddedRdpSessionState currentState,
    string? statusMessage) : EventArgs
{
    public EmbeddedRdpSessionState PreviousState { get; } = previousState;

    public EmbeddedRdpSessionState CurrentState { get; } = currentState;

    public string? StatusMessage { get; } = statusMessage;
}

/// <summary>A platform-neutral handle for an RDP session hosted inside RemoteFlow.</summary>
public interface IEmbeddedRdpSession : IAsyncDisposable
{
    EmbeddedRdpSessionState State { get; }

    string? StatusMessage { get; }

    event EventHandler<EmbeddedRdpSessionStateChangedEventArgs>? StateChanged;

    Task ConnectAsync(CancellationToken cancellationToken = default);

    Task DisconnectAsync(CancellationToken cancellationToken = default);

    Task ReconnectAsync(CancellationToken cancellationToken = default);

    void Resize(int width, int height, double scaling);
}

/// <summary>Creates embedded RDP sessions when the current platform supports them.</summary>
public interface IEmbeddedRdpSessionProvider
{
    /// <summary>Whether this provider can attempt an embedded session. Reading this property must not
    /// activate a native component or perform other platform work.</summary>
    bool SupportsEmbeddedSessions { get; }

    /// <summary>Creates a session, reporting expected platform, connection, and activation failures as a result.</summary>
    Task<Result<IEmbeddedRdpSession>> CreateAsync(
        Connection connection,
        CancellationToken cancellationToken = default);
}
