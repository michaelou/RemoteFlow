using Microsoft.EntityFrameworkCore;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Domain.Entities;

namespace RemoteFlow.Persistence.Repositories;

public sealed class RecentConnectionStore : RepositoryBase, IRecentConnectionStore
{
    public RecentConnectionStore(IDbContextFactory<RemoteFlowDbContext> contextFactory) : base(contextFactory) { }

    internal RecentConnectionStore(IDbContextFactory<RemoteFlowDbContext> contextFactory, DbContextScopeAccessor scopeAccessor)
        : base(contextFactory, scopeAccessor) { }

    public Task<RecentConnection?> GetAsync(Guid connectionId, CancellationToken cancellationToken = default)
    {
        return ReadAsync(context => context.RecentConnections.AsNoTracking()
            .SingleOrDefaultAsync(item => item.ConnectionId == connectionId, cancellationToken), cancellationToken);
    }

    public Task<IReadOnlyList<RecentConnection>> ListAsync(int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);

        return ReadAsync<IReadOnlyList<RecentConnection>>(async context =>
        {
            var recentConnections = await context.RecentConnections.AsNoTracking()
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
            return [.. recentConnections.OrderByDescending(item => item.LastOpenedUtc).Take(limit)];
        }, cancellationToken);
    }

    public Task RecordOpenedAsync(Guid connectionId, DateTimeOffset openedUtc, CancellationToken cancellationToken = default)
    {
        return WriteAsync(async context =>
        {
            var recent = await context.RecentConnections.FindAsync([connectionId], cancellationToken).ConfigureAwait(false);
            if (recent is null)
            {
                _ = context.RecentConnections.Add(RecentConnection.Create(connectionId, openedUtc).Value);
            }
            else
            {
                _ = recent.RecordOpened(openedUtc);
            }
        }, cancellationToken);
    }

    public Task RemoveAsync(Guid connectionId, CancellationToken cancellationToken = default)
    {
        return WriteAsync(async context =>
        {
            var recent = await context.RecentConnections.FindAsync([connectionId], cancellationToken).ConfigureAwait(false);
            if (recent is not null)
            {
                _ = context.RecentConnections.Remove(recent);
            }
        }, cancellationToken);
    }
}
