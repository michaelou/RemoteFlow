using RemoteFlow.Application.Abstractions;
using RemoteFlow.Domain.Abstractions;
using RemoteFlow.Domain.Common;
using RemoteFlow.Domain.Entities;

namespace RemoteFlow.Application.Services;

public sealed record TagUsage(Tag Tag, int ConnectionCount);

public interface ITagService
{
    Task<Result<Tag>> CreateAsync(string name, string? colorHex = null, CancellationToken cancellationToken = default);

    Task<Result<Tag>> RenameAsync(Guid id, string name, CancellationToken cancellationToken = default);

    Task<Result<Tag>> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Result<Tag>> MergeAsync(Guid sourceId, Guid targetId, CancellationToken cancellationToken = default);

    Task<Result<Connection>> AssignAsync(Guid connectionId, Guid tagId, CancellationToken cancellationToken = default);

    Task<Result<Connection>> UnassignAsync(Guid connectionId, Guid tagId, CancellationToken cancellationToken = default);

    Task<int> CleanupOrphansAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TagUsage>> GetUsageCountsAsync(CancellationToken cancellationToken = default);
}

public sealed class TagService(
    ITagRepository tags,
    IConnectionRepository connections,
    IUnitOfWork unitOfWork,
    IGuidProvider guidProvider,
    IClock clock,
    IWorkspaceChangeNotifier? changeNotifier = null) : ITagService
{
    public Task<Result<Tag>> CreateAsync(
        string name,
        string? colorHex = null,
        CancellationToken cancellationToken = default)
    {
        var added = false;
        return NotifyAfterAsync(
            unitOfWork.ExecuteAsync(async token =>
            {
                var created = Tag.Create(guidProvider, name, colorHex, clock.UtcNow);
                if (created.IsFailure)
                {
                    return created;
                }

                var existing = await tags.GetByNameAsync(created.Value.Name, token).ConfigureAwait(false);
                if (existing is not null)
                {
                    // Reusing a tag that already exists writes nothing, so there is nothing to announce.
                    return Result<Tag>.Success(existing);
                }

                await tags.AddAsync(created.Value, token).ConfigureAwait(false);
                added = true;
                return created;
            }, cancellationToken),
            WorkspaceChangeKind.Created,
            () => added);
    }

    public Task<Result<Tag>> RenameAsync(Guid id, string name, CancellationToken cancellationToken = default)
    {
        return NotifyAfterAsync(unitOfWork.ExecuteAsync(async token =>
        {
            var tag = await tags.GetByIdAsync(id, token).ConfigureAwait(false);
            if (tag is null)
            {
                return MissingTag(id);
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                return tag.Rename(name);
            }

            var collision = await tags.GetByNameAsync(name.Trim(), token).ConfigureAwait(false);
            if (collision is not null && collision.Id != id)
            {
                return Result<Tag>.Failure(RemoteFlowError.Validation(
                    "tag.name_collision",
                    "A tag with this name already exists."));
            }

            var renamed = tag.Rename(name);
            if (renamed.IsSuccess)
            {
                await tags.UpdateAsync(tag, token).ConfigureAwait(false);
            }

            return renamed;
        }, cancellationToken), WorkspaceChangeKind.Updated);
    }

    public Task<Result<Tag>> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return NotifyAfterAsync(unitOfWork.ExecuteAsync(async token =>
        {
            var tag = await tags.GetByIdAsync(id, token).ConfigureAwait(false);
            if (tag is null)
            {
                return MissingTag(id);
            }

            await tags.DeleteAsync(id, token).ConfigureAwait(false);
            return Result<Tag>.Success(tag);
        }, cancellationToken), WorkspaceChangeKind.Deleted);
    }

    public Task<Result<Tag>> MergeAsync(
        Guid sourceId,
        Guid targetId,
        CancellationToken cancellationToken = default)
    {
        return sourceId == targetId
            ? Task.FromResult(Result<Tag>.Failure(RemoteFlowError.Validation(
                "tag.merge_same",
                "Choose two different tags to merge.")))
            : NotifyAfterAsync(unitOfWork.ExecuteAsync(async token =>
        {
            var source = await tags.GetByIdAsync(sourceId, token).ConfigureAwait(false);
            if (source is null)
            {
                return MissingTag(sourceId);
            }

            var target = await tags.GetByIdAsync(targetId, token).ConfigureAwait(false);
            if (target is null)
            {
                return MissingTag(targetId);
            }

            var allConnections = await connections.ListAsync(token).ConfigureAwait(false);
            foreach (var connection in allConnections.Where(connection =>
                         connection.Tags.Any(join => join.TagId == sourceId)))
            {
                if (!connection.Tags.Any(join => join.TagId == targetId))
                {
                    _ = await connections.AddTagAsync(connection.Id, targetId, token).ConfigureAwait(false);
                }

                _ = await connections.RemoveTagAsync(connection.Id, sourceId, token).ConfigureAwait(false);
            }

            await tags.DeleteAsync(sourceId, token).ConfigureAwait(false);
            return Result<Tag>.Success(target);
        }, cancellationToken), WorkspaceChangeKind.Deleted);
    }

    public Task<Result<Connection>> AssignAsync(
        Guid connectionId,
        Guid tagId,
        CancellationToken cancellationToken = default)
    {
        return ChangeAssignmentAsync(connectionId, tagId, assign: true, cancellationToken);
    }

    public Task<Result<Connection>> UnassignAsync(
        Guid connectionId,
        Guid tagId,
        CancellationToken cancellationToken = default)
    {
        return ChangeAssignmentAsync(connectionId, tagId, assign: false, cancellationToken);
    }

    public async Task<int> CleanupOrphansAsync(CancellationToken cancellationToken = default)
    {
        var deletedCount = await unitOfWork.ExecuteAsync(async token =>
        {
            var allTags = await tags.ListAsync(token).ConfigureAwait(false);
            var deleted = 0;
            foreach (var tag in allTags)
            {
                if (await tags.GetUsageCountAsync(tag.Id, token).ConfigureAwait(false) == 0)
                {
                    await tags.DeleteAsync(tag.Id, token).ConfigureAwait(false);
                    deleted++;
                }
            }

            return deleted;
        }, cancellationToken).ConfigureAwait(false);
        if (deletedCount > 0)
        {
            // One signal for the sweep as a whole: no single ID describes which tags went.
            changeNotifier?.Notify(WorkspaceEntityKind.Tag, Guid.Empty, WorkspaceChangeKind.Deleted);
        }

        return deletedCount;
    }

    public async Task<IReadOnlyList<TagUsage>> GetUsageCountsAsync(
        CancellationToken cancellationToken = default)
    {
        var allTags = await tags.ListAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<TagUsage>(allTags.Count);
        foreach (var tag in allTags)
        {
            var count = await tags.GetUsageCountAsync(tag.Id, cancellationToken).ConfigureAwait(false);
            result.Add(new TagUsage(tag, count));
        }

        return result;
    }

    private Task<Result<Connection>> ChangeAssignmentAsync(
        Guid connectionId,
        Guid tagId,
        bool assign,
        CancellationToken cancellationToken)
    {
        return NotifyAssignmentAfterAsync(unitOfWork.ExecuteAsync(async token =>
        {
            var connection = await connections.GetByIdAsync(connectionId, token).ConfigureAwait(false);
            if (connection is null)
            {
                return Result<Connection>.Failure(RemoteFlowError.NotFound(
                    "connection.not_found",
                    $"Connection '{connectionId}' was not found."));
            }

            if (await tags.GetByIdAsync(tagId, token).ConfigureAwait(false) is null)
            {
                return Result<Connection>.Failure(MissingTag(tagId).Error);
            }

            _ = assign
                ? await connections.AddTagAsync(connectionId, tagId, token).ConfigureAwait(false)
                : await connections.RemoveTagAsync(connectionId, tagId, token).ConfigureAwait(false);

            return Result<Connection>.Success(connection);
        }, cancellationToken), tagId);
    }

    private static Result<Tag> MissingTag(Guid id)
    {
        return Result<Tag>.Failure(RemoteFlowError.NotFound("tag.not_found", $"Tag '{id}' was not found."));
    }

    /// <summary>Signals only after <paramref name="operation"/> has committed, and only when it succeeded.
    /// Raising from inside the unit-of-work lambda would announce writes a later failure rolls back, and
    /// would fire while the SQLite write transaction is still open.</summary>
    private async Task<Result<Tag>> NotifyAfterAsync(
        Task<Result<Tag>> operation,
        WorkspaceChangeKind kind,
        Func<bool>? wroteSomething = null)
    {
        var result = await operation.ConfigureAwait(false);
        if (result.IsSuccess && (wroteSomething is null || wroteSomething()))
        {
            changeNotifier?.Notify(WorkspaceEntityKind.Tag, result.Value.Id, kind);
        }

        return result;
    }

    /// <summary>Assigning and unassigning return the connection, but what changed is the tag link — which
    /// is its own entry in a backup archive, so it has to be announced.</summary>
    private async Task<Result<Connection>> NotifyAssignmentAfterAsync(
        Task<Result<Connection>> operation,
        Guid tagId)
    {
        var result = await operation.ConfigureAwait(false);
        if (result.IsSuccess)
        {
            changeNotifier?.Notify(WorkspaceEntityKind.Tag, tagId, WorkspaceChangeKind.Updated);
        }

        return result;
    }
}
