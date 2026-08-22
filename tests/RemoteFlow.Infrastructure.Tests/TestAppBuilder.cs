using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(RemoteFlow.Infrastructure.Tests.TestAppBuilder))]

// Same reason as RemoteFlow.UI.Tests: Avalonia's type initialisers race when more than one thread runs
// them, and this assembly also mixes `[AvaloniaFact]` with plain `[Fact]`. Only two tests here need
// Avalonia, which makes the race rarer rather than absent. Switched off in xunit.runner.json.

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
