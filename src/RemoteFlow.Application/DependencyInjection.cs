using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Domain.Abstractions;

namespace RemoteFlow.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddRemoteFlowApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IClock>(SystemClock.Instance);
        services.TryAddSingleton<IGuidProvider>(SystemGuidProvider.Instance);
        return services;
    }
}
