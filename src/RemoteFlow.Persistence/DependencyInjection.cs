using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Queries;
using RemoteFlow.Application.Abstractions.Backup;
using RemoteFlow.Persistence.Backup;
using RemoteFlow.Persistence.Queries;
using RemoteFlow.Persistence.Repositories;

namespace RemoteFlow.Persistence;

public static class DependencyInjection
{
    public static IServiceCollection AddRemoteFlowPersistence(
        this IServiceCollection services,
        IAppPaths appPaths)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(appPaths);

        services.TryAddSingleton(appPaths);
        AddPersistenceServices(services, appPaths.DataDirectory);
        _ = services.AddSingleton<IDbInitializer, DbInitializer>();
        return services;
    }

    public static IServiceCollection AddRemoteFlowPersistence(
        this IServiceCollection services,
        string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        AddPersistenceServices(services, dataDirectory);
        return services;
    }

    private static void AddPersistenceServices(IServiceCollection services, string dataDirectory)
    {
        _ = services.AddSingleton<IDbContextFactory<RemoteFlowDbContext>>(
            _ => new RemoteFlowDbContextFactory(dataDirectory));
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
        _ = services.AddSingleton<IConnectionQueryService, ConnectionQueryService>();
        _ = services.AddSingleton<IBackupDataSource, EfBackupDataSource>();
        _ = services.AddSingleton<IBackupImportStore>(provider => new EfBackupImportStore(
            provider.GetRequiredService<IDbContextFactory<RemoteFlowDbContext>>(),
            Path.Combine(dataDirectory, RemoteFlowDatabase.FileName),
            provider.GetService<IBackupImportFaultInjector>()));
    }
}
