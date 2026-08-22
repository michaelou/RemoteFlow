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
        return WriteAsync(async context =>
        {
            var existing = context.Connections.Local.FirstOrDefault(candidate => candidate.Id == connection.Id)
                ?? await context.Connections
                    .Include(candidate => candidate.Tags)
                    .SingleOrDefaultAsync(candidate => candidate.Id == connection.Id, cancellationToken)
                    .ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Connection '{connection.Id}' was not found.");
            // Every owned block is copied explicitly, and this list has to stay in step with the model:
            // an owned type that is configured but not copied here is silently discarded on every update,
            // while still saving correctly on create. RepositoryRoundTripTests counts the model's owned
            // navigations against this list so the next one cannot be forgotten the way ObjectStorage was.
            context.Entry(existing).CurrentValues.SetValues(connection);
            context.Entry(existing.Credential).CurrentValues.SetValues(connection.Credential);
            context.Entry(existing.Ssh).CurrentValues.SetValues(connection.Ssh);
            context.Entry(existing.Sftp).CurrentValues.SetValues(connection.Sftp);
            context.Entry(existing.Rdp).CurrentValues.SetValues(connection.Rdp);
            context.Entry(existing.ObjectStorage).CurrentValues.SetValues(connection.ObjectStorage);

            foreach (var removedTag in existing.Tags
                         .Where(item => connection.Tags.All(candidate => candidate.TagId != item.TagId))
                         .ToArray())
            {
                _ = existing.RemoveTag(removedTag.TagId);
            }

            foreach (var addedTag in connection.Tags.Where(item =>
                         existing.Tags.All(candidate => candidate.TagId != item.TagId)))
            {
                _ = existing.AddTag(addedTag.TagId);
            }
        }, cancellationToken);
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
