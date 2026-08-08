using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Backup;
using RemoteFlow.Domain.Enums;
using RemoteFlow.Infrastructure.Security;
using RemoteFlow.Infrastructure.Security.Crypto;

namespace RemoteFlow.Infrastructure.Backup;

public sealed class CredentialEnvelope(
    IPassphraseKdf kdf,
    IAuthenticatedCipher cipher,
    ISecureRandom random,
    ICredentialProviderSelector providerSelector,
    IEnumerable<ICredentialProvider> providers) : IBackupCredentialProtector
{
    private const string _algorithm = "argon2id";
    private const string _magic = "RemoteFlowCredentials";
    private const int _version = 1;
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        // Indented output follows Environment.NewLine unless told otherwise, which would make an
        // envelope written on Windows differ byte for byte from the same envelope written on Linux or
        // macOS. The backup format is an interchange format: pin the line ending.
        NewLine = "\n",
    };
    private readonly IReadOnlyList<ICredentialProvider> _providers = [.. providers];

    public BackupCredentialKdf CreateKdfParameters()
    {
        return new BackupCredentialKdf(
            _algorithm,
            64 * 1024,
            3,
            1,
            Convert.ToBase64String(random.GetBytes(16)));
    }

    public async Task<byte[]> EncryptAsync(
        IReadOnlyList<BackupConnection> connections,
        BackupManifest manifest,
        ReadOnlyMemory<char> passphrase,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(manifest);
        if (passphrase.IsEmpty)
        {
            throw new BackupCredentialException("Enter a backup passphrase.");
        }

        var parameters = ParseKdf(manifest);
        var salt = Convert.FromBase64String(manifest.CredentialKdf!.Salt);
        var key = kdf.DeriveKey(passphrase.Span, salt, parameters, cipher.KeySize);
        var manifestHash = HashManifest(manifest);
        try
        {
            var records = new List<CredentialRecord>();
            foreach (var connection in connections.Where(item => item.Credential.Kind != CredentialKind.None))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var provider = _providers.FirstOrDefault(item => item.IsAvailable &&
                    string.Equals(item.Name, connection.Credential.StoreProvider, StringComparison.Ordinal));
                if (provider is null)
                {
                    continue;
                }

                using var secret = await provider.GetAsync(connection.Credential.StoreKey, cancellationToken)
                    .ConfigureAwait(false);
                if (secret is null)
                {
                    continue;
                }

                var plaintext = new byte[Encoding.UTF8.GetByteCount(secret.Secret.Span)];
                byte[]? nonce = null;
                byte[]? ciphertext = null;
                byte[]? tag = null;
                try
                {
                    _ = Encoding.UTF8.GetBytes(secret.Secret.Span, plaintext);
                    nonce = random.GetBytes(cipher.NonceSize);
                    ciphertext = new byte[plaintext.Length];
                    tag = new byte[cipher.TagSize];
                    cipher.Encrypt(
                        key,
                        nonce,
                        plaintext,
                        BuildAssociatedData(connection.Id, manifestHash),
                        ciphertext,
                        tag);
                    records.Add(new CredentialRecord(
                        connection.Id,
                        connection.Credential.Kind,
                        Convert.ToBase64String(nonce),
                        Convert.ToBase64String(ciphertext),
                        Convert.ToBase64String(tag)));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                    if (nonce is not null)
                    {
                        CryptographicOperations.ZeroMemory(nonce);
                    }

                    if (ciphertext is not null)
                    {
                        CryptographicOperations.ZeroMemory(ciphertext);
                    }

                    if (tag is not null)
                    {
                        CryptographicOperations.ZeroMemory(tag);
                    }
                }
            }

            return JsonSerializer.SerializeToUtf8Bytes(new EnvelopeDocument(_magic, _version, records), _jsonOptions);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(salt);
        }
    }

    public async Task<IPreparedCredentialImport> PrepareImportAsync(
        byte[] encryptedCredentials,
        BackupManifest manifest,
        byte[]? sourceManifestHash,
        IReadOnlyList<BackupConnection> connections,
        ReadOnlyMemory<char> passphrase,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(encryptedCredentials);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(connections);
        if (passphrase.IsEmpty)
        {
            throw UnlockFailure();
        }

        var secrets = new List<PreparedSecret>();
        byte[]? key = null;
        byte[]? salt = null;
        try
        {
            var document = JsonSerializer.Deserialize<EnvelopeDocument>(encryptedCredentials, _jsonOptions);
            if (document is null || document.Magic != _magic || document.Version != _version)
            {
                throw new CryptographicException();
            }

            var parameters = ParseKdf(manifest);
            salt = Convert.FromBase64String(manifest.CredentialKdf!.Salt);
            key = kdf.DeriveKey(passphrase.Span, salt, parameters, cipher.KeySize);
            var manifestHash = sourceManifestHash ?? HashManifest(manifest);
            var provider = await providerSelector.SelectAsync(cancellationToken).ConfigureAwait(false);
            var connectionMap = connections.ToDictionary(item => item.Id);
            foreach (var record in document.Records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!connectionMap.TryGetValue(record.ConnectionId, out var connection) ||
                    record.Kind == CredentialKind.None ||
                    record.Kind != connection.Credential.Kind)
                {
                    throw new CryptographicException();
                }

                var nonce = Convert.FromBase64String(record.Nonce);
                var ciphertext = Convert.FromBase64String(record.Ciphertext);
                var tag = Convert.FromBase64String(record.Tag);
                var plaintext = new byte[ciphertext.Length];
                try
                {
                    cipher.Decrypt(
                        key,
                        nonce,
                        ciphertext,
                        tag,
                        BuildAssociatedData(record.ConnectionId, manifestHash),
                        plaintext);
                    var chars = Encoding.UTF8.GetChars(plaintext);
                    var storeKey = CredentialStoreKeys.ForConnection(record.ConnectionId, record.Kind);
                    secrets.Add(new PreparedSecret(
                        record.ConnectionId,
                        record.Kind,
                        storeKey,
                        connection.Name,
                        chars));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                    CryptographicOperations.ZeroMemory(nonce);
                    CryptographicOperations.ZeroMemory(ciphertext);
                    CryptographicOperations.ZeroMemory(tag);
                }
            }

            return new PreparedCredentialImport(provider, secrets);
        }
        catch (OperationCanceledException)
        {
            DisposeSecrets(secrets);
            throw;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or CryptographicException or ArgumentException)
        {
            DisposeSecrets(secrets);
            throw UnlockFailure(exception);
        }
        finally
        {
            if (key is not null)
            {
                CryptographicOperations.ZeroMemory(key);
            }

            if (salt is not null)
            {
                CryptographicOperations.ZeroMemory(salt);
            }
        }
    }

    private static PassphraseKdfParameters ParseKdf(BackupManifest manifest)
    {
        var value = manifest.CredentialKdf;
        if (value is null || !string.Equals(value.Algorithm, _algorithm, StringComparison.OrdinalIgnoreCase))
        {
            throw new CryptographicException();
        }

        var parameters = new PassphraseKdfParameters(value.M, value.T, value.P);
        parameters.Validate();
        return parameters;
    }

    private static byte[] HashManifest(BackupManifest manifest)
    {
        return SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(manifest, _jsonOptions));
    }

    private static byte[] BuildAssociatedData(Guid connectionId, byte[] manifestHash)
    {
        var associatedData = new byte[manifestHash.Length + 16];
        manifestHash.CopyTo(associatedData, 0);
        _ = connectionId.TryWriteBytes(associatedData.AsSpan(manifestHash.Length));
        return associatedData;
    }

    private static BackupCredentialException UnlockFailure(Exception? inner = null)
    {
        const string message = "The encrypted credentials could not be unlocked. Check the passphrase and archive integrity.";
        return inner is null ? new BackupCredentialException(message) : new BackupCredentialException(message, inner);
    }

    private static void DisposeSecrets(IEnumerable<PreparedSecret> secrets)
    {
        foreach (var secret in secrets)
        {
            secret.Dispose();
        }
    }

    private sealed record EnvelopeDocument(string Magic, int Version, IReadOnlyList<CredentialRecord> Records);

    private sealed record CredentialRecord(
        Guid ConnectionId,
        CredentialKind Kind,
        string Nonce,
        string Ciphertext,
        string Tag);

    private sealed class PreparedCredentialImport(
        ICredentialProvider provider,
        IReadOnlyList<PreparedSecret> secrets) : IPreparedCredentialImport
    {
        private readonly ICredentialProvider _provider = provider;
        private readonly IReadOnlyList<PreparedSecret> _secrets = secrets;
        private readonly List<string> _storedKeys = [];
        private bool _disposed;

        public IReadOnlyDictionary<Guid, BackupCredentialReference> References { get; } = secrets.ToDictionary(
            item => item.ConnectionId,
            item => new BackupCredentialReference(item.Kind, item.StoreKey, provider.Name, DateTimeOffset.UtcNow));

        public async Task StoreAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var secret in _secrets)
            {
                _storedKeys.Add(secret.StoreKey);
                await _provider.SetAsync(
                    secret.StoreKey,
                    secret.Value,
                    secret.DisplayName,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            foreach (var key in _storedKeys)
            {
                await _provider.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
            }

            _storedKeys.Clear();
        }

        public ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                DisposeSecrets(_secrets);
                _disposed = true;
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class PreparedSecret(
        Guid connectionId,
        CredentialKind kind,
        string storeKey,
        string displayName,
        char[] value) : IDisposable
    {
        public Guid ConnectionId { get; } = connectionId;
        public CredentialKind Kind { get; } = kind;
        public string StoreKey { get; } = storeKey;
        public string DisplayName { get; } = displayName;
        public ReadOnlyMemory<char> Value => value;

        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(value.AsSpan()));
        }
    }
}
