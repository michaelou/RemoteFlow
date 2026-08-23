using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using RemoteFlow.Application;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Storage;
using RemoteFlow.Infrastructure;
using RemoteFlow.Persistence;
using RemoteFlow.UI.Navigation;
using Xunit;

namespace RemoteFlow.UI.Tests;

/// <summary>Builds the container the desktop host builds, and resolves every page the sidebar can reach.
///
/// It exists because a missing registration is otherwise a runtime crash on first navigation: nothing else
/// in the suite constructs the real graph, and <c>ProjectSmokeTests</c> only asserts an assembly name.
/// </summary>
public sealed class NavigationCompositionTests
{
    // [AvaloniaFact], not [Fact]: TerminalSettingsViewModel's constructor reads FontManager.Current, and
    // touching an Avalonia global from a pool thread is exactly the hazard TestAppBuilder.cs documents —
    // it perturbed text measurement for every headless test that ran afterwards.
    [AvaloniaFact]
    public async Task EveryNavigationPageResolvesFromTheRealContainer()
    {
        var paths = new TempAppPaths();
        try
        {
            // The generic host supplies logging; this collection has to say so explicitly, because
            // Infrastructure's services take ILogger<T> the way they do in the real application.
            var services = new ServiceCollection()
                .AddLogging()
                .AddRemoteFlowApplication()
                .AddRemoteFlowInfrastructure(paths)
                .AddRemoteFlowPersistence(paths)
                .AddRemoteFlowUI();
            // Disposed asynchronously: the page view models are IAsyncDisposable, and the container
            // refuses to dispose one of those from the synchronous path.
            await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = false,
                ValidateScopes = true,
            });

            // Optional constructor parameters are the trap here: an unregistered one silently takes its
            // default, so the two pages with a local browser pane would each quietly stop remembering where
            // it was pointed and no page-level test would notice.
            _ = provider.GetRequiredService<ILocalFolderMemory>();

            var registrations = provider.GetServices<NavigationPageRegistration>().ToArray();

            Assert.Contains(registrations, page => page.Key == "storage");
            Assert.Equal(registrations.Length, registrations.Select(page => page.Key).Distinct().Count());
            foreach (var registration in registrations)
            {
                var page = registration.Factory();
                Assert.NotNull(page);
                Assert.False(
                    string.IsNullOrWhiteSpace(registration.IconKey),
                    $"{registration.Key} has no icon key.");
            }
        }
        finally
        {
            paths.Dispose();
        }
    }

    private sealed class TempAppPaths : IAppPaths, IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "remoteflow-composition-" + Guid.NewGuid().ToString("N"));

        public string ConfigDirectory => Path.Combine(_root, "config");

        public string DataDirectory => Path.Combine(_root, "data");

        public string CacheDirectory => Path.Combine(_root, "cache");

        public string LogDirectory => Path.Combine(_root, "logs");

        public void EnsureDirectories()
        {
            foreach (var directory in new[] { ConfigDirectory, DataDirectory, CacheDirectory, LogDirectory })
            {
                _ = Directory.CreateDirectory(directory);
            }
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch (IOException) { }
        }
    }
}
