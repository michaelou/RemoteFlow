using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Backup;
using RemoteFlow.Domain.Enums;

namespace RemoteFlow.Application.Services;

public sealed class BackupService(
    IBackupDataSource dataSource,
    IBackupArchiveSerializer serializer,
    IClock clock,
    IBackupImportStore? importStore = null) : IBackupService
{
    private readonly IBackupDataSource _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    private readonly IBackupArchiveSerializer _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly IBackupImportStore? _importStore = importStore;

    public bool CanExportCredentials => false;

    public async Task<BackupImportResult> ApplyAsync(
        BackupApplyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Path);
        if (!Enum.IsDefined(request.Strategy) || !Enum.IsDefined(request.ConflictPolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "The import strategy or conflict policy is invalid.");
        }

        if (request.Strategy == MergeStrategy.Replace &&
            !string.Equals(request.ReplaceConfirmation, "REPLACE", StringComparison.Ordinal))
        {
            throw new BackupArchiveException("Replace requires typing REPLACE exactly.");
        }

        var store = _importStore
            ?? throw new InvalidOperationException("Backup import persistence is not configured.");
        var archive = await _serializer.ReadAsync(request.Path, cancellationToken).ConfigureAwait(false);
        var local = await _dataSource.CaptureAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var missingCredentials = archive.EncryptedCredentials is null
            ? archive.Connections
                .Where(connection => connection.Credential.Kind != CredentialKind.None)
                .Select(connection => $"Connection '{connection.Name}' references a credential that was not included.")
                .ToArray()
            : [];
        var sanitized = archive with
        {
            Connections = archive.EncryptedCredentials is null
                ? [.. archive.Connections.Select(ClearCredential)]
                : archive.Connections,
        };
        var plan = request.Strategy == MergeStrategy.Replace
            ? BackupMergePlanner.Replace(sanitized)
            : BackupMergePlanner.Merge(sanitized, local, request.ConflictPolicy);

        var storeResult = await store.ApplyAsync(plan.Target, request.Strategy, cancellationToken).ConfigureAwait(false);
        return new BackupImportResult(
            request.Strategy,
            plan.AppliedCounts,
            plan.Replaced,
            plan.Renamed,
            missingCredentials,
            storeResult.PreImportBackupPath);
    }

    public async Task<BackupInspection> InspectAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();

        // Compatibility and archive integrity are deliberately checked before the local data source is read.
        // No writer or unit of work is reachable from this service, so inspection cannot mutate persistence.
        var archive = await _serializer.ReadAsync(path, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var local = await _dataSource.CaptureAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var conflicts = FindConflicts(archive, local);
        var mergeAdds = new BackupEntityCounts(
            archive.Connections.Count - CountConflicts(conflicts, BackupConflictKind.ConnectionIdentity),
            archive.Folders.Count - CountConflicts(conflicts, BackupConflictKind.FolderPath),
            archive.Tags.Count - CountConflicts(conflicts, BackupConflictKind.TagName),
            archive.ConnectionTags.Count,
            archive.Settings.Count,
            archive.HostKeys.Count);
        var localCounts = new BackupEntityCounts(
            local.Connections.Count,
            local.Folders.Count,
            local.Tags.Count,
            local.ConnectionTags.Count,
            local.Settings.Count,
            local.HostKeys.Count);
        var none = new BackupEntityCounts(0, 0, 0, 0, 0, 0);
        return new BackupInspection(
            archive.Manifest.FormatVersion,
            archive.ActualCounts,
            archive.EncryptedCredentials is not null,
            conflicts,
            new BackupApplyPreview(
                MergeStrategy.Merge,
                mergeAdds,
                conflicts.Count,
                none,
                $"Merge adds non-conflicting records and resolves {conflicts.Count} detected conflicts without deleting unrelated local data."),
            new BackupApplyPreview(
                MergeStrategy.Replace,
                archive.ActualCounts,
                0,
                localCounts,
                "Replace removes all current backup-managed data, then loads the archive. Typed confirmation is required."));
    }

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

    private static List<BackupConflict> FindConflicts(BackupArchive archive, BackupDataSnapshot local)
    {
        var conflicts = new List<BackupConflict>();
        foreach (var imported in archive.Folders)
        {
            var existing = local.Folders.FirstOrDefault(folder =>
                folder.Id == imported.Id || string.Equals(folder.Path, imported.Path, StringComparison.OrdinalIgnoreCase));
            if (existing is not null && existing != imported)
            {
                conflicts.Add(new BackupConflict(
                    BackupConflictKind.FolderPath,
                    $"Folder '{imported.Path}' conflicts with the existing folder at that path.",
                    imported.Id,
                    existing.Id));
            }
        }

        foreach (var imported in archive.Tags)
        {
            var existing = local.Tags.FirstOrDefault(tag =>
                tag.Id == imported.Id || string.Equals(tag.Name, imported.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null && existing != imported)
            {
                conflicts.Add(new BackupConflict(
                    BackupConflictKind.TagName,
                    $"Tag '{imported.Name}' conflicts with the existing tag of the same name.",
                    imported.Id,
                    existing.Id));
            }
        }

        foreach (var imported in archive.Connections)
        {
            var existing = local.Connections.FirstOrDefault(connection =>
                connection.Id == imported.Id || HasSameConnectionIdentity(connection, imported));
            if (existing is not null && existing != imported)
            {
                var username = string.IsNullOrWhiteSpace(imported.Username) ? string.Empty : $"{imported.Username}@";
                conflicts.Add(new BackupConflict(
                    BackupConflictKind.ConnectionIdentity,
                    $"Connection '{imported.Name}' ({username}{imported.Host}:{imported.Port}, {imported.Protocol}) " +
                    $"conflicts with existing connection '{existing.Name}'.",
                    imported.Id,
                    existing.Id));
            }
        }

        return conflicts;
    }

    private static bool HasSameConnectionIdentity(BackupConnection left, BackupConnection right)
    {
        return left.Protocol == right.Protocol &&
            left.Port == right.Port &&
            string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(left.Username, right.Username, StringComparison.OrdinalIgnoreCase);
    }

    private static int CountConflicts(IEnumerable<BackupConflict> conflicts, BackupConflictKind kind)
    {
        return conflicts.Count(conflict => conflict.Kind == kind);
    }

    private static BackupConnection ClearCredential(BackupConnection connection)
    {
        return connection with
        {
            Credential = new BackupCredentialReference(CredentialKind.None, string.Empty, string.Empty, null),
        };
    }
}
