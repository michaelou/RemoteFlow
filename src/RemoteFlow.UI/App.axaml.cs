using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using RemoteFlow.UI.Views;

namespace RemoteFlow.UI;

public sealed class App : global::Avalonia.Application
{
    public Func<MainWindow>? MainWindowFactory { get; init; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && MainWindowFactory is not null)
        {
            desktop.MainWindow = MainWindowFactory();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
