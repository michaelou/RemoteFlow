using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Backup;

namespace RemoteFlow.Application.Services;

public sealed class BackupService(
    IBackupDataSource dataSource,
    IBackupArchiveSerializer serializer,
    IClock clock) : IBackupService
{
    private readonly IBackupDataSource _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    private readonly IBackupArchiveSerializer _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public bool CanExportCredentials => false;

    public async Task<BackupExportResult> ExportAsync(
        BackupExportRequest request,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationPath);
        ArgumentNullException.ThrowIfNull(request.Scope);
        ValidateScope(request.Scope);
        if (request.IncludeCredentials)
        {
            throw new BackupArchiveException(
                "Credential export is not available until encrypted credential backup is enabled.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new BackupProgress("Reading RemoteFlow data", 0, 8));
        var snapshot = await _dataSource.CaptureAsync(progress, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var selected = Select(snapshot, request);
        var counts = new BackupEntityCounts(
            selected.Connections.Count,
            selected.Folders.Count,
            selected.Tags.Count,
            selected.ConnectionTags.Count,
            selected.Settings.Count,
            selected.HostKeys.Count);
        var manifest = new BackupManifest(
            BackupFormat.CurrentVersion,
            typeof(BackupService).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            _clock.UtcNow.ToUniversalTime(),
            request.IncludeMachineName ? Environment.MachineName : null,
            counts,
            IncludesCredentials: false);
        var archive = new BackupArchive(
            manifest,
            selected.Connections,
            selected.Folders,
            selected.Tags,
            selected.ConnectionTags,
            selected.Settings,
            selected.HostKeys);

        progress?.Report(new BackupProgress("Writing archive", 7, 8));
        await _serializer.WriteAsync(request.DestinationPath, archive, cancellationToken).ConfigureAwait(false);
        progress?.Report(new BackupProgress("Backup complete", 8, 8));
        return new BackupExportResult(Path.GetFullPath(request.DestinationPath), counts);
    }

    private static BackupDataSnapshot Select(BackupDataSnapshot snapshot, BackupExportRequest request)
    {
        var selectedConnections = SelectConnections(snapshot, request.Scope);
        var selectedConnectionIds = selectedConnections.Select(connection => connection.Id).ToHashSet();
        var selectedConnectionTags = snapshot.ConnectionTags
            .Where(link => selectedConnectionIds.Contains(link.ConnectionId))
            .ToArray();
        var selectedTagIds = selectedConnectionTags.Select(link => link.TagId).ToHashSet();
        var selectedTags = request.Scope.Kind == BackupExportScopeKind.All
            ? snapshot.Tags
            : [.. snapshot.Tags.Where(tag => selectedTagIds.Contains(tag.Id))];
        var selectedFolders = request.Scope.Kind == BackupExportScopeKind.All
            ? snapshot.Folders
            : request.Scope.Kind == BackupExportScopeKind.FolderSubtree
                ? SelectSubtreeFolders(snapshot.Folders, request.Scope.FolderId!.Value)
                : SelectRequiredFolders(snapshot.Folders, selectedConnections);
        var selectedHostKeys = !request.IncludeHostKeys
            ? []
            : request.Scope.Kind == BackupExportScopeKind.All
                ? snapshot.HostKeys
                : [.. snapshot.HostKeys.Where(hostKey => selectedConnections.Any(connection =>
                    string.Equals(connection.Host, hostKey.Host, StringComparison.OrdinalIgnoreCase) &&
                    connection.Port == hostKey.Port))];

        return new BackupDataSnapshot(
            selectedConnections,
            selectedFolders,
            selectedTags,
            selectedConnectionTags,
            request.IncludeSettings ? snapshot.Settings : [],
            selectedHostKeys);
    }

    private static IReadOnlyList<BackupConnection> SelectConnections(
        BackupDataSnapshot snapshot,
        BackupExportScope scope)
    {
        return scope.Kind switch
        {
            BackupExportScopeKind.All => snapshot.Connections,
            BackupExportScopeKind.FolderSubtree => SelectSubtreeConnections(snapshot, scope.FolderId!.Value),
            BackupExportScopeKind.SelectedConnections =>
                [.. snapshot.Connections.Where(connection => scope.ConnectionIds!.Contains(connection.Id))],
            _ => throw new ArgumentOutOfRangeException(nameof(scope), "The backup scope is invalid."),
        };
    }

    private static BackupConnection[] SelectSubtreeConnections(
        BackupDataSnapshot snapshot,
        Guid folderId)
    {
        var root = snapshot.Folders.FirstOrDefault(folder => folder.Id == folderId)
            ?? throw new BackupArchiveException($"The selected folder '{folderId}' no longer exists.");
        var folderIds = snapshot.Folders
            .Where(folder => folder.Id == root.Id || folder.Path.StartsWith($"{root.Path}/", StringComparison.OrdinalIgnoreCase))
            .Select(folder => folder.Id)
            .ToHashSet();
        return [.. snapshot.Connections.Where(connection =>
            connection.FolderId is { } selectedFolderId && folderIds.Contains(selectedFolderId))];
    }

    private static BackupFolder[] SelectSubtreeFolders(IReadOnlyList<BackupFolder> folders, Guid folderId)
    {
        var root = folders.FirstOrDefault(folder => folder.Id == folderId)
            ?? throw new BackupArchiveException($"The selected folder '{folderId}' no longer exists.");
        var selectedIds = folders
            .Where(folder => folder.Id == root.Id || folder.Path.StartsWith($"{root.Path}/", StringComparison.OrdinalIgnoreCase))
            .Select(folder => folder.Id)
            .ToHashSet();
        var byId = folders.ToDictionary(folder => folder.Id);
        var currentId = root.ParentId;
        while (currentId is { } id && selectedIds.Add(id))
        {
            currentId = byId.TryGetValue(id, out var folder)
                ? folder.ParentId
                : throw new BackupArchiveException($"Folder '{root.Path}' has a missing ancestor.");
        }

        return [.. folders.Where(folder => selectedIds.Contains(folder.Id)).OrderBy(folder => folder.Depth)];
    }

    private static BackupFolder[] SelectRequiredFolders(
        IReadOnlyList<BackupFolder> folders,
        IReadOnlyList<BackupConnection> connections)
    {
        var byId = folders.ToDictionary(folder => folder.Id);
        var selectedIds = new HashSet<Guid>();
        foreach (var connection in connections)
        {
            var currentId = connection.FolderId;
            while (currentId is { } id && selectedIds.Add(id))
            {
                currentId = byId.TryGetValue(id, out var folder)
                    ? folder.ParentId
                    : throw new BackupArchiveException($"Connection '{connection.Name}' references a missing folder.");
            }
        }

        return [.. folders.Where(folder => selectedIds.Contains(folder.Id)).OrderBy(folder => folder.Depth)];
    }

    private static void ValidateScope(BackupExportScope scope)
    {
        if (scope.Kind == BackupExportScopeKind.FolderSubtree &&
            (scope.FolderId is null || scope.FolderId == Guid.Empty))
        {
            throw new ArgumentException("A folder subtree export requires a folder ID.", nameof(scope));
        }

        if (scope.Kind == BackupExportScopeKind.SelectedConnections && scope.ConnectionIds is null)
        {
            throw new ArgumentException("A selected-connections export requires a connection ID set.", nameof(scope));
        }

        if (!Enum.IsDefined(scope.Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(scope), "The backup scope is invalid.");
        }
    }
}
