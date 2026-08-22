using RemoteFlow.Domain.Enums;

namespace RemoteFlow.Application.Abstractions.Backup;

public static class BackupFormat
{
    public const int CurrentVersion = 1;

    public const string ManifestEntry = "manifest.json";
    public const string ConnectionsEntry = "connections.json";
    public const string FoldersEntry = "folders.json";
    public const string TagsEntry = "tags.json";
    public const string ConnectionTagsEntry = "connection-tags.json";
    public const string SettingsEntry = "settings.json";
    public const string HostKeysEntry = "host-keys.json";
    public const string CredentialsEntry = "credentials.enc";

    public static IReadOnlyList<string> PlaintextEntries { get; } =
    [
        ManifestEntry,
        ConnectionsEntry,
        FoldersEntry,
        TagsEntry,
        ConnectionTagsEntry,
        SettingsEntry,
        HostKeysEntry,
    ];

    public static IReadOnlyDictionary<string, string> DomainEntityCoverage { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Connection"] = ConnectionsEntry,
            ["Folder"] = FoldersEntry,
            ["Tag"] = TagsEntry,
            ["ConnectionTag"] = ConnectionTagsEntry,
            ["Setting"] = SettingsEntry,
            ["HostKey"] = HostKeysEntry,
            ["RecentConnection"] = "Excluded: transient navigation history is not user configuration.",
        };
}

public sealed record BackupManifest(
    int FormatVersion,
    string AppVersion,
    DateTimeOffset CreatedUtc,
    string? MachineName,
    BackupEntityCounts Counts,
    bool IncludesCredentials,
    BackupCredentialKdf? CredentialKdf = null);

public sealed record BackupEntityCounts(
    int Connections,
    int Folders,
    int Tags,
    int ConnectionTags,
    int Settings,
    int HostKeys);

public sealed record BackupCredentialKdf(
    string Algorithm,
    int M,
    int T,
    int P,
    string Salt);

public sealed record BackupArchive(
    BackupManifest Manifest,
    IReadOnlyList<BackupConnection> Connections,
    IReadOnlyList<BackupFolder> Folders,
    IReadOnlyList<BackupTag> Tags,
    IReadOnlyList<BackupConnectionTag> ConnectionTags,
    IReadOnlyList<BackupSetting> Settings,
    IReadOnlyList<BackupHostKey> HostKeys,
    byte[]? EncryptedCredentials = null)
{
    public byte[]? ManifestHash { get; init; }

    public BackupEntityCounts ActualCounts => new(
        Connections.Count,
        Folders.Count,
        Tags.Count,
        ConnectionTags.Count,
        Settings.Count,
        HostKeys.Count);
}

public sealed record BackupConnection(
    Guid Id,
    string Name,
    string Host,
    int Port,
    ProtocolType Protocol,
    string? Username,
    AuthMethod AuthMethod,
    string? Notes,
    Guid? FolderId,
    bool IsFavorite,
    EnvironmentKind Environment,
    string? ColorOverrideHex,
    int? SortOrder,
    Guid ConcurrencyStamp,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ModifiedUtc,
    BackupCredentialReference Credential,
    BackupSshOptions Ssh,
    BackupSftpOptions Sftp,
    BackupRdpOptions Rdp,
    // Optional with a default, not a required parameter: it keeps the committed v1 golden archive
    // importing, and it keeps every existing caller compiling. A v1 archive has no objectStorage field,
    // and a connection read from one gets the defaults.
    BackupObjectStorageOptions? ObjectStorage = null);

public sealed record BackupCredentialReference(
    CredentialKind Kind,
    string StoreKey,
    string StoreProvider,
    DateTimeOffset? UpdatedUtc);

public sealed record BackupSshOptions(
    int? KeepAliveSeconds,
    string TerminalType,
    string? PrivateKeyPath,
    string? InitialCommand,
    string? StartupDirectory,
    HostKeyPolicy HostKeyPolicy,
    bool RequestPty);

public sealed record BackupSftpOptions(
    string? RemoteRootPath,
    string? LocalDownloadPath,
    bool PreserveTimestamps,
    bool ShowHiddenFiles);

public sealed record BackupObjectStorageOptions(
    string? Region,
    string? ServiceUrl,
    bool UsePathStyleAddressing,
    string? Container,
    string? RootPrefix,
    string? LocalDownloadPath);

public sealed record BackupRdpOptions(
    string? Domain,
    bool FullScreen,
    int? Width,
    int? Height,
    bool Multimon,
    bool RedirectClipboard,
    bool RedirectDrives);

public sealed record BackupFolder(
    Guid Id,
    string Name,
    Guid? ParentId,
    string Path,
    int Depth,
    int SortOrder,
    bool IsExpanded,
    Guid ConcurrencyStamp,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ModifiedUtc);

public sealed record BackupTag(Guid Id, string Name, string? ColorHex, DateTimeOffset CreatedUtc);

public sealed record BackupConnectionTag(Guid ConnectionId, Guid TagId);

public sealed record BackupSetting(string Key, string Value, DateTimeOffset ModifiedUtc);

public sealed record BackupHostKey(
    Guid Id,
    string Host,
    int Port,
    string KeyAlgorithm,
    string PublicKeyBase64,
    string Sha256Fingerprint,
    HostKeyTrust TrustState,
    HostKeySource Source,
    string? Comment,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastSeenUtc);

public interface IBackupArchiveSerializer
{
    Task WriteAsync(string path, BackupArchive archive, CancellationToken cancellationToken = default);

    Task<BackupArchive> ReadAsync(string path, CancellationToken cancellationToken = default);
}

public sealed class BackupArchiveException : Exception
{
    public BackupArchiveException(string message)
        : base(message)
    {
    }

    public BackupArchiveException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
