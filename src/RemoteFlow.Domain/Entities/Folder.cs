using RemoteFlow.Domain.Abstractions;
using RemoteFlow.Domain.Common;

namespace RemoteFlow.Domain.Entities;

public sealed class Folder
{
    private Folder()
    {
        Name = string.Empty;
        Path = string.Empty;
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; }

    public Guid? ParentId { get; private set; }

    public string Path { get; private set; }

    public int Depth { get; private set; }

    public int SortOrder { get; private set; }

    public bool IsExpanded { get; private set; }

    public Guid ConcurrencyStamp { get; private set; }

    public DateTimeOffset CreatedUtc { get; private set; }

    public DateTimeOffset ModifiedUtc { get; private set; }

    public static Result<Folder> Create(
        IGuidProvider guidProvider,
        string? name,
        Folder? parent = null,
        IEnumerable<Folder>? existingFolders = null,
        DateTimeOffset? createdUtc = null)
    {
        var normalizedName = NormalizeName(name, out var error);
        if (error is not null)
        {
            return Result<Folder>.Failure(error);
        }

        if (HasSiblingCollision(normalizedName!, parent?.Id, Guid.Empty, existingFolders ?? []))
        {
            return Result<Folder>.Failure(NameCollision());
        }

        var now = DomainValidation.Utc(createdUtc ?? DateTimeOffset.UtcNow);
        return Result<Folder>.Success(new Folder
        {
            Id = DomainValidation.NewRequiredGuid(guidProvider),
            Name = normalizedName!,
            ParentId = parent?.Id,
            Path = BuildPath(parent, normalizedName!),
            Depth = parent is null ? 0 : parent.Depth + 1,
            ConcurrencyStamp = DomainValidation.NewRequiredGuid(guidProvider),
            CreatedUtc = now,
            ModifiedUtc = now,
        });
    }

    public Result<Folder> Rename(
        string? name,
        IEnumerable<Folder> allFolders,
        IGuidProvider guidProvider,
        DateTimeOffset? modifiedUtc = null)
    {
        ArgumentNullException.ThrowIfNull(allFolders);
        var folders = allFolders as IReadOnlyCollection<Folder> ?? [.. allFolders];
        var normalizedName = NormalizeName(name, out var error);
        if (error is not null)
        {
            return Result<Folder>.Failure(error);
        }

        if (HasSiblingCollision(normalizedName!, ParentId, Id, folders))
        {
            return Result<Folder>.Failure(NameCollision());
        }

        var oldPath = Path;
        Name = normalizedName!;
        Path = ParentId is null
            ? $"/{normalizedName}"
            : $"{GetRequiredParent(folders).Path}/{normalizedName}";
        var changedUtc = modifiedUtc ?? DateTimeOffset.UtcNow;
        RewriteDescendantPaths(folders, oldPath, Path, 0, guidProvider, changedUtc);
        Touch(guidProvider, changedUtc);
        return Result<Folder>.Success(this);
    }

    public Result<Folder> MoveTo(
        Folder? newParent,
        IEnumerable<Folder> allFolders,
        IGuidProvider guidProvider,
        DateTimeOffset? modifiedUtc = null)
    {
        ArgumentNullException.ThrowIfNull(allFolders);
        var folders = allFolders as IReadOnlyCollection<Folder> ?? [.. allFolders];
        if (newParent is not null &&
            (newParent.Id == Id || newParent.Path.StartsWith($"{Path}/", StringComparison.OrdinalIgnoreCase)))
        {
            return Result<Folder>.Failure(RemoteFlowError.Validation(
                "folder.cycle",
                "A folder cannot be moved into itself or one of its descendants."));
        }

        if (HasSiblingCollision(Name, newParent?.Id, Id, folders))
        {
            return Result<Folder>.Failure(NameCollision());
        }

        var oldPath = Path;
        var oldDepth = Depth;
        ParentId = newParent?.Id;
        Path = BuildPath(newParent, Name);
        Depth = newParent is null ? 0 : newParent.Depth + 1;
        var changedUtc = modifiedUtc ?? DateTimeOffset.UtcNow;
        RewriteDescendantPaths(folders, oldPath, Path, Depth - oldDepth, guidProvider, changedUtc);
        Touch(guidProvider, changedUtc);
        return Result<Folder>.Success(this);
    }

    public Folder SetPresentation(
        int sortOrder,
        bool isExpanded,
        IGuidProvider guidProvider,
        DateTimeOffset? modifiedUtc = null)
    {
        SortOrder = sortOrder;
        IsExpanded = isExpanded;
        Touch(guidProvider, modifiedUtc);
        return this;
    }

    private static string? NormalizeName(string? name, out RemoteFlowError? error)
    {
        var normalized = DomainValidation.Required(name, 100, "folder.name", out error);
        if (error is null && normalized!.Contains('/', StringComparison.Ordinal))
        {
            error = RemoteFlowError.Validation("folder.name", "Folder names cannot contain '/'.");
            return null;
        }

        return normalized;
    }

    private static bool HasSiblingCollision(
        string name,
        Guid? parentId,
        Guid ownId,
        IEnumerable<Folder> folders)
    {
        return folders.Any(folder =>
            folder.Id != ownId &&
            folder.ParentId == parentId &&
            string.Equals(folder.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildPath(Folder? parent, string name)
    {
        return parent is null ? $"/{name}" : $"{parent.Path}/{name}";
    }

    private static RemoteFlowError NameCollision()
    {
        return RemoteFlowError.Validation("folder.name_collision", "A sibling folder already has this name.");
    }

    private Folder GetRequiredParent(IEnumerable<Folder> folders)
    {
        return folders.FirstOrDefault(folder => folder.Id == ParentId)
            ?? throw new InvalidOperationException("The folder tree does not contain the current parent.");
    }

    private static void RewriteDescendantPaths(
        IEnumerable<Folder> folders,
        string oldParentPath,
        string newParentPath,
        int depthDelta,
        IGuidProvider guidProvider,
        DateTimeOffset modifiedUtc)
    {
        var prefix = $"{oldParentPath}/";
        foreach (var descendant in folders.Where(folder => folder.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            descendant.Path = newParentPath + descendant.Path[oldParentPath.Length..];
            descendant.Depth += depthDelta;
            descendant.Touch(guidProvider, modifiedUtc);
        }
    }

    private void Touch(IGuidProvider guidProvider, DateTimeOffset? modifiedUtc)
    {
        ConcurrencyStamp = DomainValidation.NewRequiredGuid(guidProvider);
        ModifiedUtc = DomainValidation.Utc(modifiedUtc ?? DateTimeOffset.UtcNow);
    }
}
