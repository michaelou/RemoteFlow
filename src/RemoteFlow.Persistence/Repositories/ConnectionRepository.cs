using Microsoft.EntityFrameworkCore;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Domain.Entities;

namespace RemoteFlow.Persistence.Repositories;

public sealed class ConnectionRepository : RepositoryBase, IConnectionRepository
{
    public ConnectionRepository(IDbContextFactory<RemoteFlowDbContext> contextFactory)
        : base(contextFactory)
    {
    }

    internal ConnectionRepository(
        IDbContextFactory<RemoteFlowDbContext> contextFactory,
        DbContextScopeAccessor scopeAccessor)
        : base(contextFactory, scopeAccessor)
    {
    }

    public Task<Connection?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return ReadAsync(
            context => context.Connections
                .AsNoTracking()
                .Include(connection => connection.Tags)
                .SingleOrDefaultAsync(connection => connection.Id == id, cancellationToken),
            cancellationToken);
    }

    public Task<IReadOnlyList<Connection>> ListAsync(CancellationToken cancellationToken = default)
    {
        return ReadAsync<IReadOnlyList<Connection>>(
            async context => await context.Connections
                .AsNoTracking()
                .Include(connection => connection.Tags)
                .OrderBy(connection => connection.SortOrder)
                .ThenBy(connection => connection.Name)
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false),
            cancellationToken);
    }

    public Task AddAsync(Connection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return WriteAsync(
            context =>
            {
                _ = context.Connections.Add(connection);
                return Task.CompletedTask;
            },
            cancellationToken);
    }

    public Task UpdateAsync(Connection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return WriteAsync(
            context =>
            {
                _ = context.Connections.Update(connection);
                return Task.CompletedTask;
            },
            cancellationToken);
    }

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return WriteAsync(
            async context =>
            {
                var connection = await context.Connections.FindAsync([id], cancellationToken).ConfigureAwait(false);
                if (connection is not null)
                {
                    _ = context.Connections.Remove(connection);
                }
            },
            cancellationToken);
    }

    public Task<bool> AddTagAsync(Guid connectionId, Guid tagId, CancellationToken cancellationToken = default)
    {
        return WriteAsync(
            async context =>
            {
                var connection = context.Connections.Local.FirstOrDefault(item => item.Id == connectionId)
                    ?? await context.Connections
                        .Include(item => item.Tags)
                        .SingleOrDefaultAsync(item => item.Id == connectionId, cancellationToken)
                        .ConfigureAwait(false)
                    ?? throw new KeyNotFoundException($"Connection '{connectionId}' was not found.");
                return connection.AddTag(tagId).IsSuccess;
            },
            cancellationToken);
    }

    public Task<bool> RemoveTagAsync(Guid connectionId, Guid tagId, CancellationToken cancellationToken = default)
    {
        return WriteAsync(
            async context =>
            {
                var connection = context.Connections.Local.FirstOrDefault(item => item.Id == connectionId)
                    ?? await context.Connections
                        .Include(item => item.Tags)
                        .SingleOrDefaultAsync(item => item.Id == connectionId, cancellationToken)
                        .ConfigureAwait(false)
                    ?? throw new KeyNotFoundException($"Connection '{connectionId}' was not found.");
                return connection.RemoveTag(tagId);
            },
            cancellationToken);
    }
}
