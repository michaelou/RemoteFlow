using Microsoft.EntityFrameworkCore;
using RemoteFlow.Application.Abstractions;

namespace RemoteFlow.Persistence.Repositories;

public sealed class UnitOfWork : IUnitOfWork
{
    private readonly IDbContextFactory<RemoteFlowDbContext> _contextFactory;
    private readonly DbContextScopeAccessor _scopeAccessor;

    public UnitOfWork(IDbContextFactory<RemoteFlowDbContext> contextFactory)
        : this(contextFactory, DbContextScopeRegistry.Get(contextFactory))
    {
    }

    internal UnitOfWork(
        IDbContextFactory<RemoteFlowDbContext> contextFactory,
        DbContextScopeAccessor scopeAccessor)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _scopeAccessor = scopeAccessor ?? throw new ArgumentNullException(nameof(scopeAccessor));
    }

    public async Task ExecuteAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        _ = await ExecuteAsync(
            async token =>
            {
                await operation(token).ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<TResult> ExecuteAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (_scopeAccessor.Current is not null)
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        using var scope = _scopeAccessor.Push(context);
        var result = await operation(cancellationToken).ConfigureAwait(false);
        _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }
}
