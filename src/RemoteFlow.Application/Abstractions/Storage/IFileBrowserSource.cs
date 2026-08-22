using RemoteFlow.Application.Abstractions.Sftp;

namespace RemoteFlow.Application.Abstractions.Storage;

/// <summary>One row in a browser pane. Deliberately not <see cref="ObjectEntry"/> or
/// <see cref="RemoteFileInfo"/>: the pane serves both, and a union of the two would carry a Unix mode for
/// an S3 key and an ETag for a file on disk.</summary>
public sealed record FileBrowserEntry(
    string Name,
    string Path,
    bool IsDirectory,
    long Size,
    DateTimeOffset? Modified);

/// <summary>One segment of the path bar. The source builds these because the shape differs:
/// <c>C:\Users\andreas</c> and <c>media-prod/2024</c> do not split the same way.</summary>
public sealed record FileBrowserCrumb(string Label, string Path);

/// <summary>One page of a listing.
///
/// <see cref="ContinuationToken"/> is null on the last page. <see cref="Warning"/> carries the case a
/// blank pane would hide: a directory that threw part-way through enumeration still returns the entries
/// it managed to read, and says why the rest are missing.</summary>
public sealed record FileBrowserPage(
    IReadOnlyList<FileBrowserEntry> Entries,
    string? ContinuationToken = null,
    string? Warning = null);

public sealed record FileBrowserListOptions
{
    public const int DefaultPageSize = 1_000;

    public string? ContinuationToken { get; init; }

    public int PageSize { get; init; } = DefaultPageSize;

    public bool ShowHidden { get; init; }

    /// <summary>Narrows the listing at the source. Both object-storage providers support a prefix and
    /// neither supports a substring search, so this is a prefix everywhere — including locally, where the
    /// pane must not behave differently.</summary>
    public string? NamePrefix { get; init; }
}

/// <summary>What one pane browses. Path handling lives here rather than in the pane, which is what lets
/// one pane class serve <c>C:\Users\andreas</c> and <c>media-prod/2024/</c> without a single
/// source-specific branch — and why the SFTP pane's <c>path[0] == '/'</c> rule is not carried over: it
/// rejects every Windows local path.</summary>
public interface IFileBrowserSource
{
    /// <summary>What the pane's header shows: "This computer", or <c>s3://media-prod</c>.</summary>
    string DisplayName { get; }

    string RootPath { get; }

    /// <summary>The places this source can be rooted at, for a pane to offer as a picker: the ready drives
    /// on Windows, <c>/</c> on Unix, and nothing at all for object storage, where the connection already
    /// pins the root. Re-read rather than cached, because a drive can be plugged in.</summary>
    IReadOnlyList<FileBrowserCrumb> GetRoots();

    /// <summary>False on object storage. S3 has no rename — it is a billed, size-capped server-side copy
    /// plus a delete — so the source does not fake one and the pane hides the affordance.</summary>
    bool SupportsRename { get; }

    /// <summary>False on object storage, where every key is visible and there is no hidden attribute to
    /// filter on.</summary>
    bool SupportsHiddenEntries { get; }

    string Combine(string parent, string name);

    string? GetParent(string path);

    string GetName(string path);

    bool IsValidPath(string path);

    IReadOnlyList<FileBrowserCrumb> GetBreadcrumbs(string path);

    Task<SftpResult<FileBrowserPage>> ListAsync(
        string path,
        FileBrowserListOptions? options = null,
        CancellationToken cancellationToken = default);

    Task<SftpResult> CreateFolderAsync(string path, CancellationToken cancellationToken = default);

    Task<SftpResult> DeleteAsync(FileBrowserEntry entry, CancellationToken cancellationToken = default);

    Task<SftpResult> RenameAsync(string path, string newName, CancellationToken cancellationToken = default);

    /// <summary>Every entry below a folder, the pages joined up. Paging is user-visible when browsing and
    /// invisible here, which is why the port takes a continuation token and this sits on top of it: a
    /// delete plan and a folder-transfer count have to walk the whole thing, and stop early when the user
    /// cancels the confirmation.</summary>
    IAsyncEnumerable<FileBrowserEntry> EnumerateRecursiveAsync(
        FileBrowserEntry root,
        CancellationToken cancellationToken = default);
}
