using System.Security.Cryptography;
using RemoteFlow.Application.Abstractions.Sftp;

namespace RemoteFlow.Infrastructure.Platform;

public sealed class WatchedFileMonitor(
    TimeSpan? debounce = null,
    TimeSpan? pollingInterval = null,
    bool forcePolling = false) : IWatchedFileMonitor
{
    private readonly TimeSpan _debounce = debounce ?? TimeSpan.FromMilliseconds(750);
    private readonly TimeSpan _pollingInterval = pollingInterval ?? TimeSpan.FromSeconds(2);
    private readonly bool _forcePolling = forcePolling;

    public Task<IWatchedFileSubscription> WatchAsync(
        string filePath,
        string initialSha256,
        Func<WatchedFileChange, CancellationToken, Task<bool>> onChanged,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(initialSha256);
        ArgumentNullException.ThrowIfNull(onChanged);
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The watched editing copy was not found.", fullPath);
        }
        IWatchedFileSubscription subscription = new Subscription(
            fullPath,
            initialSha256,
            onChanged,
            _debounce,
            _pollingInterval,
            _forcePolling);
        return Task.FromResult(subscription);
    }

    private sealed class Subscription : IWatchedFileSubscription
    {
        private readonly string _path;
        private readonly Func<WatchedFileChange, CancellationToken, Task<bool>> _onChanged;
        private readonly TimeSpan _debounce;
        private readonly CancellationTokenSource _stopping = new();
        private readonly SemaphoreSlim _checkGate = new(1, 1);
        private readonly Timer _debounceTimer;
        private readonly Timer _pollingTimer;
        private FileSystemWatcher? _watcher;
        private string _acknowledgedSha256;
        private int _disposed;

        public Subscription(
            string path,
            string initialSha256,
            Func<WatchedFileChange, CancellationToken, Task<bool>> onChanged,
            TimeSpan debounce,
            TimeSpan pollingInterval,
            bool forcePolling)
        {
            _path = path;
            _acknowledgedSha256 = initialSha256;
            _onChanged = onChanged;
            _debounce = debounce;
            _debounceTimer = new Timer(_ => _ = CheckSafelyAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _pollingTimer = new Timer(
                _ => _ = CheckSafelyAsync(),
                null,
                pollingInterval,
                pollingInterval);
            if (!forcePolling)
            {
                TryStartWatcher();
            }
        }

        public Task CheckNowAsync(CancellationToken cancellationToken = default)
        {
            return CheckAsync(cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            _stopping.Cancel();
            _watcher?.Dispose();
            await _debounceTimer.DisposeAsync().ConfigureAwait(false);
            await _pollingTimer.DisposeAsync().ConfigureAwait(false);
            await _checkGate.WaitAsync().ConfigureAwait(false);
            _ = _checkGate.Release();
            _checkGate.Dispose();
            _stopping.Dispose();
        }

        private void TryStartWatcher()
        {
            try
            {
                var directory = Path.GetDirectoryName(_path)!;
                _watcher = new FileSystemWatcher(directory)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName |
                        NotifyFilters.LastWrite |
                        NotifyFilters.Size |
                        NotifyFilters.CreationTime,
                    EnableRaisingEvents = false,
                };
                _watcher.Changed += OnFileEvent;
                _watcher.Created += OnFileEvent;
                _watcher.Deleted += OnFileEvent;
                _watcher.Renamed += OnRenamed;
                _watcher.Error += OnWatcherError;
                _watcher.EnableRaisingEvents = true;
            }
            catch (IOException)
            {
                _watcher?.Dispose();
                _watcher = null;
            }
            catch (UnauthorizedAccessException)
            {
                _watcher?.Dispose();
                _watcher = null;
            }
        }

        private void OnFileEvent(object sender, FileSystemEventArgs args)
        {
            if (string.Equals(Path.GetFullPath(args.FullPath), _path, PathComparison()))
            {
                Signal();
            }
        }

        private void OnRenamed(object sender, RenamedEventArgs args)
        {
            if (string.Equals(Path.GetFullPath(args.FullPath), _path, PathComparison()) ||
                string.Equals(Path.GetFullPath(args.OldFullPath), _path, PathComparison()))
            {
                Signal();
            }
        }

        private void OnWatcherError(object sender, ErrorEventArgs args)
        {
            _watcher?.Dispose();
            _watcher = null;
            Signal();
        }

        private void Signal()
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                _ = _debounceTimer.Change(_debounce, Timeout.InfiniteTimeSpan);
            }
        }

        private async Task CheckSafelyAsync()
        {
            try
            {
                await CheckAsync(_stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private async Task CheckAsync(CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }
            await _checkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var change = await TryCaptureAsync(cancellationToken).ConfigureAwait(false);
                if (change is null || string.Equals(
                    change.Sha256,
                    _acknowledgedSha256,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                if (await _onChanged(change, cancellationToken).ConfigureAwait(false))
                {
                    var acknowledged = await TryCaptureAsync(cancellationToken).ConfigureAwait(false);
                    _acknowledgedSha256 = acknowledged?.Sha256 ?? change.Sha256;
                }
            }
            finally
            {
                _ = _checkGate.Release();
            }
        }

        private async Task<WatchedFileChange?> TryCaptureAsync(CancellationToken cancellationToken)
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (!File.Exists(_path))
                    {
                        return null;
                    }
                    var info = new FileInfo(_path);
                    await using var stream = new FileStream(
                        _path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete,
                        64 * 1024,
                        useAsync: true);
                    var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
                    info.Refresh();
                    return new WatchedFileChange(
                        _path,
                        info.Length,
                        info.LastWriteTimeUtc,
                        Convert.ToHexString(hash).ToLowerInvariant());
                }
                catch (IOException) when (attempt < 3)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(50 * (attempt + 1)), cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            return null;
        }

        private static StringComparison PathComparison()
        {
            return OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
        }
    }
}
