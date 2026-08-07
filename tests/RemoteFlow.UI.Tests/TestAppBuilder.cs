using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(RemoteFlow.UI.Tests.TestAppBuilder))]

namespace RemoteFlow.UI.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<UI.App>()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
    }
}
