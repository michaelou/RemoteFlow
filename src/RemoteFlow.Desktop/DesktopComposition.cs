using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RemoteFlow.Application;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Infrastructure;
using RemoteFlow.Persistence;
using RemoteFlow.UI;

namespace RemoteFlow.Desktop;

public static class DesktopComposition
{
    public static HostApplicationBuilder ConfigureServices(HostApplicationBuilder builder, IAppPaths appPaths)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(appPaths);
        _ = builder.Logging.ClearProviders();
        _ = builder.Services
            .AddRemoteFlowApplication()
            .AddRemoteFlowInfrastructure(appPaths)
            .AddRemoteFlowPersistence(appPaths)
            .AddRemoteFlowUI();
        return builder;
    }
}
