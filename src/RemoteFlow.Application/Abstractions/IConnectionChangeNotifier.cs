namespace RemoteFlow.Application.Abstractions;

public enum ConnectionChangeKind
{
    Created = 1,
    Updated = 2,
    Deleted = 3,
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
}

public sealed class ConnectionChangeNotifier : IConnectionChangeNotifier
{
    public event EventHandler<ConnectionChangedEventArgs>? ConnectionChanged;

    public void Notify(Guid connectionId, ConnectionChangeKind kind)
    {
        ConnectionChanged?.Invoke(this, new ConnectionChangedEventArgs(connectionId, kind));
    }
}
