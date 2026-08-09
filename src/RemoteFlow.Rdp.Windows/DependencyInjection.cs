using Microsoft.Extensions.DependencyInjection;

namespace RemoteFlow.Rdp.Windows;

public static class DependencyInjection
{
    public static IServiceCollection AddRemoteFlowRdpWindows(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The platform composition point exists before the provider implementation so native services
        // never need to be registered from a shared project. Issue #80 adds the first registration.
        return services;
    }
}
