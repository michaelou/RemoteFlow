using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Persistence.Repositories;

namespace RemoteFlow.Persistence;

public static class DependencyInjection
{
    public static IServiceCollection AddRemoteFlowPersistence(
        this IServiceCollection services,
        string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        _ = services.AddSingleton<IDbContextFactory<RemoteFlowDbContext>>(
            _ => new RemoteFlowDbContextFactory(dataDirectory));
        _ = services.AddSingleton<IClock>(SystemClock.Instance);
        _ = services.AddSingleton<DbContextScopeAccessor>();
        _ = services.AddSingleton<IConnectionRepository>(provider => new ConnectionRepository(
            provider.GetRequiredService<IDbContextFactory<RemoteFlowDbContext>>(),
            provider.GetRequiredService<DbContextScopeAccessor>()));
        _ = services.AddSingleton<IFolderRepository>(provider => new FolderRepository(
            provider.GetRequiredService<IDbContextFactory<RemoteFlowDbContext>>(),
            provider.GetRequiredService<DbContextScopeAccessor>()));
        _ = services.AddSingleton<ITagRepository>(provider => new TagRepository(
            provider.GetRequiredService<IDbContextFactory<RemoteFlowDbContext>>(),
            provider.GetRequiredService<DbContextScopeAccessor>()));
        _ = services.AddSingleton<IHostKeyStore>(provider => new HostKeyStore(
            provider.GetRequiredService<IDbContextFactory<RemoteFlowDbContext>>(),
            provider.GetRequiredService<DbContextScopeAccessor>()));
        _ = services.AddSingleton<IRecentConnectionStore>(provider => new RecentConnectionStore(
            provider.GetRequiredService<IDbContextFactory<RemoteFlowDbContext>>(),
            provider.GetRequiredService<DbContextScopeAccessor>()));
        _ = services.AddSingleton<ISettingsStore>(provider => new SettingsStore(
            provider.GetRequiredService<IDbContextFactory<RemoteFlowDbContext>>(),
            provider.GetRequiredService<IClock>()));
        _ = services.AddSingleton<IUnitOfWork>(provider => new UnitOfWork(
            provider.GetRequiredService<IDbContextFactory<RemoteFlowDbContext>>(),
            provider.GetRequiredService<DbContextScopeAccessor>()));
        return services;
    }
}
