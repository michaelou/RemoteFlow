using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(RemoteFlow.UI.Tests.TestAppBuilder))]

// Avalonia's static initialisers are not thread-safe: `RoutedEvent.Register` writes into a plain
// Dictionary in `RoutedEventRegistry`, so two threads running a type initialiser at once corrupt it and
// every later construction of a control fails with a TypeInitializationException. This assembly is exactly
// the shape that provokes it — `[AvaloniaFact]` runs on the headless dispatcher thread while plain
// `[Fact]` tests run on pool threads, and xunit parallelises by collection, which here means by class.
//
// The cost is that this assembly runs serially. That is a few seconds, against a failure mode that cost a
// red Windows build and 63 failures reported as 63 unrelated bugs.
//
// It is switched off in xunit.runner.json, not with [assembly: CollectionBehavior]. That attribute compiles
// and does nothing here: xunit v3's in-process runner reports "parallel test collections = on" with it
// applied, and honours the JSON instead.

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
