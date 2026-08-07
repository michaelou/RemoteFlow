using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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
        return services;
    }
}
