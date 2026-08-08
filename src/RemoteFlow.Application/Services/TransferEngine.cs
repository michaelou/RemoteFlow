using System.Diagnostics;
using RemoteFlow.Application.Abstractions.Sftp;

#pragma warning disable IDE0046 // Guard clauses keep transfer failure handling explicit.

namespace RemoteFlow.Application.Services;

public sealed class TransferEngine : IDisposable
{
    private readonly ISftpService _sftp;
    private readonly ITransferConflictResolver? _conflictResolver;
    private readonly TransferOptions _options;
    private readonly SemaphoreSlim _concurrency;
    private int _disposed;

    public TransferEngine(
        ISftpService sftp,
        ITransferConflictResolver? conflictResolver = null,
        TransferOptions? options = null)
    {
        _sftp = sftp ?? throw new ArgumentNullException(nameof(sftp));
        _conflictResolver = conflictResolver;
        _options = options ?? new TransferOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxConcurrentTransfers, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.BufferSize, 1);
        _concurrency = new SemaphoreSlim(
            _options.MaxConcurrentTransfers,
            _options.MaxConcurrentTransfers);
    }

    public async Task<TransferResult> UploadAsync(
        string localPath,
        string remotePath,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        ThrowIfDisposed();
        if (!File.Exists(localPath) && !Directory.Exists(localPath))
        {
            return SingleFailure(
                localPath,
                remotePath,
                SftpError.NotFound,
                "The local source path was not found.");
        }

        if (!await TryEnterAsync(cancellationToken).ConfigureAwait(false))
        {
            return SingleCancelled(localPath, remotePath);
        }

        try
        {
            return File.Exists(localPath)
                ? new TransferResult([
                    await UploadFileAsync(localPath, SftpPath.Normalize(remotePath), progress, cancellationToken)
                        .ConfigureAwait(false),
                ])
                : await UploadDirectoryAsync(localPath, remotePath, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = _concurrency.Release();
        }
    }

    public async Task<TransferResult> DownloadAsync(
        string remotePath,
        string localPath,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        ThrowIfDisposed();
        if (!await TryEnterAsync(cancellationToken).ConfigureAwait(false))
        {
            return SingleCancelled(remotePath, localPath);
        }

        try
        {
            var normalized = SftpPath.Normalize(remotePath);
            var stat = await _sftp.StatAsync(normalized, cancellationToken).ConfigureAwait(false);
            if (stat.IsFailure)
            {
                return SingleFailure(normalized, localPath, stat.Failure);
            }

            if (stat.Value is null)
            {
                return SingleFailure(
                    normalized,
                    localPath,
                    SftpError.NotFound,
                    "The remote source path was not found.");
            }

            return stat.Value.IsDirectory && !stat.Value.IsSymlink
                ? await DownloadDirectoryAsync(normalized, localPath, progress, cancellationToken).ConfigureAwait(false)
                : new TransferResult([
                    await DownloadFileAsync(stat.Value, localPath, progress, cancellationToken).ConfigureAwait(false),
                ]);
        }
        finally
        {
            _ = _concurrency.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _concurrency.Dispose();
        }
    }

    private async Task<TransferResult> UploadDirectoryAsync(
        string localDirectory,
        string remoteDirectory,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var normalizedRemote = SftpPath.Normalize(remoteDirectory);
        var ensured = await EnsureRemoteDirectoryAsync(normalizedRemote, cancellationToken).ConfigureAwait(false);
        if (ensured is not null)
        {
            return new TransferResult([ensured]);
        }

        var results = new List<TransferItemResult>();
        foreach (var directory in Directory.EnumerateDirectories(localDirectory, "*", SearchOption.AllDirectories))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                results.Add(Cancelled(directory, normalizedRemote));
                break;
            }

            var relative = Path.GetRelativePath(localDirectory, directory);
            var target = SftpPath.Combine(normalizedRemote, relative);
            var directoryResult = await EnsureRemoteDirectoryAsync(target, cancellationToken).ConfigureAwait(false);
            if (directoryResult is not null)
            {
                results.Add(directoryResult);
            }
        }

        foreach (var file in Directory.EnumerateFiles(localDirectory, "*", SearchOption.AllDirectories))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                results.Add(Cancelled(file, normalizedRemote));
                break;
            }

            var relative = Path.GetRelativePath(localDirectory, file);
            results.Add(await UploadFileAsync(
                file,
                SftpPath.Combine(normalizedRemote, relative),
                progress,
                cancellationToken).ConfigureAwait(false));
        }

        return new TransferResult(results);
    }

    private async Task<TransferResult> DownloadDirectoryAsync(
        string remoteDirectory,
        string localDirectory,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        _ = Directory.CreateDirectory(localDirectory);
        var results = new List<TransferItemResult>();
        await DownloadDirectoryEntriesAsync(
            remoteDirectory,
            localDirectory,
            results,
            progress,
            cancellationToken).ConfigureAwait(false);
        return new TransferResult(results);
    }

    private async Task DownloadDirectoryEntriesAsync(
        string remoteDirectory,
        string localDirectory,
        List<TransferItemResult> results,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var listed = await _sftp.ListAsync(remoteDirectory, cancellationToken).ConfigureAwait(false);
        if (listed.IsFailure)
        {
            results.Add(Failed(remoteDirectory, localDirectory, listed.Failure));
            return;
        }

        foreach (var entry in listed.Value)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                results.Add(Cancelled(entry.FullPath, localDirectory));
                return;
            }

            var localTarget = Path.Combine(localDirectory, entry.Name);
            if (entry.IsDirectory && !entry.IsSymlink)
            {
                _ = Directory.CreateDirectory(localTarget);
                await DownloadDirectoryEntriesAsync(
                    entry.FullPath,
                    localTarget,
                    results,
                    progress,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                results.Add(await DownloadFileAsync(entry, localTarget, progress, cancellationToken)
                    .ConfigureAwait(false));
            }
        }
    }

    private async Task<TransferItemResult> UploadFileAsync(
        string localPath,
        string remotePath,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var existing = await _sftp.StatAsync(remotePath, cancellationToken).ConfigureAwait(false);
        if (existing.IsFailure)
        {
            return Failed(localPath, remotePath, existing.Failure);
        }

        var conflict = await ResolveConflictAsync(
            TransferDirection.Upload,
            localPath,
            remotePath,
            existing.Value?.Size,
            existing.Value is not null,
            cancellationToken).ConfigureAwait(false);
        if (conflict is not null)
        {
            return conflict;
        }

        var temporaryPath = remotePath + ".part";
        var length = new FileInfo(localPath).Length;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await BestEffortDeleteRemoteAsync(temporaryPath).ConfigureAwait(false);
            try
            {
                await using var source = new FileStream(
                    localPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    _options.BufferSize,
                    useAsync: true);
                var opened = await _sftp.OpenWriteAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
                if (opened.IsFailure)
                {
                    if (attempt == 0 && IsTransient(opened.Failure))
                    {
                        continue;
                    }

                    return Failed(localPath, remotePath, opened.Failure);
                }

                await using (opened.Value.ConfigureAwait(false))
                {
                    await CopyWithProgressAsync(
                        source,
                        opened.Value,
                        localPath,
                        remotePath,
                        length,
                        progress,
                        cancellationToken).ConfigureAwait(false);
                }

                var renamed = await _sftp.RenameAsync(temporaryPath, remotePath, cancellationToken)
                    .ConfigureAwait(false);
                if (renamed.IsSuccess)
                {
                    return Completed(localPath, remotePath);
                }

                if (attempt == 0 && IsTransient(renamed.Failure))
                {
                    continue;
                }

                return Failed(localPath, remotePath, renamed.Failure);
            }
            catch (OperationCanceledException)
            {
                await BestEffortDeleteRemoteAsync(temporaryPath).ConfigureAwait(false);
                return Cancelled(localPath, remotePath);
            }
            catch (IOException exception) when (attempt == 0)
            {
                _ = exception;
            }
            catch (Exception exception)
            {
                await BestEffortDeleteRemoteAsync(temporaryPath).ConfigureAwait(false);
                return Failed(localPath, remotePath, new SftpFailure(SftpError.Unknown, exception.Message));
            }
        }

        await BestEffortDeleteRemoteAsync(temporaryPath).ConfigureAwait(false);
        return Failed(
            localPath,
            remotePath,
            new SftpFailure(SftpError.ConnectionLost, "The upload failed after one retry."));
    }

    private async Task<TransferItemResult> DownloadFileAsync(
        RemoteFileInfo remoteFile,
        string localPath,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var conflict = await ResolveConflictAsync(
            TransferDirection.Download,
            remoteFile.FullPath,
            localPath,
            File.Exists(localPath) ? new FileInfo(localPath).Length : null,
            File.Exists(localPath),
            cancellationToken).ConfigureAwait(false);
        if (conflict is not null)
        {
            return conflict;
        }

        var parent = Path.GetDirectoryName(Path.GetFullPath(localPath));
        if (parent is not null)
        {
            _ = Directory.CreateDirectory(parent);
        }

        var temporaryPath = localPath + ".part";
        for (var attempt = 0; attempt < 2; attempt++)
        {
            BestEffortDeleteLocal(temporaryPath);
            try
            {
                var opened = await _sftp.OpenReadAsync(remoteFile.FullPath, cancellationToken).ConfigureAwait(false);
                if (opened.IsFailure)
                {
                    if (attempt == 0 && IsTransient(opened.Failure))
                    {
                        continue;
                    }

                    return Failed(remoteFile.FullPath, localPath, opened.Failure);
                }

                await using (opened.Value.ConfigureAwait(false))
                await using (var destination = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    _options.BufferSize,
                    useAsync: true))
                {
                    await CopyWithProgressAsync(
                        opened.Value,
                        destination,
                        remoteFile.FullPath,
                        localPath,
                        remoteFile.Size,
                        progress,
                        cancellationToken).ConfigureAwait(false);
                }

                File.Move(temporaryPath, localPath, overwrite: true);
                return Completed(remoteFile.FullPath, localPath);
            }
            catch (OperationCanceledException)
            {
                BestEffortDeleteLocal(temporaryPath);
                return Cancelled(remoteFile.FullPath, localPath);
            }
            catch (IOException exception) when (attempt == 0)
            {
                _ = exception;
            }
            catch (Exception exception)
            {
                BestEffortDeleteLocal(temporaryPath);
                return Failed(
                    remoteFile.FullPath,
                    localPath,
                    new SftpFailure(SftpError.Unknown, exception.Message));
            }
        }

        BestEffortDeleteLocal(temporaryPath);
        return Failed(
            remoteFile.FullPath,
            localPath,
            new SftpFailure(SftpError.ConnectionLost, "The download failed after one retry."));
    }

    private async Task<TransferItemResult?> EnsureRemoteDirectoryAsync(
        string remotePath,
        CancellationToken cancellationToken)
    {
        var stat = await _sftp.StatAsync(remotePath, cancellationToken).ConfigureAwait(false);
        if (stat.IsFailure)
        {
            return Failed(remotePath, remotePath, stat.Failure);
        }

        if (stat.Value is { IsDirectory: true })
        {
            return null;
        }

        if (stat.Value is not null)
        {
            return Failed(
                remotePath,
                remotePath,
                new SftpFailure(SftpError.NotDirectory, "A file exists where a directory is required."));
        }

        var created = await _sftp.CreateDirectoryAsync(remotePath, cancellationToken).ConfigureAwait(false);
        return created.IsSuccess ? null : Failed(remotePath, remotePath, created.Failure);
    }

    private async Task<TransferItemResult?> ResolveConflictAsync(
        TransferDirection direction,
        string sourcePath,
        string destinationPath,
        long? existingSize,
        bool exists,
        CancellationToken cancellationToken)
    {
        if (!exists)
        {
            return null;
        }

        if (_conflictResolver is null)
        {
            return new TransferItemResult(
                sourcePath,
                destinationPath,
                TransferItemStatus.Conflict,
                new SftpFailure(SftpError.AlreadyExists, "The target exists and requires confirmation."));
        }

        try
        {
            var decision = await _conflictResolver.ResolveAsync(
                new TransferConflict(direction, sourcePath, destinationPath, existingSize),
                cancellationToken).ConfigureAwait(false);
            return decision switch
            {
                TransferConflictDecision.Overwrite => null,
                TransferConflictDecision.Skip => new TransferItemResult(
                    sourcePath,
                    destinationPath,
                    TransferItemStatus.Skipped),
                TransferConflictDecision.Cancel => Cancelled(sourcePath, destinationPath),
                _ => throw new InvalidOperationException($"Unknown transfer conflict decision: {decision}."),
            };
        }
        catch (OperationCanceledException)
        {
            return Cancelled(sourcePath, destinationPath);
        }
    }

    private async Task CopyWithProgressAsync(
        Stream source,
        Stream destination,
        string sourcePath,
        string destinationPath,
        long totalBytes,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[_options.BufferSize];
        var transferred = 0L;
        var stopwatch = Stopwatch.StartNew();
        Report(progress, sourcePath, destinationPath, 0, totalBytes, 0, isCompleted: false);
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            transferred += read;
            var seconds = Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
            Report(
                progress,
                sourcePath,
                destinationPath,
                transferred,
                totalBytes,
                transferred / seconds,
                isCompleted: false);
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        var finalRate = transferred == 0
            ? 0
            : transferred / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
        Report(
            progress,
            sourcePath,
            destinationPath,
            transferred,
            totalBytes,
            finalRate,
            isCompleted: true);
    }

    private static void Report(
        IProgress<TransferProgress>? progress,
        string sourcePath,
        string destinationPath,
        long transferred,
        long total,
        double rate,
        bool isCompleted)
    {
        TimeSpan? remaining = !isCompleted && rate > 0 && total > transferred
            ? TimeSpan.FromSeconds((total - transferred) / rate)
            : isCompleted ? TimeSpan.Zero : null;
        progress?.Report(new TransferProgress(
            sourcePath,
            destinationPath,
            transferred,
            total,
            rate,
            remaining,
            isCompleted));
    }

    private async Task<bool> TryEnterAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task BestEffortDeleteRemoteAsync(string path)
    {
        _ = await _sftp.DeleteAsync(path, recursive: false, CancellationToken.None).ConfigureAwait(false);
    }

    private static void BestEffortDeleteLocal(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool IsTransient(SftpFailure failure)
    {
        return failure.Error is SftpError.ConnectionLost or SftpError.Unknown;
    }

    private static TransferResult SingleFailure(
        string sourcePath,
        string destinationPath,
        SftpError error,
        string message)
    {
        return SingleFailure(sourcePath, destinationPath, new SftpFailure(error, message));
    }

    private static TransferResult SingleFailure(
        string sourcePath,
        string destinationPath,
        SftpFailure failure)
    {
        return new TransferResult([Failed(sourcePath, destinationPath, failure)]);
    }

    private static TransferResult SingleCancelled(string sourcePath, string destinationPath)
    {
        return new TransferResult([Cancelled(sourcePath, destinationPath)]);
    }

    private static TransferItemResult Completed(string sourcePath, string destinationPath)
    {
        return new TransferItemResult(sourcePath, destinationPath, TransferItemStatus.Completed);
    }

    private static TransferItemResult Cancelled(string sourcePath, string destinationPath)
    {
        return new TransferItemResult(
            sourcePath,
            destinationPath,
            TransferItemStatus.Cancelled,
            new SftpFailure(SftpError.Cancelled, "The transfer was cancelled."));
    }

    private static TransferItemResult Failed(
        string sourcePath,
        string destinationPath,
        SftpFailure failure)
    {
        return new TransferItemResult(sourcePath, destinationPath, TransferItemStatus.Failed, failure);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}

#pragma warning restore IDE0046
