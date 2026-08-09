using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Rdp.Windows.Interop;
using RemoteFlow.UI.Services;

namespace RemoteFlow.Rdp.Windows;

public static class DependencyInjection
{
    public static IServiceCollection AddRemoteFlowRdpWindows(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        _ = services.RemoveAll<IEmbeddedRdpSessionProvider>();
        services.TryAddSingleton<INativeRdpControlFactory>(WindowsNativeRdpControlFactory.Instance);
        _ = services.AddSingleton(provider => new WindowsEmbeddedRdpSessionProvider(
            provider.GetRequiredService<INativeRdpControlFactory>(),
            provider.GetRequiredService<IUiDispatcher>(),
            provider.GetServices<ICredentialProvider>(),
            provider.GetService<Microsoft.Extensions.Logging.ILogger<WindowsEmbeddedRdpSession>>()));
        _ = services.AddSingleton<IEmbeddedRdpSessionProvider>(provider =>
            provider.GetRequiredService<WindowsEmbeddedRdpSessionProvider>());
        return services;
    }
}
