using RemoteFlow.Application.Abstractions;

namespace RemoteFlow.Infrastructure.Security;

public enum CredentialPlatform
{
    Windows,
    MacOS,
    Linux,
}

public sealed class CredentialProviderSelector(
    ISettingsStore settingsStore,
    IEnumerable<ICredentialProvider> providers,
    CredentialPlatform? platform = null,
    CredentialSecurityState? securityState = null)
{
    private readonly IReadOnlyList<ICredentialProvider> _providers = [.. providers];
    private readonly CredentialPlatform _platform = platform ?? CurrentPlatform();

    public async Task<ICredentialProvider> SelectAsync(CancellationToken cancellationToken = default)
    {
        var forceFileVault = await settingsStore.Get(SettingKeys.ForceFileVault, cancellationToken).ConfigureAwait(false);
        securityState?.SetKeyringUnavailable(false);
        var desiredName = forceFileVault
            ? "file-vault"
            : _platform switch
            {
                CredentialPlatform.Windows => "windows-credman",
                CredentialPlatform.MacOS => "macos-keychain",
                CredentialPlatform.Linux => "libsecret",
                _ => throw new InvalidOperationException("The credential platform is unsupported."),
            };

        var preferred = _providers.FirstOrDefault(provider =>
            provider.IsAvailable && string.Equals(provider.Name, desiredName, StringComparison.Ordinal));
        if (preferred is not null)
        {
            return preferred;
        }

        var fallback = _providers.FirstOrDefault(provider =>
            provider.IsAvailable && string.Equals(provider.Name, "file-vault", StringComparison.Ordinal));
        if (!forceFileVault && _platform == CredentialPlatform.Linux && fallback is not null)
        {
            securityState?.SetKeyringUnavailable(true);
        }

        return fallback ?? throw new CredentialProviderException(
            $"Credential provider '{desiredName}' is unavailable and no file vault is configured.");
    }

    private static CredentialPlatform CurrentPlatform()
    {
        return OperatingSystem.IsWindows()
            ? CredentialPlatform.Windows
            : OperatingSystem.IsMacOS()
                ? CredentialPlatform.MacOS
                : OperatingSystem.IsLinux()
                    ? CredentialPlatform.Linux
                    : throw new PlatformNotSupportedException("RemoteFlow supports Windows, macOS, and Linux.");
    }
}
