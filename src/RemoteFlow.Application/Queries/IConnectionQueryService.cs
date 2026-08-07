using RemoteFlow.Domain.Enums;

namespace RemoteFlow.Application.Queries;

public enum TagMatch
{
    And = 0,
    Or = 1,
}

public enum ConnectionSortBy
{
    Name = 0,
    Host = 1,
    LastOpened = 2,
    SortOrder = 3,
}

public sealed record ConnectionFilter
{
    public string? Text { get; init; }

    public IReadOnlyCollection<ProtocolType> Protocols { get; init; } = [];

    public IReadOnlyCollection<EnvironmentKind> Environments { get; init; } = [];

    public IReadOnlyCollection<Guid> Tags { get; init; } = [];

    public TagMatch TagMatch { get; init; } = TagMatch.Or;

    public Guid? FolderId { get; init; }

    public bool IncludeDescendants { get; init; }

    public bool FavoritesOnly { get; init; }

    public ConnectionSortBy SortBy { get; init; } = ConnectionSortBy.Name;

    public bool Descending { get; init; }
}

public sealed record ConnectionListItem(
    Guid Id,
    string Name,
    string Host,
    int Port,
    ProtocolType Protocol,
    EnvironmentKind Environment,
    bool IsFavorite,
    Guid? FolderId,
    string? FolderPath,
    string? ColorOverrideHex,
    int? SortOrder,
    IReadOnlyList<string> TagNames,
    DateTimeOffset? LastOpenedUtc);

public interface IConnectionQueryService
{
    Task<IReadOnlyList<ConnectionListItem>> QueryAsync(
        ConnectionFilter filter,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConnectionListItem>> SearchPaletteAsync(
        string text,
        int limit = 20,
        CancellationToken cancellationToken = default);
}
