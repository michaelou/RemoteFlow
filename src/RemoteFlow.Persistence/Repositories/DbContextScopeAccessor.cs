using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;

namespace RemoteFlow.Persistence.Repositories;

internal static class DbContextScopeRegistry
{
    private static readonly ConditionalWeakTable<IDbContextFactory<RemoteFlowDbContext>, DbContextScopeAccessor> _scopes = [];

    public static DbContextScopeAccessor Get(IDbContextFactory<RemoteFlowDbContext> contextFactory)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        return _scopes.GetValue(contextFactory, static _ => new DbContextScopeAccessor());
    }
}

internal sealed class DbContextScopeAccessor
{
    private readonly AsyncLocal<RemoteFlowDbContext?> _current = new();

    public RemoteFlowDbContext? Current => _current.Value;

    public IDisposable Push(RemoteFlowDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_current.Value is not null)
        {
            throw new InvalidOperationException("A database unit of work is already active.");
        }

        _current.Value = context;
        return new Scope(this);
    }

    private sealed class Scope(DbContextScopeAccessor owner) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            owner._current.Value = null;
            _disposed = true;
        }
    }
}
