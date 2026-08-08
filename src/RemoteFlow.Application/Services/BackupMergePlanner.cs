using RemoteFlow.Application.Abstractions.Backup;
using RemoteFlow.Domain.Enums;

namespace RemoteFlow.Application.Services;

internal sealed record BackupMergePlan(
    BackupDataSnapshot Target,
    BackupEntityCounts AppliedCounts,
    int Replaced,
    int Renamed);

internal static class BackupMergePlanner
{
    public static BackupMergePlan Replace(BackupArchive archive)
    {
        return new BackupMergePlan(
            Snapshot(archive),
            archive.ActualCounts,
            Replaced: 0,
            Renamed: 0);
    }

    public static BackupMergePlan Merge(
        BackupArchive archive,
        BackupDataSnapshot local,
        MergeConflictPolicy policy)
    {
        var folders = local.Folders.ToList();
        var tags = local.Tags.ToList();
        var connections = local.Connections.ToList();
        var folderMap = new Dictionary<Guid, Guid>();
        var tagMap = new Dictionary<Guid, Guid>();
        var connectionMap = new Dictionary<Guid, Guid>();
        var replaced = 0;
        var renamed = 0;

        foreach (var imported in archive.Folders.OrderBy(folder => folder.Depth))
        {
            var existing = folders.FirstOrDefault(folder =>
                folder.Id == imported.Id || string.Equals(folder.Path, imported.Path, StringComparison.OrdinalIgnoreCase));
            var parentId = imported.ParentId is { } sourceParent && folderMap.TryGetValue(sourceParent, out var mappedParent)
                ? mappedParent
                : imported.ParentId;
            if (existing is null)
            {
                var added = RebuildFolder(imported, imported.Id, imported.Name, parentId, folders);
                folders.Add(added);
                folderMap[imported.Id] = added.Id;
                continue;
            }

            folderMap[imported.Id] = existing.Id;
            if (existing == imported || policy == MergeConflictPolicy.PreferLocal)
            {
                continue;
            }

            if (policy == MergeConflictPolicy.PreferImported)
            {
                var replacement = RebuildFolder(imported, existing.Id, imported.Name, parentId, folders);
                folders[folders.IndexOf(existing)] = replacement;
                replaced++;
            }
            else
            {
                var renamedName = UniqueFolderName($"{imported.Name} (imported)", parentId, folders);
                var id = folders.Any(folder => folder.Id == imported.Id) ? Guid.NewGuid() : imported.Id;
                var renamedFolder = RebuildFolder(imported, id, renamedName, parentId, folders);
                folders.Add(renamedFolder);
                folderMap[imported.Id] = renamedFolder.Id;
                renamed++;
            }
        }

        foreach (var imported in archive.Tags)
        {
            var existing = tags.FirstOrDefault(tag =>
                tag.Id == imported.Id || string.Equals(tag.Name, imported.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                tags.Add(imported);
                tagMap[imported.Id] = imported.Id;
                continue;
            }

            tagMap[imported.Id] = existing.Id;
            if (existing == imported || policy == MergeConflictPolicy.PreferLocal)
            {
                continue;
            }

            if (policy == MergeConflictPolicy.PreferImported)
            {
                tags[tags.IndexOf(existing)] = imported with { Id = existing.Id };
                replaced++;
            }
            else
            {
                var id = tags.Any(tag => tag.Id == imported.Id) ? Guid.NewGuid() : imported.Id;
                var renamedTag = imported with { Id = id, Name = UniqueTagName($"{imported.Name} (imported)", tags) };
                tags.Add(renamedTag);
                tagMap[imported.Id] = renamedTag.Id;
                renamed++;
            }
        }

        foreach (var importedValue in archive.Connections)
        {
            var imported = importedValue with
            {
                FolderId = importedValue.FolderId is { } sourceFolder && folderMap.TryGetValue(sourceFolder, out var targetFolder)
                    ? targetFolder
                    : importedValue.FolderId,
            };
            var existing = connections.FirstOrDefault(connection =>
                connection.Id == imported.Id || SameIdentity(connection, imported));
            if (existing is null)
            {
                connections.Add(imported);
                connectionMap[importedValue.Id] = imported.Id;
                continue;
            }

            connectionMap[importedValue.Id] = existing.Id;
            if (existing == imported || policy == MergeConflictPolicy.PreferLocal)
            {
                continue;
            }

            if (policy == MergeConflictPolicy.PreferImported)
            {
                connections[connections.IndexOf(existing)] = imported with { Id = existing.Id };
                replaced++;
            }
            else
            {
                var id = connections.Any(connection => connection.Id == imported.Id) ? Guid.NewGuid() : imported.Id;
                var renamedConnection = imported with { Id = id, Name = $"{imported.Name} (imported)" };
                connections.Add(renamedConnection);
                connectionMap[importedValue.Id] = renamedConnection.Id;
                renamed++;
            }
        }

        var links = local.ConnectionTags.ToHashSet();
        foreach (var imported in archive.ConnectionTags)
        {
            var connectionId = connectionMap.GetValueOrDefault(imported.ConnectionId, imported.ConnectionId);
            var tagId = tagMap.GetValueOrDefault(imported.TagId, imported.TagId);
            if (connections.Any(connection => connection.Id == connectionId) && tags.Any(tag => tag.Id == tagId))
            {
                _ = links.Add(new BackupConnectionTag(connectionId, tagId));
            }
        }

        var settings = MergeSettings(local.Settings, archive.Settings, policy);
        var hostKeys = MergeHostKeys(local.HostKeys, archive.HostKeys, policy, ref replaced);
        var target = new BackupDataSnapshot(
            connections,
            [.. folders.OrderBy(folder => folder.Depth).ThenBy(folder => folder.Path)],
            tags,
            [.. links],
            settings,
            hostKeys);
        return new BackupMergePlan(target, archive.ActualCounts, replaced, renamed);
    }

    private static BackupDataSnapshot Snapshot(BackupArchive archive)
    {
        return new BackupDataSnapshot(
            archive.Connections,
            archive.Folders,
            archive.Tags,
            archive.ConnectionTags,
            archive.Settings,
            archive.HostKeys);
    }

    private static BackupFolder RebuildFolder(
        BackupFolder source,
        Guid id,
        string name,
        Guid? parentId,
        IReadOnlyList<BackupFolder> folders)
    {
        var parent = parentId is null ? null : folders.FirstOrDefault(folder => folder.Id == parentId)
            ?? throw new BackupArchiveException($"Folder '{source.Path}' references a missing parent.");
        return source with
        {
            Id = id,
            Name = name,
            ParentId = parentId,
            Path = parent is null ? $"/{name}" : $"{parent.Path}/{name}",
            Depth = parent is null ? 0 : parent.Depth + 1,
        };
    }

    private static string UniqueFolderName(string candidate, Guid? parentId, IEnumerable<BackupFolder> folders)
    {
        var name = candidate;
        var suffix = 2;
        while (folders.Any(folder => folder.ParentId == parentId &&
                   string.Equals(folder.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            name = $"{candidate} {suffix++}";
        }

        return name;
    }

    private static string UniqueTagName(string candidate, IEnumerable<BackupTag> tags)
    {
        var name = candidate;
        var suffix = 2;
        while (tags.Any(tag => string.Equals(tag.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            name = $"{candidate} {suffix++}";
        }

        return name;
    }

    private static BackupSetting[] MergeSettings(
        IReadOnlyList<BackupSetting> local,
        IReadOnlyList<BackupSetting> imported,
        MergeConflictPolicy policy)
    {
        var result = local.ToDictionary(setting => setting.Key, StringComparer.Ordinal);
        foreach (var setting in imported)
        {
            if (!result.ContainsKey(setting.Key) || policy == MergeConflictPolicy.PreferImported)
            {
                result[setting.Key] = setting;
            }
        }

        return [.. result.Values.OrderBy(setting => setting.Key)];
    }

    private static List<BackupHostKey> MergeHostKeys(
        IReadOnlyList<BackupHostKey> local,
        IReadOnlyList<BackupHostKey> imported,
        MergeConflictPolicy policy,
        ref int replaced)
    {
        var result = local.ToList();
        foreach (var hostKey in imported)
        {
            var existing = result.FirstOrDefault(item => item.Id == hostKey.Id ||
                (string.Equals(item.Host, hostKey.Host, StringComparison.OrdinalIgnoreCase) &&
                 item.Port == hostKey.Port && item.KeyAlgorithm == hostKey.KeyAlgorithm));
            if (existing is null)
            {
                result.Add(hostKey);
            }
            else if (existing != hostKey && policy == MergeConflictPolicy.PreferImported)
            {
                result[result.IndexOf(existing)] = hostKey with { Id = existing.Id };
                replaced++;
            }
        }

        return result;
    }

    private static bool SameIdentity(BackupConnection left, BackupConnection right)
    {
        return left.Protocol == right.Protocol && left.Port == right.Port &&
            string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(left.Username, right.Username, StringComparison.OrdinalIgnoreCase);
    }
}
