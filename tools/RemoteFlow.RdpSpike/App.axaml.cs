using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace RemoteFlow.RdpSpike;

public sealed class App : Avalonia.Application
{
    internal static SpikeOptions LaunchOptions { get; set; } = SpikeOptions.Parse([]);

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
