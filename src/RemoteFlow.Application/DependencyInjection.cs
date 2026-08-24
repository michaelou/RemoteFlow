using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Ssh;
using RemoteFlow.Application.Abstractions.Sftp;
using RemoteFlow.Application.Abstractions.Backup;
using RemoteFlow.Application.Abstractions.Storage;
using RemoteFlow.Application.Services;
using RemoteFlow.Application.Services.Backup;
using RemoteFlow.Domain.Abstractions;

namespace RemoteFlow.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddRemoteFlowApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IClock>(SystemClock.Instance);
        services.TryAddSingleton<IGuidProvider>(SystemGuidProvider.Instance);
        services.TryAddSingleton<IConnectionChangeNotifier, ConnectionChangeNotifier>();
        services.TryAddSingleton<IWorkspaceChangeNotifier, WorkspaceChangeNotifier>();
        services.TryAddSingleton<IVaultUnlockService, VaultUnlockService>();
        services.TryAddSingleton<IAutoBackupPassphraseStore, AutoBackupPassphraseStore>();
        services.TryAddSingleton<IAutoBackupStatusStore, FileAutoBackupStatusStore>();
        services.TryAddSingleton<IAutoBackupDestinationFactory, AutoBackupDestinationFactory>();
        services.TryAddSingleton<IAutoBackupRunner, AutoBackupRunner>();
        services.TryAddSingleton<IBackupService, BackupService>();
        services.TryAddSingleton<IConnectionService, ConnectionService>();
        services.TryAddSingleton<IConnectionCredentialService, ConnectionCredentialService>();
        services.TryAddSingleton<ITagService, TagService>();
        services.TryAddSingleton<IFolderService, FolderService>();
        services.TryAddSingleton<KeymapService>();
        services.TryAddSingleton<IShellProfileService, ShellProfileService>();
        services.TryAddSingleton<IHostKeyPrompt, RejectingHostKeyPrompt>();
        services.TryAddSingleton<IHostKeyVerifier, HostKeyVerifier>();
        services.TryAddSingleton<IKnownHostsImportService, KnownHostsImportService>();
        services.TryAddSingleton<ISessionManager, SessionManager>();
        services.TryAddSingleton<IEmbeddedRdpSessionProvider>(NoEmbeddedRdpSessionProvider.Instance);
        services.TryAddSingleton<IRemoteEditServiceFactory, RemoteEditServiceFactory>();
        services.TryAddSingleton<ILocalFolderMemory, SettingsLocalFolderMemory>();
        return services;
    }
}
