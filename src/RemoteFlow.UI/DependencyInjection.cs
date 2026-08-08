using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Ssh;
using RemoteFlow.UI.Navigation;
using RemoteFlow.UI.Services;
using RemoteFlow.UI.ViewModels;
using RemoteFlow.UI.ViewModels.Connections;
using RemoteFlow.UI.ViewModels.CommandPalette;
using RemoteFlow.UI.ViewModels.Terminal;
using RemoteFlow.UI.ViewModels.Settings;
using RemoteFlow.UI.ViewModels.Security;
using RemoteFlow.UI.Views;

namespace RemoteFlow.UI;

public static class DependencyInjection
{
    public static IServiceCollection AddRemoteFlowUI(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ConnectionsPageViewModel>();
        services.TryAddSingleton<ConnectionEditorViewModelFactory>();
        services.TryAddSingleton<CommandPaletteViewModel>();
        services.TryAddSingleton<TerminalsPageViewModel>();
        services.TryAddSingleton<IUiDispatcher, UiDispatcher>();
        services.TryAddSingleton<TransfersPageViewModel>();
        services.TryAddSingleton<TerminalSettingsViewModel>();
        services.TryAddSingleton<SettingsPageViewModel>();
        services.TryAddSingleton<TrustedKeysViewModel>();
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
        services.TryAddSingleton<IClipboardService, AvaloniaClipboardService>();
        services.TryAddSingleton<IPasteWarningService, PasteWarningDialogService>();
        services.TryAddSingleton<TerminalClipboardController>();
        services.TryAddSingleton<IConfirmationDialogService, ConfirmationDialogService>();
        _ = services.Replace(ServiceDescriptor.Singleton<IHostKeyPrompt, HostKeyPromptService>());
        services.TryAddSingleton<IKeyboardInteractivePrompt, KeyboardInteractivePromptService>();
        services.TryAddSingleton<ISshCredentialPrompt, SshCredentialPromptService>();
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
