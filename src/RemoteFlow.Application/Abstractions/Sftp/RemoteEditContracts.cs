namespace RemoteFlow.Application.Abstractions.Sftp;

public sealed record RemoteSnapshot(
    long Size,
    DateTimeOffset MTimeUtc,
    string? Sha256,
    bool Exists = true);

public sealed record LocalSnapshot(long Size, DateTimeOffset MTimeUtc, string Sha256);

public sealed record WatchedFileChange(
    string Path,
    long Size,
    DateTimeOffset MTimeUtc,
    string Sha256);

public interface IWatchedFileSubscription : IAsyncDisposable
{
    Task CheckNowAsync(CancellationToken cancellationToken = default);
}

public interface IWatchedFileMonitor
{
    Task<IWatchedFileSubscription> WatchAsync(
        string filePath,
        string initialSha256,
        Func<WatchedFileChange, CancellationToken, Task<bool>> onChanged,
        CancellationToken cancellationToken = default);
}

public interface IFileEditorLauncher
{
    Task OpenAsync(string filePath, CancellationToken cancellationToken = default);
}

public interface IRemoteEditCloseGuard
{
    Task<bool> ConfirmDiscardUnsavedChangesAsync(
        string remotePath,
        CancellationToken cancellationToken = default);
}

public enum RemoteEditConflictResolution
{
    OverwriteRemote = 1,
    KeepBoth = 2,
    DiscardLocal = 3,
    Cancel = 4,
}

public sealed record RemoteEditConflict(
    string RemotePath,
    RemoteSnapshot DownloadedSnapshot,
    RemoteSnapshot CurrentSnapshot);

public interface IRemoteEditConflictResolver
{
    Task<RemoteEditConflictResolution> ResolveAsync(
        RemoteEditConflict conflict,
        CancellationToken cancellationToken = default);
}

public sealed class RemoteEditHandle
{
    internal RemoteEditHandle(
        Guid id,
        string remotePath,
        string localPath,
        RemoteSnapshot remoteSnapshot,
        LocalSnapshot localSnapshot)
    {
        Id = id;
        OriginalRemotePath = remotePath;
        RemotePath = remotePath;
        LocalPath = localPath;
        RemoteSnapshot = remoteSnapshot;
        LocalSnapshot = localSnapshot;
    }

    public Guid Id { get; }

    public string OriginalRemotePath { get; }

    public string RemotePath { get; internal set; }

    public string LocalPath { get; }

    public RemoteSnapshot RemoteSnapshot { get; internal set; }

    public LocalSnapshot LocalSnapshot { get; internal set; }

    public bool IsDirty { get; internal set; }

    public bool IsUploading { get; internal set; }

    internal IWatchedFileSubscription? Watch { get; set; }

    internal SemaphoreSlim UploadGate { get; } = new(1, 1);
}

public sealed record RemoteEditUploadResult(
    string RemotePath,
    string LocalPath,
    bool Succeeded,
    string? Message = null);

public interface IRemoteEditService : IAsyncDisposable
{
    event EventHandler? ActiveEditsChanged;

    /// <summary>
    /// Raised after a saved editing copy has been published to the server, or after publishing it failed.
    /// Handlers run on the file-watcher's thread.
    /// </summary>
    event EventHandler<RemoteEditUploadResult>? UploadCompleted;

    IReadOnlyList<RemoteEditHandle> ActiveEdits { get; }

    int ActiveCount { get; }

    Task<RemoteEditHandle> OpenAsync(
        string remotePath,
        CancellationToken cancellationToken = default);

    Task<bool> CloseAsync(
        RemoteEditHandle edit,
        CancellationToken cancellationToken = default);

    Task<bool> CloseAllAsync(CancellationToken cancellationToken = default);
}

public interface IRemoteEditServiceFactory
{
    IRemoteEditService Create(ISftpService sftp, Guid sessionId);

    Task SweepStaleFilesAsync(CancellationToken cancellationToken = default);
}
