using RemoteFlow.Domain.Enums;

namespace RemoteFlow.Application.Abstractions.Backup;

public enum BackupExportScopeKind
{
    All = 1,
    FolderSubtree = 2,
    SelectedConnections = 3,
}

public sealed record BackupExportScope(
    BackupExportScopeKind Kind,
    Guid? FolderId = null,
    IReadOnlySet<Guid>? ConnectionIds = null)
{
    public static BackupExportScope All { get; } = new(BackupExportScopeKind.All);

    public static BackupExportScope FolderSubtree(Guid folderId)
    {
        return new BackupExportScope(BackupExportScopeKind.FolderSubtree, folderId);
    }

    public static BackupExportScope SelectedConnections(IEnumerable<Guid> connectionIds)
    {
        ArgumentNullException.ThrowIfNull(connectionIds);
        return new BackupExportScope(
            BackupExportScopeKind.SelectedConnections,
            ConnectionIds: connectionIds.ToHashSet());
    }
}

public sealed record BackupExportRequest(
    string DestinationPath,
    BackupExportScope Scope,
    bool IncludeSettings = true,
    bool IncludeHostKeys = true,
    bool IncludeCredentials = false,
    bool IncludeMachineName = true);

public sealed record BackupProgress(string Stage, int CompletedUnits, int TotalUnits)
{
    public double Percent => TotalUnits == 0 ? 0 : CompletedUnits * 100d / TotalUnits;
}

public sealed record BackupExportResult(string Path, BackupEntityCounts Counts)
{
    public string Summary =>
        $"Exported {Counts.Connections} connections, {Counts.Folders} folders, {Counts.Tags} tags, " +
        $"{Counts.ConnectionTags} tag links, {Counts.Settings} settings, and {Counts.HostKeys} host keys to '{Path}'.";
}

public sealed record BackupDataSnapshot(
    IReadOnlyList<BackupConnection> Connections,
    IReadOnlyList<BackupFolder> Folders,
    IReadOnlyList<BackupTag> Tags,
    IReadOnlyList<BackupConnectionTag> ConnectionTags,
    IReadOnlyList<BackupSetting> Settings,
    IReadOnlyList<BackupHostKey> HostKeys);

public interface IBackupDataSource
{
    Task<BackupDataSnapshot> CaptureAsync(
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IBackupService
{
    bool CanExportCredentials { get; }

    Task<BackupExportResult> ExportAsync(
        BackupExportRequest request,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<BackupInspection> InspectAsync(string path, CancellationToken cancellationToken = default);
}

public enum BackupConflictKind
{
    FolderPath = 1,
    TagName = 2,
    ConnectionIdentity = 3,
}

public sealed record BackupConflict(
    BackupConflictKind Kind,
    string Description,
    Guid ImportedId,
    Guid LocalId);

public sealed record BackupApplyPreview(
    MergeStrategy Strategy,
    BackupEntityCounts Adds,
    int Replaces,
    BackupEntityCounts Removes,
    string Description);

public sealed record BackupInspection(
    int FormatVersion,
    BackupEntityCounts Counts,
    bool ContainsCredentials,
    IReadOnlyList<BackupConflict> Conflicts,
    BackupApplyPreview MergePreview,
    BackupApplyPreview ReplacePreview);
