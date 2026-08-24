using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Infrastructure.Security.Crypto;

namespace RemoteFlow.Infrastructure.Security;

public sealed class EncryptedFileVaultProvider : ICredentialProvider, ICredentialVault, IDisposable
{
    private const int _formatVersion = 1;
    private const int _saltSize = 32;
    private const int _maximumManifestSize = 64 * 1024 * 1024;
    private static readonly byte[] _magic = "RFV1"u8.ToArray();

    private readonly IPassphraseKdf _kdf;
    private readonly IAuthenticatedCipher _cipher;
    private readonly ISecureRandom _random;
    private readonly PassphraseKdfParameters _newVaultParameters;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private VaultDocument? _document;
    private byte[]? _derivedKey;
    private GCHandle _derivedKeyHandle;
    private bool _disposed;

    public EncryptedFileVaultProvider(
        IAppPaths appPaths,
        IPassphraseKdf kdf,
        IAuthenticatedCipher cipher,
        ISecureRandom random,
        PassphraseKdfParameters? newVaultParameters = null)
    {
        ArgumentNullException.ThrowIfNull(appPaths);
        _kdf = kdf ?? throw new ArgumentNullException(nameof(kdf));
        _cipher = cipher ?? throw new ArgumentNullException(nameof(cipher));
        _random = random ?? throw new ArgumentNullException(nameof(random));
        _newVaultParameters = newVaultParameters ?? PassphraseKdfParameters.VaultDefault;
        _newVaultParameters.Validate();
        VaultPath = Path.Combine(appPaths.ConfigDirectory, "vault.rfv");
    }

    public string Name => "file-vault";

    public bool IsAvailable => true;

    public string VaultPath { get; }

    public bool IsUnlocked => _derivedKey is not null;

    /// <summary>Whether a vault file has been written yet. Unlocking a vault that does not exist creates it,
    /// so this is what separates "invent a passphrase" from "recall one".</summary>
    public bool Exists => File.Exists(VaultPath);

    internal ReadOnlyMemory<byte> KeyMemoryForTesting => _derivedKey ?? ReadOnlyMemory<byte>.Empty;

    public async Task UnlockAsync(
        ReadOnlyMemory<char> passphrase,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (passphrase.IsEmpty)
        {
            throw new ArgumentException("The vault passphrase cannot be empty.", nameof(passphrase));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var passphraseCopy = passphrase.ToArray();
        try
        {
            await Task.Run(() => UnlockCore(passphraseCopy), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(passphraseCopy.AsSpan()));
            _ = _gate.Release();
        }
    }

    /// <summary>The result-returning face of <see cref="UnlockAsync"/>, for callers in a layer that cannot
    /// name <see cref="VaultUnlockException"/>. A wrong passphrase is an ordinary thing for a person to do
    /// and should not travel as an exception.</summary>
    public async Task<VaultUnlockOutcome> TryUnlockAsync(
        ReadOnlyMemory<char> passphrase,
        CancellationToken cancellationToken = default)
    {
        if (passphrase.IsEmpty)
        {
            return VaultUnlockOutcome.IncorrectPassphrase;
        }

        try
        {
            await UnlockAsync(passphrase, cancellationToken).ConfigureAwait(false);
            return VaultUnlockOutcome.Unlocked;
        }
        catch (VaultUnlockException)
        {
            // Deliberately not separated from a corrupt or truncated vault file: authenticated decryption
            // cannot tell the two apart, and inventing a distinction would be a guess presented as fact.
            return VaultUnlockOutcome.IncorrectPassphrase;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return VaultUnlockOutcome.Failed;
        }
    }

    public async Task<SecretHandle?> GetAsync(
        string storeKey,
        CancellationToken cancellationToken = default)
    {
        ValidateStoreKey(storeKey);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = RequireDocument();
            if (!document.Records.TryGetValue(storeKey, out var record))
            {
                return null;
            }

            var associatedData = Encoding.UTF8.GetBytes(storeKey);
            var plaintext = new byte[record.Ciphertext.Length];
            try
            {
                _cipher.Decrypt(
                    RequireKey(),
                    record.Nonce,
                    record.Ciphertext,
                    record.Tag,
                    associatedData,
                    plaintext);
                var chars = new char[Encoding.UTF8.GetCharCount(plaintext)];
                _ = Encoding.UTF8.GetChars(plaintext, chars);
                try
                {
                    return new SecretHandle(chars);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(chars.AsSpan()));
                }
            }
            catch (CryptographicException)
            {
                throw new VaultUnlockException();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(associatedData);
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    public async Task SetAsync(
        string storeKey,
        ReadOnlyMemory<char> secret,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        ValidateStoreKey(storeKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var secretCopy = secret.ToArray();
        try
        {
            var document = RequireDocument();
            var plaintext = new byte[Encoding.UTF8.GetByteCount(secretCopy)];
            var associatedData = Encoding.UTF8.GetBytes(storeKey);
            var nonce = _random.GetBytes(_cipher.NonceSize);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[_cipher.TagSize];
            try
            {
                _ = Encoding.UTF8.GetBytes(secretCopy, plaintext);
                _cipher.Encrypt(RequireKey(), nonce, plaintext, associatedData, ciphertext, tag);
                document.Records[storeKey] = new VaultRecord(nonce, ciphertext, tag);
                SaveDocument(document);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
                CryptographicOperations.ZeroMemory(associatedData);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(secretCopy.AsSpan()));
            _ = _gate.Release();
        }
    }

    public async Task DeleteAsync(string storeKey, CancellationToken cancellationToken = default)
    {
        ValidateStoreKey(storeKey);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = RequireDocument();
            if (document.Records.Remove(storeKey))
            {
                SaveDocument(document);
            }
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _gate.Wait();
        try
        {
            if (_disposed)
            {
                return;
            }

            ClearDerivedKey();
            _document = null;
            _disposed = true;
        }
        finally
        {
            _ = _gate.Release();
        }

        _gate.Dispose();
    }

    private void UnlockCore(ReadOnlySpan<char> passphrase)
    {
        try
        {
            if (!File.Exists(VaultPath))
            {
                var salt = _random.GetBytes(_saltSize);
                var key = _kdf.DeriveKey(passphrase, salt, _newVaultParameters, _cipher.KeySize);
                SetDerivedKey(key);
                _document = new VaultDocument(_newVaultParameters, salt, new Dictionary<string, VaultRecord>(StringComparer.Ordinal));
                SaveDocument(_document);
                return;
            }

            var fileBytes = File.ReadAllBytes(VaultPath);
            try
            {
                var envelope = ReadEnvelope(fileBytes);
                var key = _kdf.DeriveKey(passphrase, envelope.Salt, envelope.Parameters, _cipher.KeySize);
                var manifest = new byte[envelope.ManifestCiphertext.Length];
                try
                {
                    _cipher.Decrypt(
                        key,
                        envelope.ManifestNonce,
                        envelope.ManifestCiphertext,
                        envelope.ManifestTag,
                        envelope.Header,
                        manifest);
                    var records = ReadManifest(manifest);
                    SetDerivedKey(key);
                    _document = new VaultDocument(envelope.Parameters, envelope.Salt, records);
                    key = [];
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(key);
                    CryptographicOperations.ZeroMemory(manifest);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(fileBytes);
            }
        }
        catch (VaultUnlockException)
        {
            ClearDerivedKey();
            _document = null;
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ClearDerivedKey();
            _document = null;
            throw new VaultUnlockException();
        }
    }

    private void SaveDocument(VaultDocument document)
    {
        var directory = Path.GetDirectoryName(VaultPath)
            ?? throw new InvalidOperationException("The vault path does not have a parent directory.");
        _ = Directory.CreateDirectory(directory);

        var header = WriteHeader(document.Parameters, document.Salt);
        var manifest = WriteManifest(document.Records);
        var nonce = _random.GetBytes(_cipher.NonceSize);
        var ciphertext = new byte[manifest.Length];
        var tag = new byte[_cipher.TagSize];
        byte[]? fileBytes = null;
        try
        {
            _cipher.Encrypt(RequireKey(), nonce, manifest, header, ciphertext, tag);
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(header);
                writer.Write(nonce);
                writer.Write(ciphertext.Length);
                writer.Write(ciphertext);
                writer.Write(tag);
            }

            fileBytes = stream.ToArray();
            var temporaryPath = $"{VaultPath}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllBytes(temporaryPath, fileBytes);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(temporaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }

                File.Move(temporaryPath, VaultPath, overwrite: true);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(VaultPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(header);
            CryptographicOperations.ZeroMemory(manifest);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
            if (fileBytes is not null)
            {
                CryptographicOperations.ZeroMemory(fileBytes);
            }
        }
    }

    private static byte[] WriteHeader(PassphraseKdfParameters parameters, byte[] salt)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(_magic);
            writer.Write(_formatVersion);
            writer.Write(parameters.MemorySizeKiB);
            writer.Write(parameters.Iterations);
            writer.Write(parameters.Parallelism);
            writer.Write(salt.Length);
            writer.Write(salt);
        }

        return stream.ToArray();
    }

    private static byte[] WriteManifest(IReadOnlyDictionary<string, VaultRecord> records)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(records.Count);
            foreach (var (storeKey, record) in records.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                writer.Write(storeKey);
                writer.Write(record.Nonce.Length);
                writer.Write(record.Nonce);
                writer.Write(record.Ciphertext.Length);
                writer.Write(record.Ciphertext);
                writer.Write(record.Tag.Length);
                writer.Write(record.Tag);
            }
        }

        return stream.ToArray();
    }

    private VaultEnvelope ReadEnvelope(byte[] fileBytes)
    {
        using var stream = new MemoryStream(fileBytes, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var magic = reader.ReadBytes(_magic.Length);
        if (!magic.AsSpan().SequenceEqual(_magic) || reader.ReadInt32() != _formatVersion)
        {
            throw new VaultUnlockException();
        }

        var parameters = new PassphraseKdfParameters(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
        parameters.Validate();
        var saltLength = reader.ReadInt32();
        if (saltLength is < 8 or > 1024)
        {
            throw new VaultUnlockException();
        }

        var salt = ReadExact(reader, saltLength);
        var headerLength = checked((int)stream.Position);
        var header = fileBytes[..headerLength];
        var nonce = ReadExact(reader, _cipher.NonceSize);
        var manifestLength = reader.ReadInt32();
        if (manifestLength is < 0 or > _maximumManifestSize)
        {
            throw new VaultUnlockException();
        }

        var manifest = ReadExact(reader, manifestLength);
        var tag = ReadExact(reader, _cipher.TagSize);
        return stream.Position == stream.Length
            ? new VaultEnvelope(parameters, salt, header, nonce, manifest, tag)
            : throw new VaultUnlockException();
    }

    private Dictionary<string, VaultRecord> ReadManifest(byte[] manifest)
    {
        using var stream = new MemoryStream(manifest, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var count = reader.ReadInt32();
        if (count is < 0 or > 1_000_000)
        {
            throw new VaultUnlockException();
        }

        var records = new Dictionary<string, VaultRecord>(count, StringComparer.Ordinal);
        for (var index = 0; index < count; index++)
        {
            var storeKey = reader.ReadString();
            ValidateStoreKey(storeKey);
            var nonce = ReadExact(reader, ReadBoundedLength(reader, _cipher.NonceSize));
            var ciphertext = ReadExact(reader, ReadBoundedLength(reader, _maximumManifestSize));
            var tag = ReadExact(reader, ReadBoundedLength(reader, _cipher.TagSize));
            if (nonce.Length != _cipher.NonceSize || tag.Length != _cipher.TagSize || !records.TryAdd(
                    storeKey,
                    new VaultRecord(nonce, ciphertext, tag)))
            {
                throw new VaultUnlockException();
            }
        }

        return stream.Position == stream.Length ? records : throw new VaultUnlockException();
    }

    private static int ReadBoundedLength(BinaryReader reader, int maximum)
    {
        var length = reader.ReadInt32();
        return length is >= 0 && length <= maximum ? length : throw new VaultUnlockException();
    }

    private static byte[] ReadExact(BinaryReader reader, int count)
    {
        var bytes = reader.ReadBytes(count);
        return bytes.Length == count ? bytes : throw new VaultUnlockException();
    }

    private void SetDerivedKey(byte[] key)
    {
        ClearDerivedKey();
        _derivedKey = key;
        _derivedKeyHandle = GCHandle.Alloc(_derivedKey, GCHandleType.Pinned);
    }

    private void ClearDerivedKey()
    {
        if (_derivedKey is not null)
        {
            CryptographicOperations.ZeroMemory(_derivedKey);
        }

        if (_derivedKeyHandle.IsAllocated)
        {
            _derivedKeyHandle.Free();
        }

        _derivedKey = null;
    }

    private VaultDocument RequireDocument()
    {
        return _document ?? throw new VaultLockedException();
    }

    private byte[] RequireKey()
    {
        return _derivedKey ?? throw new VaultLockedException();
    }

    private static void ValidateStoreKey(string storeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeKey);
        if (storeKey.Length > 512)
        {
            throw new ArgumentException("Credential store keys cannot exceed 512 characters.", nameof(storeKey));
        }
    }

    private sealed record VaultDocument(
        PassphraseKdfParameters Parameters,
        byte[] Salt,
        Dictionary<string, VaultRecord> Records);

    private sealed record VaultRecord(byte[] Nonce, byte[] Ciphertext, byte[] Tag);

    private sealed record VaultEnvelope(
        PassphraseKdfParameters Parameters,
        byte[] Salt,
        byte[] Header,
        byte[] ManifestNonce,
        byte[] ManifestCiphertext,
        byte[] ManifestTag);
}
