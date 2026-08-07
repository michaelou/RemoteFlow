using Microsoft.EntityFrameworkCore;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Domain.Entities;

namespace RemoteFlow.Persistence.Repositories;

public sealed class TagRepository : RepositoryBase, ITagRepository
{
    public TagRepository(IDbContextFactory<RemoteFlowDbContext> contextFactory) : base(contextFactory) { }

    internal TagRepository(IDbContextFactory<RemoteFlowDbContext> contextFactory, DbContextScopeAccessor scopeAccessor)
        : base(contextFactory, scopeAccessor) { }

    public Task<Tag?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return ReadAsync(context => context.Tags.AsNoTracking().SingleOrDefaultAsync(tag => tag.Id == id, cancellationToken), cancellationToken);
    }

    public Task<Tag?> GetByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return ReadAsync(context => context.Tags.AsNoTracking().SingleOrDefaultAsync(tag => tag.Name == name, cancellationToken), cancellationToken);
    }

    public Task<IReadOnlyList<Tag>> ListAsync(CancellationToken cancellationToken = default)
    {
        return ReadAsync<IReadOnlyList<Tag>>(async context => await context.Tags.AsNoTracking().OrderBy(tag => tag.Name)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false), cancellationToken);
    }

    public Task AddAsync(Tag tag, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tag);
        return WriteAsync(context => { _ = context.Tags.Add(tag); return Task.CompletedTask; }, cancellationToken);
    }

    public Task UpdateAsync(Tag tag, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tag);
        return WriteAsync(context => { _ = context.Tags.Update(tag); return Task.CompletedTask; }, cancellationToken);
    }

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return WriteAsync(async context =>
        {
            var tag = await context.Tags.FindAsync([id], cancellationToken).ConfigureAwait(false);
            if (tag is not null)
            {
                _ = context.Tags.Remove(tag);
            }
        }, cancellationToken);
    }

    public Task<int> GetUsageCountAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return ReadAsync(context => context.ConnectionTags.CountAsync(item => item.TagId == id, cancellationToken), cancellationToken);
    }
}
