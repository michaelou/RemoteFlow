using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using RemoteFlow.UI.Input;
using RemoteFlow.UI.Services;
using RemoteFlow.UI.Views;

namespace RemoteFlow.UI;

public sealed class App : global::Avalonia.Application
{
    public Func<MainWindow>? MainWindowFactory { get; init; }

    /// <summary>The splash shown while <see cref="StartupAction"/> runs. Optional: a host that does not
    /// supply one gets the main window immediately, empty until its own startup fills it in.</summary>
    public Func<SplashWindow>? SplashFactory { get; init; }

    public Func<IStartupProgress, Task>? StartupAction { get; init; }

    public Func<Exception, Task>? StartupErrorAction { get; init; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        EnterActivatesFocusedButton.Install();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && MainWindowFactory is not null)
        {
            if (SplashFactory is null)
            {
                var mainWindow = MainWindowFactory();
                mainWindow.Opened += OnMainWindowOpened;
                desktop.MainWindow = mainWindow;
            }
            else
            {
                // The main window is built, loaded and placed behind the splash and only then shown, so
                // nothing half-built is ever on screen. Two consequences, both deliberate:
                //
                // MainWindow stays null until it is ready, which is what stops the lifetime showing it —
                // the lifetime calls MainWindow?.Show() once, immediately after this method returns.
                //
                // And shutdown has to follow the main window rather than the last window standing: the
                // splash closes on purpose, and while it is the only window OnLastWindowClose would read
                // that as the application having finished and end the process.
                desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
                var splash = SplashFactory();
                splash.Show();
                _ = StartBehindSplashAsync(desktop, splash);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task StartBehindSplashAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        SplashWindow splash)
    {
        try
        {
            if (StartupAction is not null)
            {
                await StartupAction(splash.Progress).ConfigureAwait(true);
            }

            splash.Progress.Report("Opening the workspace");
            var mainWindow = MainWindowFactory!();

            // Assigned before it is shown, so that the moment it is on screen it is the window closing it
            // ends the application. Loading and placing it first is the whole point: InitializeAsync reads
            // the connections and restores the remembered size and position, and doing that after Show is
            // what used to make the window appear at a default size and then jump.
            desktop.MainWindow = mainWindow;
            await mainWindow.InitializeAsync().ConfigureAwait(true);
            mainWindow.Show();
            mainWindow.Activate();
        }
        catch (Exception exception)
        {
            // The splash is topmost while it is up, and what comes next is an error dialog: stop it
            // sitting over the one message the user needs to read.
            splash.Topmost = false;
            if (StartupErrorAction is not null)
            {
                await StartupErrorAction(exception).ConfigureAwait(true);
            }

            // Nothing reached the screen, so there is no window left to keep the process alive and no
            // window for the user to close. Ending it is the only honest outcome: the alternative is an
            // invisible process that has to be killed from a task manager.
            if (desktop.MainWindow is null)
            {
                splash.Close();
                desktop.Shutdown(1);
                return;
            }
        }
        finally
        {
            // Last, and after the main window is up: closing it while it is the only window open would
            // trip the lifetime's shutdown, and closing it before the main window is drawn would put a
            // hole on the desktop between the two.
            if (splash.IsVisible)
            {
                splash.Close();
            }
        }
    }

    private async void OnMainWindowOpened(object? sender, EventArgs eventArgs)
    {
        if (sender is not MainWindow mainWindow)
        {
            return;
        }

        mainWindow.Opened -= OnMainWindowOpened;

        try
        {
            if (StartupAction is not null)
            {
                await StartupAction(NullStartupProgress.Instance).ConfigureAwait(true);
            }

            await mainWindow.InitializeAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            if (StartupErrorAction is not null)
            {
                await StartupErrorAction(exception).ConfigureAwait(true);
            }
        }
    }
}
