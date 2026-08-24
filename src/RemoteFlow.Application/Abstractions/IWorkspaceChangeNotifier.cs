namespace RemoteFlow.Application.Abstractions;

public enum WorkspaceEntityKind
{
    Folder = 1,
    Tag = 2,
}

public enum WorkspaceChangeKind
{
    Created = 1,
    Updated = 2,
    Deleted = 3,
}

public sealed class WorkspaceChangedEventArgs(
    WorkspaceEntityKind entity,
    Guid entityId,
    WorkspaceChangeKind kind) : EventArgs
{
    public WorkspaceEntityKind Entity { get; } = entity;

    /// <summary>The folder or tag that changed, or <see cref="Guid.Empty"/> when the operation touched an
    /// unspecified set of them — a sweep of orphaned tags, for instance.</summary>
    public Guid EntityId { get; } = entityId;

    public WorkspaceChangeKind Kind { get; } = kind;
}

/// <summary>Announces folder and tag edits, the way <see cref="IConnectionChangeNotifier"/> announces
/// connection edits. Deliberately a sibling rather than more members on <c>ConnectionChangeKind</c>: that
/// event carries a <c>ConnectionId</c>, and a field holding a folder's ID under that name is a lie the
/// next reader would believe.
///
/// Like its sibling this is a plain synchronous event, raised on whichever thread completed the mutation.
/// A subscriber that throws throws into the caller's save, and one that blocks blocks it — so handlers do
/// no I/O and return immediately.</summary>
public interface IWorkspaceChangeNotifier
{
    event EventHandler<WorkspaceChangedEventArgs>? WorkspaceChanged;

    void Notify(WorkspaceEntityKind entity, Guid entityId, WorkspaceChangeKind kind);
}

public sealed class WorkspaceChangeNotifier : IWorkspaceChangeNotifier
{
    public event EventHandler<WorkspaceChangedEventArgs>? WorkspaceChanged;

    public void Notify(WorkspaceEntityKind entity, Guid entityId, WorkspaceChangeKind kind)
    {
        WorkspaceChanged?.Invoke(this, new WorkspaceChangedEventArgs(entity, entityId, kind));
    }
}
