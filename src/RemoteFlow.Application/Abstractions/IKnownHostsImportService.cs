namespace RemoteFlow.Application.Abstractions;

public sealed record KnownHostImportEntry(
    int LineNumber,
    string Host,
    int Port,
    string KeyAlgorithm,
    string PublicKeyBase64,
    string Sha256Fingerprint,
    string? Comment,
    bool IsHashed,
    bool IsRevoked)
{
    public string DisplayHost => IsHashed ? "Hashed hostname (OpenSSH privacy entry)" : Host;
}

public sealed record KnownHostsImportPreview(
    string SourcePath,
    IReadOnlyList<KnownHostImportEntry> Entries,
    IReadOnlyList<string> Warnings);

public sealed record KnownHostsImportResult(int Added, int Skipped);

public interface IKnownHostsImportService
{
    Task<KnownHostsImportPreview> PreviewAsync(
        string path,
        CancellationToken cancellationToken = default);

    Task<KnownHostsImportResult> ApplyAsync(
        KnownHostsImportPreview preview,
        CancellationToken cancellationToken = default);
}
