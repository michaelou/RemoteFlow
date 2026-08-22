using System.Runtime.CompilerServices;
using RemoteFlow.Application.Abstractions.Sftp;
using RemoteFlow.Application.Abstractions.Storage;

namespace RemoteFlow.Application.Services;

/// <summary>A bucket or container behind the browser port.
///
/// Paging is passed straight through as a continuation token rather than hidden behind an
/// <see cref="IAsyncEnumerable{T}"/>, because the page boundary is exactly what the pane has to show as
/// "Load more" and be able to stop at, and it maps one to one onto both providers' listing calls.</summary>
public sealed class ObjectStorageFileBrowserSource(
    IObjectStorageService storage,
    string displayName,
    string? rootPath = null) : IFileBrowserSource
{
    private readonly IObjectStorageService _storage = storage ??
        throw new ArgumentNullException(nameof(storage));

    public string DisplayName { get; } = displayName;

    public string RootPath { get; } = string.IsNullOrWhiteSpace(rootPath)
        ? ObjectStoragePath.Root
        : ObjectStoragePath.Normalize(rootPath);

    /// <summary>False, and not faked. S3 has no rename: the closest primitive is a server-side copy
    /// billed per byte and capped at 5 GiB, followed by a delete, on a feature whose premise is
    /// multi-gigabyte objects.</summary>
    public bool SupportsRename => false;

    public bool SupportsHiddenEntries => false;

    public string Combine(string parent, string name)
    {
        return ObjectStoragePath.Combine(parent, name);
    }

    public string? GetParent(string path)
    {
        var normalized = ObjectStoragePath.Normalize(path);

        // Never above the connection's configured root: a key scoped to one bucket cannot list the
        // account, and offering the walk up would produce a permission error instead of a folder.
        if (string.Equals(normalized, RootPath, StringComparison.Ordinal))
        {
            return null;
        }

        var parent = ObjectStoragePath.GetParent(normalized);
        return parent is null || !IsInsideRoot(parent) ? RootPath : parent;
    }

    public string GetName(string path)
    {
        return ObjectStoragePath.GetName(path);
    }

    public bool IsValidPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            return IsInsideRoot(ObjectStoragePath.Normalize(path));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public IReadOnlyList<FileBrowserCrumb> GetBreadcrumbs(string path)
    {
        if (!IsValidPath(path))
        {
            return [];
        }

        var normalized = ObjectStoragePath.Normalize(path);
        var crumbs = new List<FileBrowserCrumb>();
        var current = normalized;
        while (current is not null)
        {
            var parent = GetParent(current);
            crumbs.Insert(0, new FileBrowserCrumb(
                parent is null ? RootLabel() : ObjectStoragePath.GetName(current),
                current));
            current = parent;
        }

        return crumbs;
    }

    public async Task<SftpResult<FileBrowserPage>> ListAsync(
        string path,
        FileBrowserListOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidPath(path))
        {
            return SftpResult<FileBrowserPage>.Fail(
                SftpError.InvalidPath,
                $"'{path}' is outside this connection's root.");
        }

        var settings = options ?? new FileBrowserListOptions();

        // The filter narrows the request rather than the answer: both providers list by prefix and
        // neither searches by substring, so one request replaces a hundred.
        var page = await _storage.ListAsync(
            path,
            new ObjectStoragePaging
            {
                PageSize = Math.Clamp(settings.PageSize, 1, ObjectStoragePaging.MaximumPageSize),
                ContinuationToken = settings.ContinuationToken,
                NamePrefix = settings.NamePrefix,
            },
            cancellationToken).ConfigureAwait(false);
        return page.IsFailure
            ? SftpResult<FileBrowserPage>.Fail(page.Failure.Error, page.Failure.Message)
            : SftpResult<FileBrowserPage>.Success(new FileBrowserPage(
                [.. page.Value.Entries.Select(Describe)],
                page.Value.ContinuationToken));
    }

    public Task<SftpResult> CreateFolderAsync(string path, CancellationToken cancellationToken = default)
    {
        return _storage.CreateFolderAsync(path, cancellationToken);
    }

    public Task<SftpResult> DeleteAsync(FileBrowserEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return _storage.DeleteAsync(entry.Path, entry.IsDirectory, cancellationToken);
    }

    public Task<SftpResult> RenameAsync(
        string path,
        string newName,
        CancellationToken cancellationToken = default)
    {
        _ = path;
        _ = newName;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(SftpResult.Fail(
            SftpError.NotSupported,
            "Object storage has no rename. Copy the object to the new key and delete the old one."));
    }

    public async IAsyncEnumerable<FileBrowserEntry> EnumerateRecursiveAsync(
        FileBrowserEntry root,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        yield return root;
        if (!root.IsDirectory)
        {
            yield break;
        }

        string? token = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await ListAsync(
                root.Path,
                new FileBrowserListOptions { ContinuationToken = token },
                cancellationToken).ConfigureAwait(false);
            if (page.IsFailure)
            {
                yield break;
            }

            foreach (var child in page.Value.Entries)
            {
                await foreach (var descendant in EnumerateRecursiveAsync(child, cancellationToken)
                    .ConfigureAwait(false))
                {
                    yield return descendant;
                }
            }

            token = page.Value.ContinuationToken;
        }
        while (token is not null);
    }

    private static FileBrowserEntry Describe(ObjectEntry entry)
    {
        return new FileBrowserEntry(
            entry.Name,
            entry.Path,
            entry.IsDirectory,
            entry.Size,
            entry.LastModifiedUtc);
    }

    private string RootLabel()
    {
        var (container, key) = ObjectStoragePath.Split(RootPath);
        return container is null
            ? "All buckets"
            : key.Length == 0 ? container : $"{container}/{key}";
    }

    private bool IsInsideRoot(string normalized)
    {
        return RootPath == ObjectStoragePath.Root ||
            string.Equals(normalized, RootPath, StringComparison.Ordinal) ||
            normalized.StartsWith(RootPath + ObjectStoragePath.Separator, StringComparison.Ordinal);
    }
}
