using RemoteFlow.Domain.Enums;

namespace RemoteFlow.Application.Abstractions;

public sealed class SessionTransitionEventArgs(
    ManagedSshSession session,
    SessionState previousState,
    SessionState currentState) : EventArgs
{
    public ManagedSshSession Session { get; } = session;
    public SessionState PreviousState { get; } = previousState;
    public SessionState CurrentState { get; } = currentState;
}

public sealed class ManagedSshSession
{
    public ManagedSshSession(
        Guid sessionId,
        Guid connectionId,
        string title,
        EnvironmentKind environment,
        string? colorOverrideHex,
        ITerminalChannel channel)
    {
        if (sessionId == Guid.Empty || connectionId == Guid.Empty)
        {
            throw new ArgumentException("Session and connection IDs are required.");
        }
        SessionId = sessionId;
        ConnectionId = connectionId;
        Title = string.IsNullOrWhiteSpace(title) ? throw new ArgumentException("A session title is required.", nameof(title)) : title;
        Environment = environment;
        ColorOverrideHex = colorOverrideHex;
        Channel = channel ?? throw new ArgumentNullException(nameof(channel));
    }

    public event EventHandler<SessionTransitionEventArgs>? Transitioned;

    public Guid SessionId { get; }
    public Guid ConnectionId { get; }
    public string Title { get; }
    public EnvironmentKind Environment { get; }
    public string? ColorOverrideHex { get; }
    public ITerminalChannel Channel { get; }
    public SessionState State { get; private set; } = SessionState.Created;
    public string? FailureReason { get; private set; }

    public void TransitionTo(SessionState nextState, string? failureReason = null)
    {
        if (nextState == State)
        {
            throw new InvalidOperationException($"The session is already in state {State}.");
        }
        if (!IsLegal(State, nextState))
        {
            throw new InvalidOperationException($"Illegal session transition: {State} -> {nextState}.");
        }
        if (nextState == SessionState.Failed && string.IsNullOrWhiteSpace(failureReason))
        {
            throw new ArgumentException("A failed session requires a failure reason.", nameof(failureReason));
        }
        var previous = State;
        State = nextState;
        FailureReason = nextState is SessionState.Failed or SessionState.Disconnected ? failureReason : null;
        Transitioned?.Invoke(this, new(this, previous, nextState));
    }

    private static bool IsLegal(SessionState current, SessionState next)
    {
        return current switch
        {
            SessionState.Created => next is SessionState.Connecting or SessionState.Closed,
            SessionState.Connecting => next is SessionState.Connected or SessionState.Failed or SessionState.Closed,
            SessionState.Connected => next is SessionState.Disconnected or SessionState.Failed or SessionState.Closed,
            SessionState.Reconnecting => next is SessionState.Connected or SessionState.Failed or SessionState.Disconnected or SessionState.Closed,
            SessionState.Disconnected => next is SessionState.Reconnecting or SessionState.Closed,
            SessionState.Failed => next is SessionState.Connecting or SessionState.Reconnecting or SessionState.Closed,
            SessionState.Closed => false,
            _ => throw new ArgumentOutOfRangeException(nameof(current)),
        };
    }
}

public interface ISessionManager : IAsyncDisposable
{
    event EventHandler<ManagedSshSession>? SessionAdded;
    event EventHandler<ManagedSshSession>? SessionRemoved;
    event EventHandler<SessionTransitionEventArgs>? SessionChanged;

    IReadOnlyList<ManagedSshSession> Sessions { get; }

    Task<ManagedSshSession> OpenAsync(Guid connectionId, CancellationToken cancellationToken = default);

    IReadOnlyList<ManagedSshSession> GetForConnection(Guid connectionId);

    Task RetryAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task CancelAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task CloseAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task ShutdownAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}
