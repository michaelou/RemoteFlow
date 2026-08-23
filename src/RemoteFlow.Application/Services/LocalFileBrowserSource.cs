using System.Globalization;
using System.Runtime.CompilerServices;
using RemoteFlow.Application.Abstractions.Sftp;
using RemoteFlow.Application.Abstractions.Storage;

namespace RemoteFlow.Application.Services;

/// <summary>The local filesystem behind the browser port.
///
/// It lives in Application rather than as <c>System.IO</c> calls in the pane for two reasons. The
/// behaviour is not trivial — a mid-enumeration <see cref="UnauthorizedAccessException"/> on something
/// like <c>C:\System Volume Information</c> has to yield a partial page rather than blanking the pane —
/// and here it gets plain <c>[Fact]</c> coverage instead of an Avalonia harness. <c>System.IO</c> is the
/// base class library, so the dependency-direction tests stay green.</summary>
public sealed class LocalFileBrowserSource : IFileBrowserSource
{
    public string DisplayName => "This computer";

    public string RootPath { get; } = DefaultRoot();

    public bool SupportsRename => true;

    public bool SupportsHiddenEntries => true;

    /// <summary>The drives or the single Unix root, in the order a pane should offer them.</summary>
    public static IReadOnlyList<string> Roots()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [Path.DirectorySeparatorChar.ToString()];
        }

        try
        {
            return [.. ReadyDrives()
                .Select(drive => drive.RootDirectory.FullName)
                .Order(StringComparer.OrdinalIgnoreCase)];
        }
        catch (IOException)
        {
            return [Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\"];
        }
    }

    /// <summary>The drives, labelled the way the operating system labels them — <c>C:\ (Windows)</c> —
    /// so a pane can offer a picker. Enumerated on demand, because a drive can be plugged in while the
    /// page is open.</summary>
    public IReadOnlyList<FileBrowserCrumb> GetRoots()
    {
        if (!OperatingSystem.IsWindows())
        {
            var unix = Path.DirectorySeparatorChar.ToString();
            return [new FileBrowserCrumb(unix, unix)];
        }

        try
        {
            return [.. ReadyDrives()
                .OrderBy(drive => drive.RootDirectory.FullName, StringComparer.OrdinalIgnoreCase)
                .Select(drive => new FileBrowserCrumb(Label(drive), drive.RootDirectory.FullName))];
        }
        catch (IOException)
        {
            var fallback = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
            return [new FileBrowserCrumb(fallback, fallback)];
        }
    }

    private static DriveInfo[] ReadyDrives()
    {
        // Materialised before IsReady is asked, so one unresponsive drive throws here rather than part-way
        // through a lazy sequence the caller is already iterating.
        return [.. DriveInfo.GetDrives().Where(IsUsable)];
    }

    private static bool IsUsable(DriveInfo drive)
    {
        try
        {
            return drive.IsReady;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A mapped network drive whose server has gone away answers this way. It is not a root the
            // user can browse, and it must not take the whole picker down with it.
            return false;
        }
    }

    private static string Label(DriveInfo drive)
    {
        var root = drive.RootDirectory.FullName;
        try
        {
            var volume = drive.VolumeLabel;
            return string.IsNullOrWhiteSpace(volume) ? root : $"{root} ({volume})";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return root;
        }
    }

    public string Combine(string parent, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parent);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Path.Combine(parent, name);
    }

    /// <summary>Null at a drive root or at <c>/</c>. <see cref="Directory.GetParent(string)"/> already
    /// answers that correctly on both platforms, which is the whole reason path handling belongs to the
    /// source.</summary>
    public string? GetParent(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            return Directory.GetParent(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))?.FullName;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    public string GetName(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(full));

        // A drive root has no file name, and "" in the path bar would read as a bug.
        return name.Length == 0 ? full : name;
    }

    public bool IsValidPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            return Path.IsPathFullyQualified(path);
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

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var crumbs = new List<FileBrowserCrumb>();
        var current = full;
        while (current is not null)
        {
            var parent = GetParent(current);
            crumbs.Insert(0, new FileBrowserCrumb(parent is null ? current : GetName(current), current));
            current = parent;
        }

        return crumbs;
    }

    public Task<SftpResult<FileBrowserPage>> ListAsync(
        string path,
        FileBrowserListOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidPath(path))
        {
            return Task.FromResult(SftpResult<FileBrowserPage>.Fail(
                SftpError.InvalidPath,
                "Enter a full path, such as C:\\Users or /home."));
        }

        var settings = options ?? new FileBrowserListOptions();
        if (!Directory.Exists(path))
        {
            return Task.FromResult(SftpResult<FileBrowserPage>.Fail(
                SftpError.NotFound,
                $"'{path}' was not found."));
        }

        var entries = new List<FileBrowserEntry>();
        string? warning = null;
        try
        {
            // The enumerator is walked by hand rather than through a foreach over EnumerateFileSystemInfos,
            // because the exception arrives from MoveNext part-way along: catching it around the loop would
            // throw away every entry already read and blank the pane.
            using var enumerator = new DirectoryInfo(path)
                .EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly)
                .GetEnumerator();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (!enumerator.MoveNext())
                    {
                        break;
                    }
                }
                catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
                {
                    warning = $"Some entries in '{path}' could not be read: {exception.Message}";
                    break;
                }

                if (Describe(enumerator.Current, settings) is { } entry)
                {
                    entries.Add(entry);
                }
            }
        }
        catch (OperationCanceledException)
        {
            return Task.FromResult(SftpResult<FileBrowserPage>.Fail(
                SftpError.Cancelled,
                "The folder load was cancelled."));
        }
        catch (UnauthorizedAccessException exception)
        {
            return Task.FromResult(SftpResult<FileBrowserPage>.Fail(
                SftpError.PermissionDenied,
                $"'{path}' cannot be read: {exception.Message}"));
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException)
        {
            return Task.FromResult(SftpResult<FileBrowserPage>.Fail(
                SftpError.Unknown,
                $"'{path}' could not be listed: {exception.Message}"));
        }

        entries.Sort(static (left, right) => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(SftpResult<FileBrowserPage>.Success(Page(entries, settings, warning)));
    }

    public Task<SftpResult> CreateFolderAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsValidPath(path))
        {
            return Task.FromResult(SftpResult.Fail(SftpError.InvalidPath, "Enter a full path."));
        }

        if (Directory.Exists(path) || File.Exists(path))
        {
            return Task.FromResult(SftpResult.Fail(SftpError.AlreadyExists, $"'{path}' already exists."));
        }

        try
        {
            _ = Directory.CreateDirectory(path);
            return Task.FromResult(SftpResult.Success());
        }
        catch (UnauthorizedAccessException exception)
        {
            return Task.FromResult(SftpResult.Fail(SftpError.PermissionDenied, exception.Message));
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException)
        {
            return Task.FromResult(SftpResult.Fail(SftpError.Unknown, exception.Message));
        }
    }

    public Task<SftpResult> DeleteAsync(FileBrowserEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (entry.IsDirectory)
            {
                Directory.Delete(entry.Path, recursive: true);
            }
            else
            {
                File.Delete(entry.Path);
            }

            return Task.FromResult(SftpResult.Success());
        }
        catch (DirectoryNotFoundException)
        {
            return Task.FromResult(SftpResult.Fail(SftpError.NotFound, $"'{entry.Path}' was not found."));
        }
        catch (UnauthorizedAccessException exception)
        {
            return Task.FromResult(SftpResult.Fail(SftpError.PermissionDenied, exception.Message));
        }
        catch (IOException exception)
        {
            return Task.FromResult(SftpResult.Fail(SftpError.Unknown, exception.Message));
        }
    }

    public Task<SftpResult> RenameAsync(
        string path,
        string newName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var parent = GetParent(path);
        if (parent is null)
        {
            return Task.FromResult(SftpResult.Fail(SftpError.InvalidPath, "A drive root cannot be renamed."));
        }

        var target = Combine(parent, newName);
        if (Directory.Exists(target) || File.Exists(target))
        {
            return Task.FromResult(SftpResult.Fail(SftpError.AlreadyExists, $"'{newName}' already exists."));
        }

        try
        {
            if (Directory.Exists(path))
            {
                Directory.Move(path, target);
            }
            else
            {
                File.Move(path, target);
            }

            return Task.FromResult(SftpResult.Success());
        }
        catch (UnauthorizedAccessException exception)
        {
            return Task.FromResult(SftpResult.Fail(SftpError.PermissionDenied, exception.Message));
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException)
        {
            return Task.FromResult(SftpResult.Fail(SftpError.Unknown, exception.Message));
        }
    }

    /// <summary>Moves one file or folder into a destination directory, and answers where it landed.
    ///
    /// Not on <see cref="IFileBrowserSource"/>: moving into a prefix is a server-side copy plus a delete on
    /// object storage, billed and size-capped, and there is nothing to move <em>from</em> there anyway. This
    /// exists for the one caller that has already put bytes on disk and needs them somewhere else — the SFTP
    /// page finishing a drag whose rows it downloaded to a staging directory to build the drag payload.
    /// Doing that as a move rather than a second download is the difference between one transfer of a 4 GB
    /// file and two.
    ///
    /// An existing destination is a failure rather than an overwrite: a silent clobber of a local file the
    /// user never named is not something a drag should be able to do.</summary>
    public Task<SftpResult<string>> MoveIntoAsync(
        string sourcePath,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (!IsValidPath(destinationDirectory) || !Directory.Exists(destinationDirectory))
        {
            return Task.FromResult(SftpResult<string>.Fail(
                SftpError.InvalidPath,
                $"'{destinationDirectory}' is not a folder that can be written to."));
        }

        var name = GetName(sourcePath);
        var destination = Path.Combine(destinationDirectory, name);
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            return Task.FromResult(SftpResult<string>.Fail(
                SftpError.AlreadyExists,
                $"'{name}' already exists in {destinationDirectory}."));
        }

        try
        {
            if (Directory.Exists(sourcePath))
            {
                MoveDirectory(sourcePath, destination, cancellationToken);
            }
            else if (File.Exists(sourcePath))
            {
                File.Move(sourcePath, destination);
            }
            else
            {
                return Task.FromResult(SftpResult<string>.Fail(
                    SftpError.NotFound,
                    $"'{sourcePath}' was not found."));
            }

            return Task.FromResult(SftpResult<string>.Success(destination));
        }
        catch (OperationCanceledException)
        {
            return Task.FromResult(SftpResult<string>.Fail(SftpError.Cancelled, "The move was cancelled."));
        }
        catch (UnauthorizedAccessException exception)
        {
            return Task.FromResult(SftpResult<string>.Fail(SftpError.PermissionDenied, exception.Message));
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException)
        {
            return Task.FromResult(SftpResult<string>.Fail(SftpError.Unknown, exception.Message));
        }
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

        var options = new FileBrowserListOptions { ShowHidden = true };
        string? token = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await ListAsync(
                root.Path,
                options with { ContinuationToken = token },
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

    /// <summary>Paged with a synthetic index token, so a <c>node_modules</c> holding 200,000 files reaches
    /// the pane exactly the way a 200,000-key prefix does and the pane keeps zero source-specific
    /// branches.</summary>
    private static FileBrowserPage Page(
        List<FileBrowserEntry> entries,
        FileBrowserListOptions options,
        string? warning)
    {
        var start = 0;
        if (options.ContinuationToken is { Length: > 0 } token &&
            int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            start = Math.Clamp(parsed, 0, entries.Count);
        }

        var size = Math.Max(1, options.PageSize);
        var window = entries.Skip(start).Take(size).ToArray();
        var next = start + window.Length;
        return new FileBrowserPage(
            window,
            next < entries.Count ? next.ToString(CultureInfo.InvariantCulture) : null,
            warning);
    }

    /// <summary><see cref="Directory.Move(string, string)"/> cannot cross a volume, and a staging directory
    /// under the temporary folder sits on a different volume from the destination often enough on Windows
    /// that the copy fallback is the ordinary path rather than the exotic one. A file needs no fallback:
    /// <see cref="File.Move(string, string)"/> copies across volumes by itself.</summary>
    private static void MoveDirectory(string source, string destination, CancellationToken cancellationToken)
    {
        try
        {
            Directory.Move(source, destination);
            return;
        }
        catch (IOException)
        {
            // Almost always "source and destination are on different volumes". Anything else this hides
            // will surface again from the copy below, with its own message.
        }

        CopyDirectory(source, destination, cancellationToken);
        Directory.Delete(source, recursive: true);
    }

    private static void CopyDirectory(string source, string destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)), cancellationToken);
        }
    }

    private static FileBrowserEntry? Describe(FileSystemInfo info, FileBrowserListOptions options)
    {
        try
        {
            // The correct local test is the hidden attribute, not a leading dot: "." names nothing on
            // Windows, and a dotfile is not hidden to the operating system on either platform.
            if (!options.ShowHidden && info.Attributes.HasFlag(FileAttributes.Hidden))
            {
                return null;
            }

            if (options.NamePrefix is { Length: > 0 } prefix &&
                !info.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var isDirectory = info is DirectoryInfo;
            return new FileBrowserEntry(
                info.Name,
                info.FullName,
                isDirectory,
                isDirectory ? 0 : ((FileInfo)info).Length,
                info.LastWriteTimeUtc);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A row that vanished between enumeration and stat is not an error; it is simply gone.
            return null;
        }
    }

    private static string DefaultRoot()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home) || !Directory.Exists(home) ? Roots()[0] : home;
    }
}
