namespace RemoteFlow.Application.Abstractions;

public enum ConnectionChangeKind
{
    Created = 1,
    Updated = 2,
    Deleted = 3,

    /// <summary>Every connection may have changed at once, because a backup import rewrote the store.
    /// Listeners reload from scratch instead of patching the one row an ID points at.</summary>
    Reloaded = 4,
}

public sealed class ConnectionChangedEventArgs(Guid connectionId, ConnectionChangeKind kind) : EventArgs
{
    public Guid ConnectionId { get; } = connectionId;

    public ConnectionChangeKind Kind { get; } = kind;
}

public interface IConnectionChangeNotifier
{
    event EventHandler<ConnectionChangedEventArgs>? ConnectionChanged;

    void Notify(Guid connectionId, ConnectionChangeKind kind);

    /// <summary>Announces that the whole connection store was rewritten, so no single ID describes the
    /// change. The event carries <see cref="Guid.Empty"/> as its connection ID.</summary>
    void NotifyReloaded();
}

public sealed class ConnectionChangeNotifier : IConnectionChangeNotifier
{
    public event EventHandler<ConnectionChangedEventArgs>? ConnectionChanged;

    public void Notify(Guid connectionId, ConnectionChangeKind kind)
    {
        ConnectionChanged?.Invoke(this, new ConnectionChangedEventArgs(connectionId, kind));
    }

    public void NotifyReloaded()
    {
        Notify(Guid.Empty, ConnectionChangeKind.Reloaded);
    }
}
