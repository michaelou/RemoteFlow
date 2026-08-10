using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Ssh;
using RemoteFlow.Application.Abstractions.Sftp;
using RemoteFlow.UI.Navigation;
using RemoteFlow.UI.Services;
using RemoteFlow.UI.ViewModels;
using RemoteFlow.UI.ViewModels.Connections;
using RemoteFlow.UI.ViewModels.CommandPalette;
using RemoteFlow.UI.ViewModels.Terminal;
using RemoteFlow.UI.ViewModels.Transfers;
using RemoteFlow.UI.ViewModels.Settings;
using RemoteFlow.UI.ViewModels.Security;
using RemoteFlow.UI.ViewModels.Sftp;
using RemoteFlow.UI.ViewModels.Backup;
using RemoteFlow.UI.Views;

namespace RemoteFlow.UI;

public static class DependencyInjection
{
    public static IServiceCollection AddRemoteFlowUI(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ConnectionsPageViewModel>();
        services.TryAddSingleton<BackupExportViewModel>();
        services.TryAddSingleton<BackupImportPreviewViewModel>();
        services.TryAddSingleton<BackupPageViewModel>();
        services.TryAddSingleton<ConnectionEditorViewModelFactory>();
        services.TryAddSingleton<CommandPaletteViewModel>();
        services.TryAddSingleton<TerminalsPageViewModel>();
        services.TryAddSingleton<IUiDispatcher, UiDispatcher>();
        services.TryAddSingleton<TransfersPageViewModel>();
        services.TryAddSingleton<SftpWorkspaceViewModel>();
        services.TryAddSingleton<TerminalSettingsViewModel>();
        services.TryAddSingleton<RdpSettingsViewModel>();
        services.TryAddSingleton<IEmbeddedRdpWorkspaceSessionFactory>(NoEmbeddedRdpWorkspaceSessionFactory.Instance);
        // The version is stamped into the assembly that started the process; a host that knows better can
        // register its own IAppVersionInfo before calling this.
        services.TryAddSingleton<IAppVersionInfo>(_ => AssemblyVersionInfo.ForEntryAssembly());
        services.TryAddSingleton<AboutViewModel>();
        services.TryAddSingleton<SettingsPageViewModel>();
        services.TryAddSingleton<TrustedKeysViewModel>();
        _ = services.AddSingleton(provider => new NavigationPageRegistration(
            "connections",
            "Connections",
            "Icon.Connections",
            provider.GetRequiredService<ConnectionsPageViewModel>));
        _ = services.AddSingleton(provider => new NavigationPageRegistration(
            "terminals",
            "Terminals",
            "Icon.Terminals",
            provider.GetRequiredService<TerminalsPageViewModel>));
        _ = services.AddSingleton(provider => new NavigationPageRegistration(
            "transfers",
            "Transfers",
            "Icon.Transfers",
            provider.GetRequiredService<TransfersPageViewModel>));
        _ = services.AddSingleton(provider => new NavigationPageRegistration(
            "sftp",
            "SFTP",
            "Icon.Sftp",
            provider.GetRequiredService<SftpWorkspaceViewModel>));
        _ = services.AddSingleton(provider => new NavigationPageRegistration(
            "backup",
            "Backup",
            "Icon.Backup",
            provider.GetRequiredService<BackupPageViewModel>));
        _ = services.AddSingleton(provider => new NavigationPageRegistration(
            "settings",
            "Settings",
            "Icon.Settings",
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
        services.TryAddSingleton<IRemoteEditCloseGuard, RemoteEditCloseGuard>();
        services.TryAddSingleton<IRemoteEditConflictDialogService, RemoteEditConflictDialogService>();
        services.TryAddSingleton<IRemoteEditConflictResolver, RemoteEditConflictResolver>();
        services.TryAddSingleton<IFilePickerService, AvaloniaFilePickerService>();
        services.TryAddSingleton<ISftpWorkspaceSessionFactory, SftpWorkspaceSessionFactory>();
        _ = services.Replace(ServiceDescriptor.Singleton<IHostKeyPrompt, HostKeyPromptService>());
        services.TryAddSingleton<IKeyboardInteractivePrompt, KeyboardInteractivePromptService>();
        services.TryAddSingleton<ISshCredentialPrompt, SshCredentialPromptService>();
        services.TryAddSingleton<IConnectionSessionOpener, DeferredConnectionSessionOpener>();
        _ = services.Replace(ServiceDescriptor.Singleton<IConnectionSessionOpener, SshConnectionSessionOpener>());
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
                await provider.GetRequiredService<IRemoteEditServiceFactory>()
                    .SweepStaleFilesAsync().ConfigureAwait(true);
                await provider.GetRequiredService<IRdpLauncher>()
                    .SweepStaleFilesAsync().ConfigureAwait(true);
                // Reads the update opt-in and, only if it is on, starts one check. This awaits the
                // settings read, not the network call — see AboutViewModel.InitializeAsync.
                await provider.GetRequiredService<AboutViewModel>()
                    .InitializeAsync().ConfigureAwait(true);
            },
            StartupErrorAction = exception => provider.GetRequiredService<IGlobalExceptionHandler>()
                .HandleAsync(exception, "application startup"),
        });
        return services;
    }
}
