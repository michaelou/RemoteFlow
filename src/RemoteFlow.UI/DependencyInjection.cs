using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Backup;
using RemoteFlow.Application.Services;
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
using RemoteFlow.UI.ViewModels.Storage;
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
        services.TryAddSingleton<AutomaticBackupSettingsViewModel>();
        services.TryAddSingleton<BackupPageViewModel>();
        services.TryAddSingleton<ConnectionEditorViewModelFactory>();
        services.TryAddSingleton<CommandPaletteViewModel>();
        services.TryAddSingleton<TerminalsPageViewModel>();
        services.TryAddSingleton<IUiDispatcher, UiDispatcher>();
        services.TryAddSingleton<TransfersPageViewModel>();
        services.TryAddSingleton<SftpWorkspaceViewModel>();
        services.TryAddSingleton<StoragePageViewModel>();
        services.TryAddSingleton<TerminalSettingsViewModel>();
        services.TryAddSingleton<RdpSettingsViewModel>();
        services.TryAddSingleton<IEmbeddedRdpWorkspaceSessionFactory>(NoEmbeddedRdpWorkspaceSessionFactory.Instance);
        // The version is stamped into the assembly that started the process; a host that knows better can
        // register its own IAppVersionInfo before calling this.
        services.TryAddSingleton<IAppVersionInfo>(_ => AssemblyVersionInfo.ForEntryAssembly());
        services.TryAddSingleton<AboutViewModel>();
        services.TryAddSingleton<SettingsPageViewModel>();
        services.TryAddSingleton<TrustedKeysViewModel>();
        // The sidebar is these registrations in order. Transfers comes after SFTP and Storage because it
        // is where those two pages send their work: the queue reads as the tail of the file pages rather
        // than as a third way to move a file.
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
            "sftp",
            "SFTP",
            "Icon.Sftp",
            provider.GetRequiredService<SftpWorkspaceViewModel>));
        _ = services.AddSingleton(provider => new NavigationPageRegistration(
            "storage",
            "Storage",
            "Icon.Storage",
            provider.GetRequiredService<StoragePageViewModel>));
        _ = services.AddSingleton(provider => new NavigationPageRegistration(
            "transfers",
            "Transfers",
            "Icon.Transfers",
            provider.GetRequiredService<TransfersPageViewModel>));
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
        services.TryAddSingleton<IApplicationShutdown, ApplicationShutdownService>();
        services.TryAddSingleton<IRemoteEditCloseGuard, RemoteEditCloseGuard>();
        services.TryAddSingleton<IRemoteEditConflictDialogService, RemoteEditConflictDialogService>();
        services.TryAddSingleton<IRemoteEditConflictResolver, RemoteEditConflictResolver>();
        services.TryAddSingleton<IFilePickerService, AvaloniaFilePickerService>();
        services.TryAddSingleton<ISftpWorkspaceSessionFactory, SftpWorkspaceSessionFactory>();
        services.TryAddSingleton<IStorageWorkspaceSessionFactory, StorageWorkspaceSessionFactory>();
        services.TryAddSingleton<ITransferConflictDialogService, TransferConflictDialogService>();
        services.TryAddSingleton<ITransferConflictResolverFactory, TransferConflictResolverFactory>();
        _ = services.Replace(ServiceDescriptor.Singleton<IHostKeyPrompt, HostKeyPromptService>());
        services.TryAddSingleton<IKeyboardInteractivePrompt, KeyboardInteractivePromptService>();
        services.TryAddSingleton<ISshCredentialPrompt, SshCredentialPromptService>();
        services.TryAddSingleton<IVaultUnlockPrompt, VaultUnlockPromptService>();
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
                // Before anything reads a credential. On Windows and macOS, and on Linux with a working
                // keyring, this asks nothing and returns immediately; it only prompts when the selected
                // store is RemoteFlow's own encrypted file, which nothing else opens. Declining is allowed
                // and leaves the session running without saved secrets.
                _ = await provider.GetRequiredService<IVaultUnlockService>()
                    .EnsureUnlockedAsync().ConfigureAwait(true);
                await provider.GetRequiredService<IRemoteEditServiceFactory>()
                    .SweepStaleFilesAsync().ConfigureAwait(true);
                await provider.GetRequiredService<IRdpLauncher>()
                    .SweepStaleFilesAsync().ConfigureAwait(true);
                // Not straight after an install: until RemoteFlow has started again, the downloaded
                // installer is the only way back from one that destroyed what it was replacing.
                await provider.GetRequiredService<IUpdateInstaller>()
                    .SweepStaleFilesAsync().ConfigureAwait(true);
                // Subscribes to change signals and, if the last run never finished, arms a catch-up. Reads
                // one setting and one small file; it must never await a backup, or the first paint would
                // wait on an SSH handshake.
                var autoBackup = provider.GetRequiredService<IAutoBackupRunner>();
                await autoBackup.SweepStaleFilesAsync().ConfigureAwait(true);
                await autoBackup.InitializeAsync().ConfigureAwait(true);
                // Reads the update opt-in and, only if it is on, starts one check. Also reports an update
                // that was started and never arrived, which is a thing only the next launch can notice.
                await provider.GetRequiredService<AboutViewModel>()
                    .InitializeAsync().ConfigureAwait(true);
            },
            StartupErrorAction = exception => provider.GetRequiredService<IGlobalExceptionHandler>()
                .HandleAsync(exception, "application startup"),
        });
        return services;
    }
}
