using Microsoft.EntityFrameworkCore;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Domain.Entities;

namespace RemoteFlow.Persistence.Repositories;

public sealed class FolderRepository : RepositoryBase, IFolderRepository
{
    public FolderRepository(IDbContextFactory<RemoteFlowDbContext> contextFactory)
        : base(contextFactory)
    {
    }

    internal FolderRepository(IDbContextFactory<RemoteFlowDbContext> contextFactory, DbContextScopeAccessor scopeAccessor)
        : base(contextFactory, scopeAccessor)
    {
    }

    public Task<Folder?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return ReadAsync(
            context => context.Folders.AsNoTracking().SingleOrDefaultAsync(folder => folder.Id == id, cancellationToken),
            cancellationToken);
    }

    public Task<IReadOnlyList<Folder>> ListAsync(CancellationToken cancellationToken = default)
    {
        return ReadAsync<IReadOnlyList<Folder>>(
            async context => await context.Folders.AsNoTracking().OrderBy(folder => folder.Path)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken);
    }

    public Task AddAsync(Folder folder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        return WriteAsync(context => { _ = context.Folders.Add(folder); return Task.CompletedTask; }, cancellationToken);
    }

    public Task UpdateAsync(Folder folder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        return WriteAsync(async context =>
        {
            var existing = context.Folders.Local.FirstOrDefault(candidate => candidate.Id == folder.Id)
                ?? await context.Folders.SingleOrDefaultAsync(
                    candidate => candidate.Id == folder.Id,
                    cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Folder '{folder.Id}' was not found.");
            context.Entry(existing).CurrentValues.SetValues(folder);
        }, cancellationToken);
    }

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return WriteAsync(async context =>
        {
            var folder = await context.Folders.FindAsync([id], cancellationToken).ConfigureAwait(false);
            if (folder is not null)
            {
                _ = context.Folders.Remove(folder);
            }
        }, cancellationToken);
    }
}
