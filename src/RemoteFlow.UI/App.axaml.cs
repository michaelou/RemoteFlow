using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using RemoteFlow.UI.Views;

namespace RemoteFlow.UI;

public sealed class App : global::Avalonia.Application
{
    public Func<MainWindow>? MainWindowFactory { get; init; }

    public Func<Task>? StartupAction { get; init; }

    public Func<Exception, Task>? StartupErrorAction { get; init; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && MainWindowFactory is not null)
        {
            var mainWindow = MainWindowFactory();
            mainWindow.Opened += OnMainWindowOpened;

            desktop.MainWindow = mainWindow;
        }

        base.OnFrameworkInitializationCompleted();
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
                await StartupAction().ConfigureAwait(true);
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
