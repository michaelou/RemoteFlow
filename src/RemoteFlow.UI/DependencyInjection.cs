using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.UI.Navigation;
using RemoteFlow.UI.Services;
using RemoteFlow.UI.ViewModels;
using RemoteFlow.UI.ViewModels.Connections;
using RemoteFlow.UI.ViewModels.CommandPalette;
using RemoteFlow.UI.Views;

namespace RemoteFlow.UI;

public static class DependencyInjection
{
    public static IServiceCollection AddRemoteFlowUI(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ConnectionsPageViewModel>();
        services.TryAddSingleton<CommandPaletteViewModel>();
        services.TryAddSingleton<TerminalsPageViewModel>();
        services.TryAddSingleton<TransfersPageViewModel>();
        services.TryAddSingleton<SettingsPageViewModel>();
        _ = services.AddSingleton(provider => new NavigationPageRegistration(
            "connections",
            "Connections",
            provider.GetRequiredService<ConnectionsPageViewModel>));
        _ = services.AddSingleton(provider => new NavigationPageRegistration(
            "terminals",
            "Terminals",
            provider.GetRequiredService<TerminalsPageViewModel>));
        _ = services.AddSingleton(provider => new NavigationPageRegistration(
            "transfers",
            "Transfers",
            provider.GetRequiredService<TransfersPageViewModel>));
        _ = services.AddSingleton(provider => new NavigationPageRegistration(
            "settings",
            "Settings",
            provider.GetRequiredService<SettingsPageViewModel>));
        services.TryAddSingleton<INavigationService>(provider => new NavigationService(
            provider.GetServices<NavigationPageRegistration>(),
            "connections"));
        services.TryAddSingleton<MainWindowViewModel>();
        services.TryAddSingleton<WindowGeometryService>();
        services.TryAddSingleton<MainWindow>();
        services.TryAddSingleton<IErrorDialogService, ErrorDialogService>();
        services.TryAddSingleton<IConnectionSessionOpener, DeferredConnectionSessionOpener>();
        services.TryAddSingleton<IThemeService>(provider => new ThemeService(
            provider.GetRequiredService<App>(),
            provider.GetRequiredService<ISettingsStore>()));
        services.TryAddSingleton(provider => new App
        {
            MainWindowFactory = provider.GetRequiredService<MainWindow>,
            StartupAction = async () =>
            {
                await provider.GetRequiredService<IDbInitializer>().InitializeAsync().ConfigureAwait(true);
                await provider.GetRequiredService<IThemeService>().InitializeAsync().ConfigureAwait(true);
            },
            StartupErrorAction = exception => provider.GetRequiredService<IGlobalExceptionHandler>()
                .HandleAsync(exception, "application startup"),
        });
        return services;
    }
}
