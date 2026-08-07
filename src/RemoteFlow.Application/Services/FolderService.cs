using RemoteFlow.Application.Abstractions;
using RemoteFlow.Domain.Abstractions;
using RemoteFlow.Domain.Common;
using RemoteFlow.Domain.Entities;

namespace RemoteFlow.Application.Services;

public enum FolderDeleteMode
{
    Reparent = 0,
    DeleteSubtree = 1,
    DeleteSubtreeAndConnections = 2,
}

public interface IFolderService
{
    Task<Result<Folder>> CreateAsync(
        string name,
        Guid? parentId = null,
        CancellationToken cancellationToken = default);

    Task<Result<Folder>> RenameAsync(Guid id, string name, CancellationToken cancellationToken = default);

    Task<Result<Folder>> MoveAsync(Guid id, Guid? parentId, CancellationToken cancellationToken = default);

    Task<Result<Folder>> DeleteAsync(
        Guid id,
        FolderDeleteMode mode = FolderDeleteMode.Reparent,
        CancellationToken cancellationToken = default);
}

public sealed class FolderService(
    IFolderRepository folders,
    IConnectionRepository connections,
    IConnectionService connectionService,
    IUnitOfWork unitOfWork,
    IGuidProvider guidProvider,
    IClock clock) : IFolderService
{
    public const int MaximumDepth = 16;

    public Task<Result<Folder>> CreateAsync(
        string name,
        Guid? parentId = null,
        CancellationToken cancellationToken = default)
    {
        return unitOfWork.ExecuteAsync(async token =>
        {
            var allFolders = await folders.ListAsync(token).ConfigureAwait(false);
            var parent = parentId is null
                ? null
                : allFolders.SingleOrDefault(folder => folder.Id == parentId.Value);
            if (parentId is not null && parent is null)
            {
                return MissingFolder(parentId.Value);
            }

            if (parent is not null && parent.Depth + 1 > MaximumDepth)
            {
                return DepthExceeded();
            }

            var created = Folder.Create(guidProvider, name, parent, allFolders, clock.UtcNow);
            if (created.IsFailure)
            {
                return created;
            }

            var consistency = CheckConsistency([.. allFolders, created.Value]);
            if (consistency is not null)
            {
                return Result<Folder>.Failure(consistency);
            }

            await folders.AddAsync(created.Value, token).ConfigureAwait(false);
            return created;
        }, cancellationToken);
    }

    public Task<Result<Folder>> RenameAsync(
        Guid id,
        string name,
        CancellationToken cancellationToken = default)
    {
        return unitOfWork.ExecuteAsync(async token =>
        {
            var allFolders = await folders.ListAsync(token).ConfigureAwait(false);
            var folder = allFolders.SingleOrDefault(candidate => candidate.Id == id);
            if (folder is null)
            {
                return MissingFolder(id);
            }

            var changedFolders = GetSubtree(allFolders, folder.Path);
            var renamed = folder.Rename(name, allFolders, guidProvider, clock.UtcNow);
            if (renamed.IsFailure)
            {
                return renamed;
            }

            var consistency = CheckConsistency(allFolders);
            if (consistency is not null)
            {
                return Result<Folder>.Failure(consistency);
            }

            foreach (var changed in changedFolders)
            {
                await folders.UpdateAsync(changed, token).ConfigureAwait(false);
            }

            return renamed;
        }, cancellationToken);
    }

    public Task<Result<Folder>> MoveAsync(
        Guid id,
        Guid? parentId,
        CancellationToken cancellationToken = default)
    {
        return unitOfWork.ExecuteAsync(async token =>
        {
            var allFolders = await folders.ListAsync(token).ConfigureAwait(false);
            var folder = allFolders.SingleOrDefault(candidate => candidate.Id == id);
            if (folder is null)
            {
                return MissingFolder(id);
            }

            var parent = parentId is null
                ? null
                : allFolders.SingleOrDefault(candidate => candidate.Id == parentId.Value);
            if (parentId is not null && parent is null)
            {
                return MissingFolder(parentId.Value);
            }

            var subtree = GetSubtree(allFolders, folder.Path);
            var relativeMaximumDepth = subtree.Max(candidate => candidate.Depth - folder.Depth);
            var newDepth = parent is null ? 0 : parent.Depth + 1;
            if (newDepth + relativeMaximumDepth > MaximumDepth)
            {
                return DepthExceeded();
            }

            var changedFolders = GetSubtree(allFolders, folder.Path);
            var moved = folder.MoveTo(parent, allFolders, guidProvider, clock.UtcNow);
            if (moved.IsFailure)
            {
                return moved;
            }

            var consistency = CheckConsistency(allFolders);
            if (consistency is not null)
            {
                return Result<Folder>.Failure(consistency);
            }

            foreach (var changed in changedFolders)
            {
                await folders.UpdateAsync(changed, token).ConfigureAwait(false);
            }

            return moved;
        }, cancellationToken);
    }

    public Task<Result<Folder>> DeleteAsync(
        Guid id,
        FolderDeleteMode mode = FolderDeleteMode.Reparent,
        CancellationToken cancellationToken = default)
    {
        return !Enum.IsDefined(mode)
            ? Task.FromResult(Result<Folder>.Failure(RemoteFlowError.Validation(
                "folder.delete_mode",
                "Choose a supported folder delete option.")))
            : unitOfWork.ExecuteAsync(async token =>
        {
            var allFolders = await folders.ListAsync(token).ConfigureAwait(false);
            var folder = allFolders.SingleOrDefault(candidate => candidate.Id == id);
            if (folder is null)
            {
                return MissingFolder(id);
            }

            var parent = folder.ParentId is null
                ? null
                : allFolders.Single(candidate => candidate.Id == folder.ParentId.Value);
            var subtree = GetSubtree(allFolders, folder.Path);
            var subtreeIds = subtree.Select(candidate => candidate.Id).ToHashSet();
            var allConnections = await connections.ListAsync(token).ConfigureAwait(false);

            if (mode == FolderDeleteMode.Reparent)
            {
                var directChildren = allFolders
                    .Where(candidate => candidate.ParentId == folder.Id)
                    .OrderBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var destinationNames = allFolders
                    .Where(candidate => candidate.ParentId == parent?.Id && candidate.Id != folder.Id)
                    .Select(candidate => candidate.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (directChildren.Any(child => destinationNames.Contains(child.Name)))
                {
                    return Result<Folder>.Failure(RemoteFlowError.Validation(
                        "folder.name_collision",
                        "A sibling folder already has this name."));
                }

                foreach (var child in directChildren)
                {
                    var moved = child.MoveTo(parent, allFolders, guidProvider, clock.UtcNow);
                    if (moved.IsFailure)
                    {
                        return Result<Folder>.Failure(moved.Error);
                    }

                    foreach (var changed in GetSubtree(allFolders, child.Path))
                    {
                        await folders.UpdateAsync(changed, token).ConfigureAwait(false);
                    }
                }

                foreach (var connection in allConnections.Where(connection => connection.FolderId == folder.Id))
                {
                    _ = connection.SetFolder(parent?.Id, guidProvider, clock.UtcNow);
                    await connections.UpdateAsync(connection, token).ConfigureAwait(false);
                }

                await folders.DeleteAsync(folder.Id, token).ConfigureAwait(false);
            }
            else
            {
                foreach (var connection in allConnections.Where(connection =>
                             connection.FolderId is { } folderId && subtreeIds.Contains(folderId)))
                {
                    if (mode == FolderDeleteMode.DeleteSubtreeAndConnections)
                    {
                        var deleted = await connectionService.DeleteAsync(connection.Id, token).ConfigureAwait(false);
                        if (deleted.IsFailure)
                        {
                            return Result<Folder>.Failure(deleted.Error);
                        }
                    }
                    else
                    {
                        _ = connection.SetFolder(parent?.Id, guidProvider, clock.UtcNow);
                        await connections.UpdateAsync(connection, token).ConfigureAwait(false);
                    }
                }

                foreach (var deletedFolder in subtree.OrderByDescending(candidate => candidate.Depth))
                {
                    await folders.DeleteAsync(deletedFolder.Id, token).ConfigureAwait(false);
                }
            }

            var remaining = mode == FolderDeleteMode.Reparent
                ? allFolders.Where(candidate => candidate.Id != folder.Id).ToArray()
                : [.. allFolders.Where(candidate => !subtreeIds.Contains(candidate.Id))];
            var consistency = CheckConsistency(remaining);
            return consistency is null
                ? Result<Folder>.Success(folder)
                : Result<Folder>.Failure(consistency);
        }, cancellationToken);
    }

    private static IReadOnlyList<Folder> GetSubtree(IEnumerable<Folder> allFolders, string rootPath)
    {
        var prefix = $"{rootPath}/";
        return [.. allFolders.Where(folder =>
            string.Equals(folder.Path, rootPath, StringComparison.OrdinalIgnoreCase) ||
            folder.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))];
    }

    private static RemoteFlowError? CheckConsistency(IReadOnlyCollection<Folder> allFolders)
    {
        foreach (var folder in allFolders)
        {
            var parent = folder.ParentId is null
                ? null
                : allFolders.SingleOrDefault(candidate => candidate.Id == folder.ParentId.Value);
            if (folder.ParentId is not null && parent is null)
            {
                return InconsistentTree();
            }

            var expectedPath = parent is null ? $"/{folder.Name}" : $"{parent.Path}/{folder.Name}";
            var expectedDepth = parent is null ? 0 : parent.Depth + 1;
            if (!string.Equals(folder.Path, expectedPath, StringComparison.Ordinal) || folder.Depth != expectedDepth)
            {
                return InconsistentTree();
            }
        }

        return null;
    }

    private static Result<Folder> MissingFolder(Guid id)
    {
        return Result<Folder>.Failure(RemoteFlowError.NotFound(
            "folder.not_found",
            $"Folder '{id}' was not found."));
    }

    private static Result<Folder> DepthExceeded()
    {
        return Result<Folder>.Failure(RemoteFlowError.Validation(
            "folder.depth_limit",
            $"Folders can be nested at most {MaximumDepth} levels deep."));
    }

    private static RemoteFlowError InconsistentTree()
    {
        return RemoteFlowError.Validation(
            "folder.inconsistent_tree",
            "The folder tree is inconsistent and the operation could not be completed.");
    }
}
