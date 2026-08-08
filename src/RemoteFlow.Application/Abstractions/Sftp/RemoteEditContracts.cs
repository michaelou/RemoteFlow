namespace RemoteFlow.Application.Abstractions.Sftp;

public sealed record RemoteSnapshot(long Size, DateTimeOffset MTimeUtc, string? Sha256);

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
        RemotePath = remotePath;
        LocalPath = localPath;
        RemoteSnapshot = remoteSnapshot;
        LocalSnapshot = localSnapshot;
    }

    public Guid Id { get; }

    public string RemotePath { get; }

    public string LocalPath { get; }

    public RemoteSnapshot RemoteSnapshot { get; internal set; }

    public LocalSnapshot LocalSnapshot { get; internal set; }

    public bool IsDirty { get; internal set; }

    public bool IsUploading { get; internal set; }

    internal IWatchedFileSubscription? Watch { get; set; }

    internal SemaphoreSlim UploadGate { get; } = new(1, 1);
}

public interface IRemoteEditService : IAsyncDisposable
{
    event EventHandler? ActiveEditsChanged;

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
