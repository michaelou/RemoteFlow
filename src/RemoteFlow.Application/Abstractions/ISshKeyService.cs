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
    Task<SshKeyInspection> InspectAsync(
        string path,
        ReadOnlyMemory<char> passphrase = default,
        CancellationToken cancellationToken = default);

    Task<SshKeyInspection> GenerateEd25519Async(
        string path,
        string comment,
        CancellationToken cancellationToken = default);
}
