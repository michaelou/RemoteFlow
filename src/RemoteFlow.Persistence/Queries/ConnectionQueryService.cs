using Microsoft.EntityFrameworkCore;
using RemoteFlow.Application.Queries;
using RemoteFlow.Domain.Entities;

namespace RemoteFlow.Persistence.Queries;

public sealed class ConnectionQueryService(IDbContextFactory<RemoteFlowDbContext> contextFactory)
    : IConnectionQueryService
{
    private const string _likeEscape = "\\";

    public async Task<IReadOnlyList<ConnectionListItem>> QueryAsync(
        ConnectionFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ValidateFilter(filter);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var query = await ApplyFilterAsync(context, filter, cancellationToken).ConfigureAwait(false);
        var projected = Project(context, query);
        var ordered = ApplyOrdering(projected, filter.SortBy, filter.Descending);
        var rows = await ordered.ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return await MaterializeAsync(context, rows, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ConnectionListItem>> SearchPaletteAsync(
        string text,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        var normalizedText = text.Trim();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var pattern = BuildFuzzyLikePattern(normalizedText);
        var query = context.Connections.AsNoTracking().Where(connection =>
            EF.Functions.Like(connection.Name, pattern, _likeEscape) ||
            EF.Functions.Like(connection.Host, pattern, _likeEscape) ||
            (connection.Username != null && EF.Functions.Like(connection.Username, pattern, _likeEscape)) ||
            (connection.Notes != null && EF.Functions.Like(connection.Notes, pattern, _likeEscape)) ||
            connection.Tags.Any(join => context.Tags.Any(tag =>
                tag.Id == join.TagId && EF.Functions.Like(tag.Name, pattern, _likeEscape))));
        var rows = await Project(context, query).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var items = await MaterializeAsync(context, rows, cancellationToken).ConfigureAwait(false);
        return [.. items
            .OrderByDescending(item => GetMatchTier(item, normalizedText))
            .ThenByDescending(item => item.LastOpenedUtc)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)];
    }

    private static async Task<IQueryable<Connection>> ApplyFilterAsync(
        RemoteFlowDbContext context,
        ConnectionFilter filter,
        CancellationToken cancellationToken)
    {
        var query = context.Connections.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(filter.Text))
        {
            var pattern = $"%{EscapeLike(filter.Text.Trim())}%";
            query = query.Where(connection =>
                EF.Functions.Like(connection.Name, pattern, _likeEscape) ||
                EF.Functions.Like(connection.Host, pattern, _likeEscape) ||
                (connection.Username != null && EF.Functions.Like(connection.Username, pattern, _likeEscape)) ||
                (connection.Notes != null && EF.Functions.Like(connection.Notes, pattern, _likeEscape)) ||
                connection.Tags.Any(join => context.Tags.Any(tag =>
                    tag.Id == join.TagId && EF.Functions.Like(tag.Name, pattern, _likeEscape))));
        }

        if (filter.Protocols.Count > 0)
        {
            query = query.Where(connection => filter.Protocols.Contains(connection.Protocol));
        }

        if (filter.Environments.Count > 0)
        {
            query = query.Where(connection => filter.Environments.Contains(connection.Environment));
        }

        if (filter.Tags.Count > 0)
        {
            var tagIds = filter.Tags.Distinct().ToArray();
            query = filter.TagMatch == TagMatch.And
                ? query.Where(connection => connection.Tags.Count(join => tagIds.Contains(join.TagId)) == tagIds.Length)
                : query.Where(connection => connection.Tags.Any(join => tagIds.Contains(join.TagId)));
        }

        if (filter.FolderId is { } folderId)
        {
            if (filter.IncludeDescendants)
            {
                var folderPath = await context.Folders.AsNoTracking()
                    .Where(folder => folder.Id == folderId)
                    .Select(folder => folder.Path)
                    .SingleOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (folderPath is null)
                {
                    return query.Where(_ => false);
                }

                var pathPattern = $"{EscapeLike(folderPath)}/%";
                var folderIds = await context.Folders.AsNoTracking()
                    .Where(folder => folder.Id == folderId || EF.Functions.Like(folder.Path, pathPattern, _likeEscape))
                    .Select(folder => folder.Id)
                    .ToArrayAsync(cancellationToken)
                    .ConfigureAwait(false);
                query = query.Where(connection =>
                    connection.FolderId != null && folderIds.Contains(connection.FolderId.Value));
            }
            else
            {
                query = query.Where(connection => connection.FolderId == folderId);
            }
        }

        if (filter.FavoritesOnly)
        {
            query = query.Where(connection => connection.IsFavorite);
        }

        return query;
    }

    private static IQueryable<ConnectionProjectionRow> Project(
        RemoteFlowDbContext context,
        IQueryable<Connection> connections)
    {
#pragma warning disable IDE0031 // Null propagation is not supported in expression trees.
        return
            from connection in connections
            join folder in context.Folders.AsNoTracking()
                on connection.FolderId equals (Guid?)folder.Id into folderGroup
            from folder in folderGroup.DefaultIfEmpty()
            join recent in context.RecentConnections.AsNoTracking()
                on connection.Id equals recent.ConnectionId into recentGroup
            from recent in recentGroup.DefaultIfEmpty()
            select new ConnectionProjectionRow
            {
                Id = connection.Id,
                Name = connection.Name,
                Host = connection.Host,
                Port = connection.Port,
                Protocol = connection.Protocol,
                Environment = connection.Environment,
                IsFavorite = connection.IsFavorite,
                FolderId = connection.FolderId,
                FolderPath = folder == null ? null : folder.Path,
                ColorOverrideHex = connection.ColorOverrideHex,
                LastOpenedUtc = recent == null ? null : recent.LastOpenedUtc,
                SortOrder = connection.SortOrder,
            };
#pragma warning restore IDE0031
    }

    private static IOrderedQueryable<ConnectionProjectionRow> ApplyOrdering(
        IQueryable<ConnectionProjectionRow> query,
        ConnectionSortBy sortBy,
        bool descending)
    {
        return (sortBy, descending) switch
        {
            (ConnectionSortBy.Name, false) => query.OrderBy(item => item.Name).ThenBy(item => item.Host),
            (ConnectionSortBy.Name, true) => query.OrderByDescending(item => item.Name).ThenBy(item => item.Host),
            (ConnectionSortBy.Host, false) => query.OrderBy(item => item.Host).ThenBy(item => item.Name),
            (ConnectionSortBy.Host, true) => query.OrderByDescending(item => item.Host).ThenBy(item => item.Name),
            (ConnectionSortBy.LastOpened, false) => query.OrderBy(item => item.LastOpenedUtc).ThenBy(item => item.Name),
            (ConnectionSortBy.LastOpened, true) => query.OrderByDescending(item => item.LastOpenedUtc).ThenBy(item => item.Name),
            (ConnectionSortBy.SortOrder, false) => query.OrderBy(item => item.SortOrder == null)
                .ThenBy(item => item.SortOrder)
                .ThenBy(item => item.Name),
            (ConnectionSortBy.SortOrder, true) => query.OrderByDescending(item => item.SortOrder)
                .ThenBy(item => item.Name),
            _ => throw new ArgumentOutOfRangeException(nameof(sortBy)),
        };
    }

    private static async Task<IReadOnlyList<ConnectionListItem>> MaterializeAsync(
        RemoteFlowDbContext context,
        IReadOnlyCollection<ConnectionProjectionRow> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var connectionIds = rows.Select(row => row.Id).ToArray();
        var tagRows = await (
                from link in context.ConnectionTags.AsNoTracking()
                join tag in context.Tags.AsNoTracking() on link.TagId equals tag.Id
                where connectionIds.Contains(link.ConnectionId)
                orderby tag.Name
                select new { link.ConnectionId, tag.Name })
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        var tagsByConnection = tagRows
            .GroupBy(row => row.ConnectionId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<string>)[.. group.Select(row => row.Name)]);

        return [.. rows.Select(row => new ConnectionListItem(
            row.Id,
            row.Name,
            row.Host,
            row.Port,
            row.Protocol,
            row.Environment,
            row.IsFavorite,
            row.FolderId,
            row.FolderPath,
            row.ColorOverrideHex,
            row.SortOrder,
            tagsByConnection.GetValueOrDefault(row.Id, []),
            row.LastOpenedUtc))];
    }

    private static int GetMatchTier(ConnectionListItem item, string text)
    {
        var values = new[] { item.Name, item.Host }.Concat(item.TagNames);
        return values.Any(value => value.StartsWith(text, StringComparison.OrdinalIgnoreCase))
            ? 3
            : values.Any(value => value.Contains(text, StringComparison.OrdinalIgnoreCase))
            ? 2
            : values.Any(value => IsFuzzyMatch(value, text)) ? 1 : 0;
    }

    private static bool IsFuzzyMatch(string value, string text)
    {
        var textIndex = 0;
        foreach (var character in value)
        {
            if (textIndex < text.Length &&
                char.ToUpperInvariant(character) == char.ToUpperInvariant(text[textIndex]))
            {
                textIndex++;
            }
        }

        return textIndex == text.Length;
    }

    private static string BuildFuzzyLikePattern(string text)
    {
        return $"%{string.Join('%', text.Select(character => EscapeLike(character.ToString())))}%";
    }

    private static string EscapeLike(string value)
    {
        return value.Replace(_likeEscape, "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
    }

    private static void ValidateFilter(ConnectionFilter filter)
    {
        if (!Enum.IsDefined(filter.TagMatch))
        {
            throw new ArgumentOutOfRangeException(nameof(filter), "The tag match mode is invalid.");
        }

        if (!Enum.IsDefined(filter.SortBy))
        {
            throw new ArgumentOutOfRangeException(nameof(filter), "The connection sort is invalid.");
        }
    }

    private sealed class ConnectionProjectionRow
    {
        public Guid Id { get; init; }

        public required string Name { get; init; }

        public required string Host { get; init; }

        public int Port { get; init; }

        public Domain.Enums.ProtocolType Protocol { get; init; }

        public Domain.Enums.EnvironmentKind Environment { get; init; }

        public bool IsFavorite { get; init; }

        public Guid? FolderId { get; init; }

        public string? FolderPath { get; init; }

        public string? ColorOverrideHex { get; init; }

        public DateTimeOffset? LastOpenedUtc { get; init; }

        public int? SortOrder { get; init; }
    }
}
