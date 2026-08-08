using System.Security.Cryptography;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Sftp;

namespace RemoteFlow.Application.Services;

public sealed class RemoteEditServiceFactory(
    IAppPaths appPaths,
    IFileEditorLauncher editorLauncher,
    IWatchedFileMonitor fileMonitor,
    IRemoteEditCloseGuard closeGuard,
    IRemoteEditConflictResolver conflictResolver,
    IClock clock) : IRemoteEditServiceFactory
{
    private static readonly TimeSpan _staleAge = TimeSpan.FromDays(1);
    private readonly string _root = Path.Combine(
        (appPaths ?? throw new ArgumentNullException(nameof(appPaths))).CacheDirectory,
        "remote-edit");

    public IRemoteEditService Create(ISftpService sftp, Guid sessionId)
    {
        return new RemoteEditService(
            sftp,
            editorLauncher,
            fileMonitor,
            closeGuard,
            _root,
            sessionId,
            conflictResolver,
            clock);
    }

    public Task SweepStaleFilesAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_root))
        {
            return Task.CompletedTask;
        }

        foreach (var directory in Directory.EnumerateDirectories(_root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var lastWrite = Directory.GetLastWriteTimeUtc(directory);
                if (DateTime.UtcNow - lastWrite >= _staleAge)
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return Task.CompletedTask;
    }
}

public sealed class RemoteEditService : IRemoteEditService
{
    public const long HashLimitBytes = 8L * 1024 * 1024;

    private readonly ISftpService _sftp;
    private readonly IFileEditorLauncher _editorLauncher;
    private readonly IWatchedFileMonitor _fileMonitor;
    private readonly IRemoteEditCloseGuard _closeGuard;
    private readonly IRemoteEditConflictResolver? _conflictResolver;
    private readonly IClock _clock;
    private readonly string _sessionRoot;
    private readonly List<RemoteEditHandle> _active = [];
    private readonly Lock _sync = new();
    private int _disposed;

    public RemoteEditService(
        ISftpService sftp,
        IFileEditorLauncher editorLauncher,
        IWatchedFileMonitor fileMonitor,
        IRemoteEditCloseGuard closeGuard,
        string cacheRoot,
        Guid sessionId,
        IRemoteEditConflictResolver? conflictResolver = null,
        IClock? clock = null)
    {
        _sftp = sftp ?? throw new ArgumentNullException(nameof(sftp));
        _editorLauncher = editorLauncher ?? throw new ArgumentNullException(nameof(editorLauncher));
        _fileMonitor = fileMonitor ?? throw new ArgumentNullException(nameof(fileMonitor));
        _closeGuard = closeGuard ?? throw new ArgumentNullException(nameof(closeGuard));
        _conflictResolver = conflictResolver;
        _clock = clock ?? SystemClock.Instance;
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        _sessionRoot = Path.Combine(cacheRoot, sessionId.ToString("N"));
    }

    public event EventHandler? ActiveEditsChanged;

    public IReadOnlyList<RemoteEditHandle> ActiveEdits
    {
        get
        {
            lock (_sync)
            {
                return [.. _active];
            }
        }
    }

    public int ActiveCount
    {
        get
        {
            lock (_sync)
            {
                return _active.Count;
            }
        }
    }

    public async Task<RemoteEditHandle> OpenAsync(
        string remotePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        ThrowIfDisposed();
        var normalized = SftpPath.Normalize(remotePath);
        lock (_sync)
        {
            var existing = _active.FirstOrDefault(edit =>
                string.Equals(edit.OriginalRemotePath, normalized, StringComparison.Ordinal));
            if (existing is not null)
            {
                return existing;
            }
        }

        var stat = await _sftp.StatAsync(normalized, cancellationToken).ConfigureAwait(false);
        if (stat.IsFailure)
        {
            throw new IOException(stat.Failure.Message);
        }
        if (stat.Value is null || stat.Value.IsDirectory)
        {
            throw new IOException(stat.Value is null
                ? "The remote file was not found."
                : "Directories cannot be opened in an external editor.");
        }

        var localPath = BuildLocalPath(_sessionRoot, normalized);
        await DownloadAsync(normalized, localPath, cancellationToken).ConfigureAwait(false);
        var local = await CaptureLocalSnapshotAsync(localPath, cancellationToken).ConfigureAwait(false);
        var remote = new RemoteSnapshot(
            stat.Value.Size,
            stat.Value.ModifiedTime.ToUniversalTime(),
            stat.Value.Size <= HashLimitBytes ? local.Sha256 : null);
        var edit = new RemoteEditHandle(Guid.NewGuid(), normalized, localPath, remote, local);

        try
        {
            edit.Watch = await _fileMonitor.WatchAsync(
                localPath,
                local.Sha256,
                (change, token) => UploadChangedFileAsync(edit, change, token),
                cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                _active.Add(edit);
            }
            ActiveEditsChanged?.Invoke(this, EventArgs.Empty);
            await _editorLauncher.OpenAsync(localPath, cancellationToken).ConfigureAwait(false);
            return edit;
        }
        catch
        {
            if (edit.Watch is not null)
            {
                await edit.Watch.DisposeAsync().ConfigureAwait(false);
            }
            DeleteEditDirectory(localPath);
            lock (_sync)
            {
                _ = _active.Remove(edit);
            }
            throw;
        }
    }

    public async Task<bool> CloseAsync(
        RemoteEditHandle edit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (edit.Watch is not null)
        {
            await edit.Watch.CheckNowAsync(cancellationToken).ConfigureAwait(false);
        }
        if (edit.IsDirty && !await _closeGuard.ConfirmDiscardUnsavedChangesAsync(
                edit.RemotePath,
                cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        lock (_sync)
        {
            if (!_active.Remove(edit))
            {
                return true;
            }
        }
        if (edit.Watch is not null)
        {
            await edit.Watch.DisposeAsync().ConfigureAwait(false);
        }
        edit.UploadGate.Dispose();
        DeleteEditDirectory(edit.LocalPath);
        ActiveEditsChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public async Task<bool> CloseAllAsync(CancellationToken cancellationToken = default)
    {
        foreach (var edit in ActiveEdits)
        {
            if (!await CloseAsync(edit, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
        }
        TryDeleteDirectory(_sessionRoot);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        foreach (var edit in ActiveEdits)
        {
            if (edit.Watch is not null)
            {
                await edit.Watch.DisposeAsync().ConfigureAwait(false);
            }
            edit.UploadGate.Dispose();
            DeleteEditDirectory(edit.LocalPath);
        }
        lock (_sync)
        {
            _active.Clear();
        }
        TryDeleteDirectory(_sessionRoot);
        ActiveEditsChanged?.Invoke(this, EventArgs.Empty);
        GC.SuppressFinalize(this);
    }

    public static string BuildLocalPath(string sessionRoot, string remotePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionRoot);
        var normalized = SftpPath.Normalize(remotePath);
        var pathHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized)))
            [..8]
            .ToLowerInvariant();
        return Path.Combine(sessionRoot, pathHash, SftpPath.GetName(normalized));
    }

    public static async Task<LocalSnapshot> CaptureLocalSnapshotAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(path);
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        info.Refresh();
        return new LocalSnapshot(info.Length, info.LastWriteTimeUtc, Convert.ToHexString(hash).ToLowerInvariant());
    }

    private async Task<bool> UploadChangedFileAsync(
        RemoteEditHandle edit,
        WatchedFileChange change,
        CancellationToken cancellationToken)
    {
        edit.IsDirty = true;
        await edit.UploadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            edit.IsUploading = true;
            var current = await CaptureRemoteSnapshotAsync(
                edit.RemotePath,
                edit.RemoteSnapshot.Sha256 is not null,
                cancellationToken).ConfigureAwait(false);
            if (HasConflict(edit.RemoteSnapshot, current))
            {
                edit.IsDirty = true;
                var resolution = _conflictResolver is null
                    ? RemoteEditConflictResolution.OverwriteRemote
                    : await _conflictResolver.ResolveAsync(
                        new RemoteEditConflict(edit.RemotePath, edit.RemoteSnapshot, current),
                        cancellationToken).ConfigureAwait(false);
                switch (resolution)
                {
                    case RemoteEditConflictResolution.KeepBoth:
                        edit.RemotePath = BuildKeepBothPath(edit.RemotePath, _clock.UtcNow);
                        break;
                    case RemoteEditConflictResolution.DiscardLocal:
                        if (!current.Exists)
                        {
                            return true;
                        }
                        await DownloadAsync(edit.RemotePath, edit.LocalPath, cancellationToken).ConfigureAwait(false);
                        edit.LocalSnapshot = await CaptureLocalSnapshotAsync(edit.LocalPath, cancellationToken)
                            .ConfigureAwait(false);
                        edit.RemoteSnapshot = current;
                        edit.IsDirty = false;
                        return true;
                    case RemoteEditConflictResolution.Cancel:
                        return true;
                    case RemoteEditConflictResolution.OverwriteRemote:
                        break;
                    default:
                        throw new InvalidOperationException($"Unknown remote edit conflict resolution: {resolution}.");
                }
            }

            var uploaded = await UploadAsync(edit.LocalPath, edit.RemotePath, cancellationToken).ConfigureAwait(false);
            if (!uploaded)
            {
                return false;
            }
            edit.LocalSnapshot = new LocalSnapshot(change.Size, change.MTimeUtc, change.Sha256);
            edit.RemoteSnapshot = await CaptureRemoteSnapshotAsync(
                edit.RemotePath,
                change.Size <= HashLimitBytes,
                cancellationToken).ConfigureAwait(false);
            edit.IsDirty = false;
            return true;
        }
        finally
        {
            edit.IsUploading = false;
            _ = edit.UploadGate.Release();
        }
    }

    public static bool HasConflict(RemoteSnapshot downloaded, RemoteSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(downloaded);
        ArgumentNullException.ThrowIfNull(current);
        return downloaded.Exists != current.Exists ||
            downloaded.Size != current.Size ||
            downloaded.MTimeUtc != current.MTimeUtc ||
            (downloaded.Sha256 is not null && !string.Equals(
                downloaded.Sha256,
                current.Sha256,
                StringComparison.OrdinalIgnoreCase));
    }

    public static string BuildKeepBothPath(string remotePath, DateTimeOffset timestamp)
    {
        var normalized = SftpPath.Normalize(remotePath);
        var name = SftpPath.GetName(normalized);
        var extension = Path.GetExtension(name);
        var stem = extension.Length == 0 ? name : name[..^extension.Length];
        var copyName = $"{stem}.remoteflow-{timestamp.ToUniversalTime():yyyyMMdd-HHmmss}{extension}";
        var separator = normalized.LastIndexOf('/');
        if (separator < 0)
        {
            return copyName;
        }
        var parent = separator == 0 ? "/" : normalized[..separator];
        return SftpPath.Combine(parent, copyName);
    }

    private async Task<RemoteSnapshot> CaptureRemoteSnapshotAsync(
        string remotePath,
        bool includeHash,
        CancellationToken cancellationToken)
    {
        var stat = await _sftp.StatAsync(remotePath, cancellationToken).ConfigureAwait(false);
        if (stat.IsFailure)
        {
            throw new IOException(stat.Failure.Message);
        }
        if (stat.Value is null)
        {
            return new RemoteSnapshot(0, DateTimeOffset.UnixEpoch, null, Exists: false);
        }
        string? hash = null;
        if (includeHash && stat.Value.Size <= HashLimitBytes && !stat.Value.IsDirectory)
        {
            var opened = await _sftp.OpenReadAsync(remotePath, cancellationToken).ConfigureAwait(false);
            if (opened.IsFailure)
            {
                throw new IOException(opened.Failure.Message);
            }
            await using (opened.Value.ConfigureAwait(false))
            {
                var bytes = await SHA256.HashDataAsync(opened.Value, cancellationToken).ConfigureAwait(false);
                hash = Convert.ToHexString(bytes).ToLowerInvariant();
            }
        }
        return new RemoteSnapshot(
            stat.Value.Size,
            stat.Value.ModifiedTime.ToUniversalTime(),
            hash);
    }

    private async Task DownloadAsync(string remotePath, string localPath, CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(localPath)!;
        _ = Directory.CreateDirectory(parent);
        var temporary = localPath + ".part";
        File.Delete(temporary);
        var opened = await _sftp.OpenReadAsync(remotePath, cancellationToken).ConfigureAwait(false);
        if (opened.IsFailure)
        {
            throw new IOException(opened.Failure.Message);
        }
        try
        {
            await using (opened.Value.ConfigureAwait(false))
            await using (var destination = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                useAsync: true))
            {
                await opened.Value.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, localPath, overwrite: true);
        }
        catch
        {
            File.Delete(temporary);
            throw;
        }
    }

    private async Task<bool> UploadAsync(string localPath, string remotePath, CancellationToken cancellationToken)
    {
        var temporary = remotePath + $".remoteflow-{Guid.NewGuid():N}.part";
        var opened = await _sftp.OpenWriteAsync(temporary, cancellationToken).ConfigureAwait(false);
        if (opened.IsFailure)
        {
            return false;
        }
        try
        {
            await using (opened.Value.ConfigureAwait(false))
            await using (var source = new FileStream(
                localPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                64 * 1024,
                useAsync: true))
            {
                await source.CopyToAsync(opened.Value, cancellationToken).ConfigureAwait(false);
                await opened.Value.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            var renamed = await _sftp.RenameAsync(temporary, remotePath, cancellationToken).ConfigureAwait(false);
            return renamed.IsSuccess;
        }
        finally
        {
            _ = await _sftp.DeleteAsync(temporary, recursive: false, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static void DeleteEditDirectory(string localPath)
    {
        var directory = Path.GetDirectoryName(localPath);
        if (directory is not null)
        {
            TryDeleteDirectory(directory);
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}
