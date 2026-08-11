using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Ssh;
using RemoteFlow.Application.Abstractions.Sftp;
using RemoteFlow.Infrastructure.Diagnostics;
using RemoteFlow.Infrastructure.Platform;
using RemoteFlow.Infrastructure.Platform.Rdp;
using RemoteFlow.Infrastructure.Pty;
using RemoteFlow.Infrastructure.Security;
using RemoteFlow.Infrastructure.Ssh;
using RemoteFlow.Infrastructure.Ssh.Auth;
using RemoteFlow.Infrastructure.Updates;
using RemoteFlow.Infrastructure.Security.Crypto;
using RemoteFlow.Application.Abstractions.Backup;
using RemoteFlow.Infrastructure.Backup;

namespace RemoteFlow.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddRemoteFlowInfrastructure(
        this IServiceCollection services,
        IAppPaths appPaths)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(appPaths);

        services.TryAddSingleton<IAppPaths>(appPaths);
        services.TryAddSingleton<IBackupArchiveSerializer, ZipBackupArchiveSerializer>();
        services.TryAddSingleton<IBackupCredentialProtector, CredentialEnvelope>();
        services.TryAddSingleton<ISecureRandom, SecureRandom>();
        services.TryAddSingleton<IPtyService, PortaPtyService>();
        services.TryAddSingleton<ISystemPlatform, SystemPlatform>();
        services.TryAddSingleton<IProcessRunner, ProcessRunner>();
        services.TryAddSingleton<ISystemTerminalLauncher, SystemTerminalLauncher>();
        services.TryAddSingleton<IRdpLauncher>(provider =>
        {
            var platform = provider.GetRequiredService<ISystemPlatform>();
            return platform.OperatingSystem == OperatingSystemFamily.Windows
                ? new WindowsRdpLauncher(
                    platform,
                    provider.GetRequiredService<IProcessRunner>(),
                    provider.GetRequiredService<IAppPaths>(),
                    provider.GetRequiredService<IClock>(),
                    provider.GetServices<ICredentialProvider>())
                : new UnsupportedRdpLauncher(platform);
        });
        services.TryAddSingleton<IFileEditorLauncher, FileEditorLauncher>();
        services.TryAddSingleton<IFileRevealService, FileRevealService>();
        services.TryAddSingleton<IShellOpenService, ShellOpenService>();
        services.TryAddSingleton<ILastErrorStore, LastErrorStore>();
        // Singleton because it owns an HttpClient, which is meant to be long-lived. It opens no
        // connection until something asks it to check.
        services.TryAddSingleton<IUpdateChecker, GitHubUpdateChecker>();
        services.TryAddSingleton<IAppInstallInfo, AppInstallInfo>();
        // Also an HttpClient owner, and separate from the checker's on purpose: this one has no overall
        // timeout, because a ninety-megabyte download over a slow link is not a failure.
        services.TryAddSingleton<ReleaseAssetDownloader>();
        services.TryAddSingleton<IUpdateInstaller, UpdateInstaller>();
        services.TryAddSingleton<IWatchedFileMonitor, WatchedFileMonitor>();
        services.TryAddSingleton<TmdsSshTransport>();
        services.TryAddSingleton<SshNetTransport>();
        services.TryAddSingleton<ISshTransport, ConfiguredSshTransport>();
        services.TryAddSingleton<INetworkChangeMonitor, NetworkChangeMonitor>();
        services.TryAddSingleton<ISshAgentDiscovery, SshAgentDiscovery>();
        services.TryAddSingleton<ISshKeyService, SshKeyService>();
        services.TryAddSingleton<ISshAuthenticationMaterialProvider, SshAuthenticationMaterialProvider>();
        services.TryAddSingleton<ISecretRegistry, SecretRegistry>();
        services.TryAddSingleton<IGlobalExceptionHandler, GlobalExceptionHandler>();
        services.TryAddSingleton<WindowsCredentialProvider>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICredentialProvider, WindowsCredentialProvider>());
        services.TryAddSingleton<MacOsKeychainProvider>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICredentialProvider, MacOsKeychainProvider>());
        services.TryAddSingleton<IPassphraseKdf, Argon2idPassphraseKdf>();
        services.TryAddSingleton<IAuthenticatedCipher, AesGcmAuthenticatedCipher>();
        services.TryAddSingleton<CredentialSecurityState>();
        services.TryAddSingleton<EncryptedFileVaultProvider>();
        _ = services.AddSingleton<ICredentialProvider>(provider =>
            provider.GetRequiredService<EncryptedFileVaultProvider>());
        services.TryAddSingleton<LibSecretProvider>();
        _ = services.AddSingleton<ICredentialProvider>(provider =>
            provider.GetRequiredService<LibSecretProvider>());
        services.TryAddSingleton<CredentialProviderSelector>();
        services.TryAddSingleton<ICredentialProviderSelector>(provider =>
            provider.GetRequiredService<CredentialProviderSelector>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, RedactingLoggerProvider>());
        return services;
    }
}
