using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RemoteFlow.Application;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Infrastructure;
using RemoteFlow.Persistence;
using RemoteFlow.UI;
#if WINDOWS_RDP
using RemoteFlow.Rdp.Windows;
#endif

namespace RemoteFlow.Desktop;

public static class DesktopComposition
{
    public static HostApplicationBuilder ConfigureServices(HostApplicationBuilder builder, IAppPaths appPaths)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(appPaths);
        _ = builder.Logging.ClearProviders();
        var services = builder.Services
            .AddRemoteFlowApplication()
            .AddRemoteFlowInfrastructure(appPaths)
            .AddRemoteFlowPersistence(appPaths)
            .AddRemoteFlowUI();
#if WINDOWS_RDP
        if (OperatingSystem.IsWindowsVersionAtLeast(7))
        {
            _ = services.AddRemoteFlowRdpWindows();
        }
#endif
        return builder;
    }
}
