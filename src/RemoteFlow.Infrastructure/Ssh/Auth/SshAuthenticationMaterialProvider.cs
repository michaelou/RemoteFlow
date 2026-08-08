using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Ssh;
using RemoteFlow.Application.Services;
using RemoteFlow.Domain.Entities;
using RemoteFlow.Domain.Enums;

namespace RemoteFlow.Infrastructure.Ssh.Auth;

public sealed class SshAuthenticationMaterialProvider(
    IEnumerable<ICredentialProvider> credentialProviders,
    IConnectionCredentialService credentialService,
    ISshCredentialPrompt credentialPrompt,
    IKeyboardInteractivePrompt keyboardPrompt,
    ISshKeyService keyService,
    ISecretRegistry secretRegistry) : ISshAuthenticationMaterialProvider
{
    private readonly IReadOnlyList<ICredentialProvider> _credentialProviders = [.. credentialProviders];
    private readonly IConnectionCredentialService _credentialService = credentialService ?? throw new ArgumentNullException(nameof(credentialService));
    private readonly ISshCredentialPrompt _credentialPrompt = credentialPrompt ?? throw new ArgumentNullException(nameof(credentialPrompt));
    private readonly IKeyboardInteractivePrompt _keyboardPrompt = keyboardPrompt ?? throw new ArgumentNullException(nameof(keyboardPrompt));
    private readonly ISshKeyService _keyService = keyService ?? throw new ArgumentNullException(nameof(keyService));
    private readonly ISecretRegistry _secretRegistry = secretRegistry ?? throw new ArgumentNullException(nameof(secretRegistry));

    public async Task<IReadOnlyList<SshAuthMaterial>> CreateAsync(
        Connection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return connection.AuthMethod switch
        {
            AuthMethod.None => [new SshAuthMaterial.None()],
            AuthMethod.Password => [await CreatePasswordAsync(connection, cancellationToken).ConfigureAwait(false)],
            AuthMethod.PrivateKey => [await CreatePrivateKeyAsync(connection, cancellationToken).ConfigureAwait(false)],
            AuthMethod.Agent => [new SshAuthMaterial.Agent()],
            AuthMethod.KeyboardInteractive =>
            [
                new SshAuthMaterial.KeyboardInteractive(_keyboardPrompt.RespondAsync),
            ],
            AuthMethod.Certificate => throw new NotSupportedException("SSH certificates are not supported in v1."),
            AuthMethod.Kerberos => throw new NotSupportedException("Kerberos/GSSAPI is reserved but unsupported in v1."),
            _ => throw new ArgumentOutOfRangeException(nameof(connection)),
        };
    }

    private async Task<SshAuthMaterial> CreatePasswordAsync(
        Connection connection,
        CancellationToken cancellationToken)
    {
        using var secret = await GetOrPromptAsync(
            connection,
            CredentialKind.Password,
            "SSH password required",
            $"Enter the password for {connection.Username}@{connection.Host}.",
            cancellationToken).ConfigureAwait(false)
            ?? throw new OperationCanceledException("SSH password entry was cancelled.", cancellationToken);
        var value = new string(secret.Secret.Span);
        RegisterSecret(value);
        return new SshAuthMaterial.Password(value);
    }

    private async Task<SshAuthMaterial> CreatePrivateKeyAsync(
        Connection connection,
        CancellationToken cancellationToken)
    {
        var path = connection.Ssh.PrivateKeyPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("The SSH connection has no private key path.");
        }
        var inspection = await _keyService.InspectAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false);
        string? passphrase = null;
        if (inspection.IsEncrypted)
        {
            using var secret = await GetOrPromptAsync(
                connection,
                CredentialKind.PrivateKeyPassphrase,
                "Private-key passphrase required",
                $"Enter the passphrase for {Path.GetFileName(path)}. You can store it securely for future connections.",
                cancellationToken).ConfigureAwait(false)
                ?? throw new OperationCanceledException("Private-key passphrase entry was cancelled.", cancellationToken);
            passphrase = new string(secret.Secret.Span);
            RegisterSecret(passphrase);
            _ = await _keyService.InspectAsync(path, secret.Secret, cancellationToken).ConfigureAwait(false);
        }
        var keyData = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        RegisterSecret(keyData);
        return new SshAuthMaterial.PrivateKey(keyData, passphrase);
    }

    private async Task<SecretHandle?> GetOrPromptAsync(
        Connection connection,
        CredentialKind kind,
        string title,
        string message,
        CancellationToken cancellationToken)
    {
        if (!connection.Credential.IsEmpty && connection.Credential.Kind == kind)
        {
            var provider = _credentialProviders.FirstOrDefault(candidate =>
                candidate.IsAvailable &&
                string.Equals(candidate.Name, connection.Credential.StoreProvider, StringComparison.OrdinalIgnoreCase));
            if (provider is not null)
            {
                var stored = await provider.GetAsync(connection.Credential.StoreKey, cancellationToken).ConfigureAwait(false);
                if (stored is not null)
                {
                    return stored;
                }
            }
        }

        using var prompted = await _credentialPrompt.PromptAsync(
            new(title, message, kind, AllowSave: true),
            cancellationToken).ConfigureAwait(false);
        if (prompted is null)
        {
            return null;
        }
        if (prompted.Save)
        {
            var stored = await _credentialService.StoreAsync(
                connection.Id,
                kind,
                prompted.Secret.Secret,
                connection.Name,
                cancellationToken).ConfigureAwait(false);
            if (stored.IsFailure)
            {
                throw new InvalidOperationException(stored.Error.Message);
            }
        }
        return new SecretHandle(prompted.Secret.Secret.Span);
    }

    private void RegisterSecret(string secret)
    {
        if (secret.Length >= 4)
        {
            _secretRegistry.Register(secret);
        }
    }
}
