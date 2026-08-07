using Microsoft.EntityFrameworkCore;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Domain.Entities;

namespace RemoteFlow.Persistence.Repositories;

public sealed class HostKeyStore : RepositoryBase, IHostKeyStore
{
    public HostKeyStore(IDbContextFactory<RemoteFlowDbContext> contextFactory) : base(contextFactory) { }

    internal HostKeyStore(IDbContextFactory<RemoteFlowDbContext> contextFactory, DbContextScopeAccessor scopeAccessor)
        : base(contextFactory, scopeAccessor) { }

    public Task<HostKey?> GetAsync(string host, int port, string keyAlgorithm, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyAlgorithm);
        return ReadAsync(context => context.HostKeys.AsNoTracking().SingleOrDefaultAsync(
            item => item.Host == host && item.Port == port && item.KeyAlgorithm == keyAlgorithm,
            cancellationToken), cancellationToken);
    }

    public Task<IReadOnlyList<HostKey>> ListAsync(CancellationToken cancellationToken = default)
    {
        return ReadAsync<IReadOnlyList<HostKey>>(async context => await context.HostKeys.AsNoTracking()
            .OrderBy(item => item.Host).ThenBy(item => item.Port).ThenBy(item => item.KeyAlgorithm)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false), cancellationToken);
    }

    public Task AddAsync(HostKey hostKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hostKey);
        return WriteAsync(context => { _ = context.HostKeys.Add(hostKey); return Task.CompletedTask; }, cancellationToken);
    }

    public Task UpdateAsync(HostKey hostKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hostKey);
        return WriteAsync(context => { _ = context.HostKeys.Update(hostKey); return Task.CompletedTask; }, cancellationToken);
    }

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return WriteAsync(async context =>
        {
            var hostKey = await context.HostKeys.FindAsync([id], cancellationToken).ConfigureAwait(false);
            if (hostKey is not null)
            {
                _ = context.HostKeys.Remove(hostKey);
            }
        }, cancellationToken);
    }
}
