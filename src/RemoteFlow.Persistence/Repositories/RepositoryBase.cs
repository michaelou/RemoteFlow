using Microsoft.EntityFrameworkCore;

namespace RemoteFlow.Persistence.Repositories;

public abstract class RepositoryBase
{
    private readonly IDbContextFactory<RemoteFlowDbContext> _contextFactory;
    private readonly DbContextScopeAccessor _scopeAccessor;

    private protected RepositoryBase(IDbContextFactory<RemoteFlowDbContext> contextFactory)
        : this(contextFactory, DbContextScopeRegistry.Get(contextFactory))
    {
    }

    private protected RepositoryBase(
        IDbContextFactory<RemoteFlowDbContext> contextFactory,
        DbContextScopeAccessor scopeAccessor)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _scopeAccessor = scopeAccessor ?? throw new ArgumentNullException(nameof(scopeAccessor));
    }

    private protected async Task<TResult> ReadAsync<TResult>(
        Func<RemoteFlowDbContext, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        if (_scopeAccessor.Current is { } current)
        {
            return await operation(current).ConfigureAwait(false);
        }

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await operation(context).ConfigureAwait(false);
    }

    private protected async Task WriteAsync(
        Func<RemoteFlowDbContext, Task> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        if (_scopeAccessor.Current is { } current)
        {
            await operation(current).ConfigureAwait(false);
            return;
        }

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await operation(context).ConfigureAwait(false);
        _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private protected async Task<TResult> WriteAsync<TResult>(
        Func<RemoteFlowDbContext, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        if (_scopeAccessor.Current is { } current)
        {
            return await operation(current).ConfigureAwait(false);
        }

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var result = await operation(context).ConfigureAwait(false);
        _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }
}
