namespace RemoteFlow.Application.Abstractions;

public enum SshPrivateKeyFormat
{
    Unknown = 0,
    OpenSsh = 1,
    Pkcs8 = 2,
    Pem = 3,
    PuttyPpk = 4,
}

public sealed record SshKeyInspection(
    string Path,
    SshPrivateKeyFormat Format,
    bool IsEncrypted,
    string? KeyType,
    string? Sha256Fingerprint,
    string? Comment,
    string? PublicKeyText);

public sealed class SshKeyFormatException(string message) : IOException(message);

public interface ISshKeyService
{
    /// <summary>The conventional OpenSSH key directory for the current user (<c>~/.ssh</c>).</summary>
    string DefaultKeyDirectory { get; }

    Task<SshKeyInspection> InspectAsync(
        string path,
        ReadOnlyMemory<char> passphrase = default,
        CancellationToken cancellationToken = default);

    Task<SshKeyInspection> GenerateEd25519Async(
        string path,
        string comment,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the private keys already present in <see cref="DefaultKeyDirectory" /> so a connection
    /// can be pointed at one without browsing the file system.
    /// </summary>
    Task<IReadOnlyList<SshKeyInspection>> DiscoverAsync(CancellationToken cancellationToken = default);

    /// <summary>Writes pasted private-key text to <paramref name="path" /> and inspects the result.</summary>
    Task<SshKeyInspection> ImportAsync(
        string path,
        string privateKeyText,
        CancellationToken cancellationToken = default);
}
