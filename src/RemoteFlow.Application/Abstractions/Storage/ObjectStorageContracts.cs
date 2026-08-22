using RemoteFlow.Application.Abstractions.Sftp;

namespace RemoteFlow.Application.Abstractions.Storage;

/// <summary>What one row in an object-storage listing is. Directory-ness is carried here and nowhere else
/// — never by a trailing slash on the path, which at the key level means something different.</summary>
public enum ObjectEntryKind
{
    /// <summary>A bucket or a container, listed only at the account root.</summary>
    Container = 1,

    /// <summary>A common prefix, presented as a folder.</summary>
    Prefix = 2,

    /// <summary>An object. Named for what both providers' own documentation calls it; the analyser's
    /// objection to a member named after <c>System.Object</c> does not apply to a storage vocabulary.
    /// </summary>
#pragma warning disable CA1720
    Object = 3,
#pragma warning restore CA1720
}

public sealed record ObjectEntry(
    string Name,
    string Path,
    ObjectEntryKind Kind,
    long Size,
    DateTimeOffset? LastModifiedUtc,
    string? ETag)
{
    public bool IsDirectory => Kind is ObjectEntryKind.Container or ObjectEntryKind.Prefix;
}

/// <summary>How much of a listing to ask for and where to resume. Object stores page, and a bucket with a
/// million keys is ordinary, so paging is part of the contract rather than something a caller discovers.
/// </summary>
public sealed record ObjectStoragePaging
{
    public const int DefaultPageSize = 1_000;
    public const int MaximumPageSize = 5_000;

    public int PageSize { get; init; } = DefaultPageSize;

    public string? ContinuationToken { get; init; }
}

/// <summary>One page of a listing. <see cref="ContinuationToken"/> is null on the last page.</summary>
public sealed record ObjectStoragePage(IReadOnlyList<ObjectEntry> Entries, string? ContinuationToken = null);

/// <summary>Which bytes of an object to read, and — for a resumed transfer — the ETag they must still
/// belong to. A mismatch comes back as <see cref="SftpError.PreconditionFailed"/>, which means "the object
/// changed under you, restart", not "it is missing" or "you may not have it".</summary>
public sealed record ObjectReadOptions
{
    public long Offset { get; init; }

    public long? Length { get; init; }

    public string? IfMatchETag { get; init; }
}

/// <summary>A multipart or block upload in progress.
///
/// Part content arrives as a factory rather than as a <see cref="Stream"/> because a retried part needs a
/// fresh stream: a failed <c>UploadPart</c> has already consumed the one it was handed. The three limits
/// are the provider's real ones, so the caller can size parts without knowing which provider it has.
/// Neither adapter buffers a whole part.</summary>
public interface IObjectUpload : IAsyncDisposable
{
    long MinimumPartSize { get; }

    long MaximumPartSize { get; }

    int MaximumPartCount { get; }

    Task<SftpResult> UploadPartAsync(
        int partNumber,
        long length,
        Func<CancellationToken, ValueTask<Stream>> content,
        CancellationToken cancellationToken = default);

    Task<SftpResult<ObjectEntry>> CompleteAsync(CancellationToken cancellationToken = default);

    /// <summary>Best effort, and never throws. Permitted to be a no-op: Azure has no abort call, because
    /// uncommitted blocks expire after seven days and are not billed.</summary>
    Task<SftpResult> AbortAsync(CancellationToken cancellationToken = default);
}

/// <summary>The object-storage counterpart of <see cref="ISftpService"/>, and deliberately not an
/// implementation of it: <c>SftpPublisher</c>'s atomic publish needs <c>RenameAsync</c> and
/// <c>SetPermissionsAsync</c>, and faking those over S3 would turn every publish into a server-side
/// <c>CopyObject</c> — billed per byte and capped at 5 GiB — on a feature whose premise is multi-gigabyte
/// objects.
///
/// Paths are rooted at the account: <c>/</c> lists buckets or containers, <c>/mybucket</c> is a container
/// root, and <c>/mybucket/logs/2026</c> is a prefix inside it. The result types are the SFTP ones. Their
/// names lie; their semantics do not, and they appear 229 times across 17 files — see ADR-0019.</summary>
public interface IObjectStorageService : IAsyncDisposable
{
    Task<SftpResult<ObjectStoragePage>> ListAsync(
        string path,
        ObjectStoragePaging? paging = null,
        CancellationToken cancellationToken = default);

    Task<SftpResult<ObjectEntry?>> StatAsync(string path, CancellationToken cancellationToken = default);

    Task<SftpResult> CreateFolderAsync(string path, CancellationToken cancellationToken = default);

    Task<SftpResult> DeleteAsync(string path, bool recursive, CancellationToken cancellationToken = default);

    Task<SftpResult<Stream>> OpenReadAsync(
        string path,
        ObjectReadOptions? options = null,
        CancellationToken cancellationToken = default);

    Task<SftpResult<ObjectEntry>> WriteAsync(
        string path,
        Stream content,
        long? length = null,
        CancellationToken cancellationToken = default);

    Task<SftpResult<IObjectUpload>> StartUploadAsync(string path, CancellationToken cancellationToken = default);
}
