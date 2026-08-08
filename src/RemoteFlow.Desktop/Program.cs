using Avalonia;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Infrastructure.Platform;
using RemoteFlow.UI;

namespace RemoteFlow.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run before the first window exists, otherwise the taskbar button has already been
        // grouped under the launching process and keeps showing that process's icon.
        _ = WindowsShellIdentity.Apply();
        var builder = Host.CreateApplicationBuilder(args);
        _ = DesktopComposition.ConfigureServices(builder, new AppPaths());
        using var host = builder.Build();
        var exceptionHandler = host.Services.GetRequiredService<IGlobalExceptionHandler>();
        exceptionHandler.Install();
        Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandledException;

        try
        {
            host.Start();
            _ = BuildAvaloniaApp(host.Services).StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            Dispatcher.UIThread.UnhandledException -= OnDispatcherUnhandledException;
            host.StopAsync().GetAwaiter().GetResult();
        }

        void OnDispatcherUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs eventArgs)
        {
            eventArgs.Handled = true;
            _ = exceptionHandler.HandleAsync(eventArgs.Exception, "UI dispatcher");
        }
    }

    public static AppBuilder BuildAvaloniaApp(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return AppBuilder.Configure(services.GetRequiredService<App>)
            .UsePlatformDetect()
            .WithInterFont();
    }
}
