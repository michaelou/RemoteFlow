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
        // Answered before anything is composed or shown: --version has to work from a script, on a machine
        // where the database or the credential store might not be usable at all.
        if (VersionSwitch.IsRequested(args))
        {
            // A WinExe has no console of its own, so borrow the caller's before writing.
            _ = ParentConsole.TryAttach();
            Console.Out.WriteLine(VersionSwitch.Format(AssemblyVersionInfo.ForEntryAssembly()));
            Console.Out.Flush();
            return;
        }

        // Held for as long as this process is. The installer and the uninstaller both look for it and stop
        // rather than replacing files a running copy is using. Deliberately after the --version return: a
        // script asking what a binary is should not announce a running instance to an installer.
        RunningInstanceMutex.Acquire();

        // Must run before the first window exists, otherwise the taskbar button has already been
        // grouped under the launching process and keeps showing that process's icon.
        _ = WindowsShellIdentity.Apply();
        var builder = Host.CreateApplicationBuilder(args);
        _ = DesktopComposition.ConfigureServices(builder, new AppPaths());
        using var host = builder.Build();
        var exceptionHandler = host.Services.GetRequiredService<IGlobalExceptionHandler>();
        exceptionHandler.Install();
        var updateInstaller = host.Services.GetRequiredService<IUpdateInstaller>();
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

            // The last thing this process does, and only when an update was queued. The installer replaces
            // the directory this executable is running from, so it must not start until the window has gone
            // and the hosted services have stopped — moments before the image itself is unlocked.
            updateInstaller.RunPendingInstall();
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
