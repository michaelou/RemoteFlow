using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(RemoteFlow.Infrastructure.Tests.TestAppBuilder))]

namespace RemoteFlow.Infrastructure.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<UI.App>()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
    }
}
