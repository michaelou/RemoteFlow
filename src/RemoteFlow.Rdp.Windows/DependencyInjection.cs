using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RemoteFlow.Application.Abstractions;

namespace RemoteFlow.Rdp.Windows;

public static class DependencyInjection
{
    public static IServiceCollection AddRemoteFlowRdpWindows(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        _ = services.RemoveAll<IEmbeddedRdpSessionProvider>();
        _ = services.AddSingleton<IEmbeddedRdpSessionProvider>(WindowsEmbeddedRdpSessionProvider.Instance);
        return services;
    }
}
