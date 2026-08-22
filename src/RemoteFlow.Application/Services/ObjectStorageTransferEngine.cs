using Microsoft.Win32.SafeHandles;
using RemoteFlow.Application.Abstractions.Sftp;
using RemoteFlow.Application.Abstractions.Storage;

#pragma warning disable IDE0046 // Guard clauses keep transfer failure handling explicit.

namespace RemoteFlow.Application.Services;

/// <summary>Moves objects in parallel parts, with progress a user can trust and a cancel that does not
/// leave billable orphans behind.
///
/// A sibling of <see cref="TransferEngine"/> rather than an extension of it, and with no shared base
/// class: that engine is SFTP in its bones — <c>ISftpService</c>, <c>SftpPath</c>, <c>SftpPublisher</c>'s
/// rename-aside dance, a remote <c>.part</c> sidecar — and the only genuinely common body was the ETA
/// arithmetic, which a windowed <see cref="TransferRateMeter"/> replaces here. See ADR-0020.
///
/// It produces the same <see cref="TransferResult"/> the transfer queue already consumes, so it drops into
/// the queue unchanged. <see cref="TransferItemResult.Failure"/> being an <see cref="SftpFailure"/> is an
/// accepted naming wart: object-storage failures are mapped into it at this boundary rather than renaming
/// a type used at every SFTP call site.</summary>
public sealed class ObjectStorageTransferEngine
{
    /// <summary>Said out loud rather than swallowed: incomplete parts are billed, and a client that
    /// cannot promise they are gone should not imply that they are. The durable guarantee is a bucket
    /// lifecycle rule, which the documentation recommends.</summary>
    private const string _orphanNote =
        "The incomplete upload could not be aborted, so its parts may remain on the server and may be billed.";

    private readonly IObjectStorageService _storage;
    private readonly ITransferConflictResolver? _conflictResolver;
    private readonly ObjectTransferOptions _options;
    private readonly TimeProvider _time;

    public ObjectStorageTransferEngine(
        IObjectStorageService storage,
        ITransferConflictResolver? conflictResolver = null,
        ObjectTransferOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _conflictResolver = conflictResolver;
        _options = options ?? new ObjectTransferOptions();
        _options.Validate();
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<TransferResult> UploadAsync(
        string localPath,
        string remotePath,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        var normalized = ObjectStoragePath.Normalize(remotePath);
        if (File.Exists(localPath))
        {
            return new TransferResult([
                await UploadFileAsync(localPath, normalized, progress, cancellationToken).ConfigureAwait(false),
            ]);
        }

        return Directory.Exists(localPath)
            ? await UploadDirectoryAsync(localPath, normalized, progress, cancellationToken).ConfigureAwait(false)
            : new TransferResult([Failed(
                localPath,
                normalized,
                new SftpFailure(SftpError.NotFound, "The local source path was not found."))]);
    }

    public async Task<TransferResult> DownloadAsync(
        string remotePath,
        string localPath,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        var normalized = ObjectStoragePath.Normalize(remotePath);
        var stat = await _storage.StatAsync(normalized, cancellationToken).ConfigureAwait(false);
        if (stat.IsFailure)
        {
            return new TransferResult([Failed(normalized, localPath, stat.Failure)]);
        }

        if (stat.Value is null)
        {
            return new TransferResult([Failed(
                normalized,
                localPath,
                new SftpFailure(SftpError.NotFound, "The object was not found."))]);
        }

        if (!stat.Value.IsDirectory)
        {
            return new TransferResult([
                await DownloadFileAsync(stat.Value, localPath, progress, cancellationToken).ConfigureAwait(false),
            ]);
        }

        var results = new List<TransferItemResult>();
        await DownloadPrefixAsync(normalized, localPath, results, progress, cancellationToken)
            .ConfigureAwait(false);
        return new TransferResult(results);
    }

    private static SftpFailure? FirstFailure(IReadOnlyList<SftpResult> outcomes)
    {
        SftpFailure? cancellation = null;
        foreach (var outcome in outcomes)
        {
            if (outcome.IsSuccess)
            {
                continue;
            }

            if (outcome.Failure.Error != SftpError.Cancelled)
            {
                return outcome.Failure;
            }

            cancellation ??= outcome.Failure;
        }

        return cancellation;
    }

    private static SftpResult CancelledPart()
    {
        return SftpResult.Fail(SftpError.Cancelled, "The transfer was cancelled.");
    }

    private static bool IsTransient(SftpFailure failure)
    {
        return failure.Error is SftpError.ConnectionLost or SftpError.Unknown;
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

    private static SftpFailure Annotate(SftpFailure failure, SftpResult aborted)
    {
        return aborted.IsSuccess ? failure : new SftpFailure(failure.Error, failure.Message + " " + _orphanNote);
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

    private static TransferItemResult Cancelled(string sourcePath, string destinationPath, SftpResult aborted)
    {
        return aborted.IsSuccess
            ? Cancelled(sourcePath, destinationPath)
            : new TransferItemResult(
                sourcePath,
                destinationPath,
                TransferItemStatus.Cancelled,
                new SftpFailure(SftpError.Cancelled, "The transfer was cancelled. " + _orphanNote));
    }

    private static TransferItemResult Failed(string sourcePath, string destinationPath, SftpFailure failure)
    {
        return new TransferItemResult(sourcePath, destinationPath, TransferItemStatus.Failed, failure);
    }

    private async Task<TransferResult> UploadDirectoryAsync(
        string localDirectory,
        string remotePrefix,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var results = new List<TransferItemResult>();
        var ensured = await EnsureFolderAsync(remotePrefix, cancellationToken).ConfigureAwait(false);
        if (ensured is not null)
        {
            results.Add(ensured);
            return new TransferResult(results);
        }

        foreach (var directory in Directory.EnumerateDirectories(localDirectory, "*", SearchOption.AllDirectories))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                results.Add(Cancelled(directory, remotePrefix));
                return new TransferResult(results);
            }

            var target = ObjectStoragePath.Combine(remotePrefix, Path.GetRelativePath(localDirectory, directory));
            var directoryResult = await EnsureFolderAsync(target, cancellationToken).ConfigureAwait(false);
            if (directoryResult is not null)
            {
                results.Add(directoryResult);
            }
        }

        foreach (var file in Directory.EnumerateFiles(localDirectory, "*", SearchOption.AllDirectories))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                results.Add(Cancelled(file, remotePrefix));
                return new TransferResult(results);
            }

            results.Add(await UploadFileAsync(
                file,
                ObjectStoragePath.Combine(remotePrefix, Path.GetRelativePath(localDirectory, file)),
                progress,
                cancellationToken).ConfigureAwait(false));
        }

        return new TransferResult(results);
    }

    private async Task<TransferItemResult?> EnsureFolderAsync(string path, CancellationToken cancellationToken)
    {
        // A container is never created: ADR-0019 refuses bucket creation outright, so uploading into a
        // container root asks for nothing.
        if (ObjectStoragePath.Split(path).Key.Length == 0)
        {
            return null;
        }

        var created = await _storage.CreateFolderAsync(path, cancellationToken).ConfigureAwait(false);
        return created.IsSuccess || created.Failure.Error == SftpError.AlreadyExists
            ? null
            : Failed(path, path, created.Failure);
    }

    private async Task<TransferItemResult> UploadFileAsync(
        string localPath,
        string remotePath,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var existing = await _storage.StatAsync(remotePath, cancellationToken).ConfigureAwait(false);
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

        var length = new FileInfo(localPath).Length;
        var reporter = new ProgressReporter(progress, localPath, remotePath, length, _time, _options.ProgressInterval);
        reporter.Start();
        try
        {
            return length <= _options.SingleShotThreshold
                ? await UploadSingleShotAsync(localPath, remotePath, length, reporter, cancellationToken)
                    .ConfigureAwait(false)
                : await UploadInPartsAsync(localPath, remotePath, length, reporter, cancellationToken)
                    .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Cancelled(localPath, remotePath);
        }
        catch (Exception exception)
        {
            return Failed(localPath, remotePath, new SftpFailure(SftpError.Unknown, exception.Message));
        }
    }

    private async Task<TransferItemResult> UploadSingleShotAsync(
        string localPath,
        string remotePath,
        long length,
        ProgressReporter reporter,
        CancellationToken cancellationToken)
    {
        var written = await ExecuteWithRetryAsync(
            async token =>
            {
                var file = new FileStream(
                    localPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    _options.CopyBufferSize,
                    useAsync: true);
                await using (file.ConfigureAwait(false))
                {
                    var counting = new CountingReadStream(file, reporter.Advance);
                    await using (counting.ConfigureAwait(false))
                    {
                        return await _storage
                            .WriteAsync(remotePath, counting, length, token)
                            .ConfigureAwait(false);
                    }
                }
            },
            cancellationToken).ConfigureAwait(false);

        if (written.IsFailure)
        {
            return Failed(localPath, remotePath, written.Failure);
        }

        reporter.Complete(length);
        return Completed(localPath, remotePath);
    }

    private async Task<TransferItemResult> UploadInPartsAsync(
        string localPath,
        string remotePath,
        long length,
        ProgressReporter reporter,
        CancellationToken cancellationToken)
    {
        // Refused before the first network call: an object no ladder can address should not cost a
        // CreateMultipartUpload that then has to be aborted.
        var preflight = ObjectPartPlanner.Plan(length, _options.PartLimits);
        if (preflight.IsFailure)
        {
            return Failed(localPath, remotePath, preflight.Failure);
        }

        var started = await _storage.StartUploadAsync(remotePath, cancellationToken).ConfigureAwait(false);
        if (started.IsFailure)
        {
            return Failed(localPath, remotePath, started.Failure);
        }

        var session = started.Value;
        await using (session.ConfigureAwait(false))
        {
            SftpFailure? failure = null;
            var cancelled = false;
            try
            {
                var planned = ObjectPartPlanner.Plan(length, ObjectPartLimits.From(session));
                if (planned.IsFailure)
                {
                    failure = planned.Failure;
                }
                else
                {
                    failure = await RunPartsAsync(session, localPath, planned.Value, reporter, cancellationToken)
                        .ConfigureAwait(false);
                    if (failure is null)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var completed = await session.CompleteAsync(cancellationToken).ConfigureAwait(false);
                        if (completed.IsSuccess)
                        {
                            reporter.Complete(length);
                            return Completed(localPath, remotePath);
                        }

                        failure = completed.Failure;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            catch (Exception exception)
            {
                failure = new SftpFailure(SftpError.Unknown, exception.Message);
            }

            // Every exit that is not "Complete returned success" aborts, including Complete itself
            // failing — the case that leaves the most parts behind, and the one most often forgotten.
            var aborted = await AbortAsync(session).ConfigureAwait(false);
            cancelled = cancelled || cancellationToken.IsCancellationRequested ||
                failure?.Error == SftpError.Cancelled;
            return cancelled
                ? Cancelled(localPath, remotePath, aborted)
                : Failed(
                    localPath,
                    remotePath,
                    Annotate(failure ?? new SftpFailure(SftpError.Unknown, "The upload failed."), aborted));
        }
    }

    private async Task<SftpFailure?> RunPartsAsync(
        IObjectUpload session,
        string localPath,
        ObjectPartPlan plan,
        ProgressReporter reporter,
        CancellationToken cancellationToken)
    {
        var aggregator = new PartProgress(plan.Parts.Count);
        using var gate = new SemaphoreSlim(_options.MaxPartsInFlight, _options.MaxPartsInFlight);
        using var failFast = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tasks = new Task<SftpResult>[plan.Parts.Count];
        for (var index = 0; index < plan.Parts.Count; index++)
        {
            tasks[index] = UploadPartAsync(
                session,
                localPath,
                plan.Parts[index],
                index,
                aggregator,
                reporter,
                gate,
                failFast);
        }

        return FirstFailure(await Task.WhenAll(tasks).ConfigureAwait(false));
    }

    private async Task<SftpResult> UploadPartAsync(
        IObjectUpload session,
        string localPath,
        ObjectPart part,
        int index,
        PartProgress aggregator,
        ProgressReporter reporter,
        SemaphoreSlim gate,
        CancellationTokenSource failFast)
    {
        try
        {
            await gate.WaitAsync(failFast.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return CancelledPart();
        }

        try
        {
            var result = await ExecuteWithRetryAsync(
                token => session.UploadPartAsync(
                    part.PartNumber,
                    part.Length,
                    // A factory, never a bare stream: a retried part needs a fresh one, and each in-flight
                    // part reads through its own file handle so nothing buffers a part.
                    _ => ValueTask.FromResult<Stream>(new CountingReadStream(
                        new BoundedFileSegmentStream(localPath, part.Offset, part.Length, _options.CopyBufferSize),
                        bytes =>
                        {
                            aggregator.Set(index, bytes);
                            reporter.Advance(aggregator.Total);
                        })),
                    token),
                failFast.Token).ConfigureAwait(false);
            if (result.IsFailure)
            {
                await failFast.CancelAsync().ConfigureAwait(false);
            }
            else
            {
                aggregator.Set(index, part.Length);
                reporter.Advance(aggregator.Total);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            return CancelledPart();
        }
        catch (Exception exception)
        {
            await failFast.CancelAsync().ConfigureAwait(false);
            return SftpResult.Fail(SftpError.Unknown, exception.Message);
        }
        finally
        {
            _ = gate.Release();
        }
    }

    private async Task DownloadPrefixAsync(
        string remotePrefix,
        string localDirectory,
        List<TransferItemResult> results,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        _ = Directory.CreateDirectory(localDirectory);

        // The listing is paged rather than materialised: a bucket with a million keys is ordinary.
        var paging = new ObjectStoragePaging();
        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                results.Add(Cancelled(remotePrefix, localDirectory));
                return;
            }

            var page = await _storage.ListAsync(remotePrefix, paging, cancellationToken).ConfigureAwait(false);
            if (page.IsFailure)
            {
                results.Add(Failed(remotePrefix, localDirectory, page.Failure));
                return;
            }

            foreach (var entry in page.Value.Entries)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    results.Add(Cancelled(entry.Path, localDirectory));
                    return;
                }

                var target = Path.Combine(localDirectory, entry.Name);
                if (entry.IsDirectory)
                {
                    await DownloadPrefixAsync(entry.Path, target, results, progress, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    results.Add(await DownloadFileAsync(entry, target, progress, cancellationToken)
                        .ConfigureAwait(false));
                }
            }

            if (page.Value.ContinuationToken is not { Length: > 0 } next)
            {
                return;
            }

            paging = paging with { ContinuationToken = next };
        }
    }

    private async Task<TransferItemResult> DownloadFileAsync(
        ObjectEntry entry,
        string localPath,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var exists = File.Exists(localPath);
        var conflict = await ResolveConflictAsync(
            TransferDirection.Download,
            entry.Path,
            localPath,
            exists ? new FileInfo(localPath).Length : null,
            exists,
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
        var reporter = new ProgressReporter(
            progress,
            entry.Path,
            localPath,
            entry.Size,
            _time,
            _options.ProgressInterval);
        reporter.Start();
        try
        {
            // A zero or unknown length has no ranges to plan, so it takes the single-stream path and
            // issues no ranged request at all.
            var result = entry.Size <= 0 || entry.Size <= _options.SingleShotThreshold
                ? await DownloadWholeAsync(entry, temporaryPath, reporter, cancellationToken).ConfigureAwait(false)
                : await DownloadInRangesAsync(entry, temporaryPath, reporter, cancellationToken)
                    .ConfigureAwait(false);
            if (result.IsFailure)
            {
                BestEffortDeleteLocal(temporaryPath);
                return result.Failure.Error == SftpError.Cancelled
                    ? Cancelled(entry.Path, localPath)
                    : Failed(entry.Path, localPath, result.Failure);
            }

            File.Move(temporaryPath, localPath, overwrite: true);
            reporter.Complete(entry.Size);
            return Completed(entry.Path, localPath);
        }
        catch (OperationCanceledException)
        {
            BestEffortDeleteLocal(temporaryPath);
            return Cancelled(entry.Path, localPath);
        }
        catch (Exception exception)
        {
            BestEffortDeleteLocal(temporaryPath);
            return Failed(entry.Path, localPath, new SftpFailure(SftpError.Unknown, exception.Message));
        }
    }

    private Task<SftpResult> DownloadWholeAsync(
        ObjectEntry entry,
        string temporaryPath,
        ProgressReporter reporter,
        CancellationToken cancellationToken)
    {
        return ExecuteWithRetryAsync(
            async token =>
            {
                var opened = await _storage.OpenReadAsync(entry.Path, null, token).ConfigureAwait(false);
                if (opened.IsFailure)
                {
                    return opened;
                }

                var source = opened.Value;
                await using (source.ConfigureAwait(false))
                {
                    var destination = new FileStream(
                        temporaryPath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        _options.CopyBufferSize,
                        useAsync: true);
                    await using (destination.ConfigureAwait(false))
                    {
                        var buffer = new byte[_options.CopyBufferSize];
                        var transferred = 0L;
                        while (true)
                        {
                            var read = await source.ReadAsync(buffer, token).ConfigureAwait(false);
                            if (read == 0)
                            {
                                break;
                            }

                            await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                            transferred += read;
                            reporter.Advance(transferred);
                        }

                        await destination.FlushAsync(token).ConfigureAwait(false);
                    }
                }

                return SftpResult.Success();
            },
            cancellationToken);
    }

    private async Task<SftpResult> DownloadInRangesAsync(
        ObjectEntry entry,
        string temporaryPath,
        ProgressReporter reporter,
        CancellationToken cancellationToken)
    {
        var planned = ObjectPartPlanner.Plan(entry.Size, _options.PartLimits);
        if (planned.IsFailure)
        {
            return planned;
        }

        var plan = planned.Value;

        // One preallocated .part, written at absolute offsets. SetLength up front fails fast on a full
        // disk, so the user learns there is no room for a 500 GB object before transferring 499 GB of it;
        // per-range temp files concatenated afterwards would double the write volume, need twice the free
        // space, and add a long non-cancellable phase with the bar pinned at 100%.
        using var handle = File.OpenHandle(
            temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            FileOptions.Asynchronous);
        RandomAccess.SetLength(handle, entry.Size);

        var aggregator = new PartProgress(plan.Parts.Count);
        using var gate = new SemaphoreSlim(_options.MaxPartsInFlight, _options.MaxPartsInFlight);
        using var failFast = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tasks = new Task<SftpResult>[plan.Parts.Count];
        for (var index = 0; index < plan.Parts.Count; index++)
        {
            tasks[index] = DownloadRangeAsync(
                entry,
                handle,
                plan.Parts[index],
                index,
                aggregator,
                reporter,
                gate,
                failFast);
        }

        var failure = FirstFailure(await Task.WhenAll(tasks).ConfigureAwait(false));
        return failure is null ? SftpResult.Success() : SftpResult.Fail(failure.Error, failure.Message);
    }

    private async Task<SftpResult> DownloadRangeAsync(
        ObjectEntry entry,
        SafeFileHandle handle,
        ObjectPart part,
        int index,
        PartProgress aggregator,
        ProgressReporter reporter,
        SemaphoreSlim gate,
        CancellationTokenSource failFast)
    {
        try
        {
            await gate.WaitAsync(failFast.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return CancelledPart();
        }

        try
        {
            var result = await ExecuteWithRetryAsync(
                async token =>
                {
                    var opened = await _storage.OpenReadAsync(
                        entry.Path,
                        new ObjectReadOptions
                        {
                            Offset = part.Offset,
                            Length = part.Length,
                            IfMatchETag = entry.ETag,
                        },
                        token).ConfigureAwait(false);
                    if (opened.IsFailure)
                    {
                        return opened;
                    }

                    var source = opened.Value;
                    await using (source.ConfigureAwait(false))
                    {
                        var buffer = new byte[_options.CopyBufferSize];
                        var written = 0L;
                        while (written < part.Length)
                        {
                            var slice = (int)Math.Min(buffer.Length, part.Length - written);
                            var read = await source.ReadAsync(buffer.AsMemory(0, slice), token)
                                .ConfigureAwait(false);
                            if (read == 0)
                            {
                                break;
                            }

                            await RandomAccess.WriteAsync(
                                handle,
                                buffer.AsMemory(0, read),
                                part.Offset + written,
                                token).ConfigureAwait(false);
                            written += read;
                            aggregator.Set(index, written);
                            reporter.Advance(aggregator.Total);
                        }

                        return written == part.Length
                            ? SftpResult.Success()
                            : SftpResult.Fail(
                                SftpError.ConnectionLost,
                                "The ranged read ended before the range did.");
                    }
                },
                failFast.Token).ConfigureAwait(false);
            if (result.IsFailure)
            {
                await failFast.CancelAsync().ConfigureAwait(false);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            return CancelledPart();
        }
        catch (Exception exception)
        {
            await failFast.CancelAsync().ConfigureAwait(false);
            return SftpResult.Fail(SftpError.Unknown, exception.Message);
        }
        finally
        {
            _ = gate.Release();
        }
    }

    private async Task<SftpResult> ExecuteWithRetryAsync(
        Func<CancellationToken, Task<SftpResult>> operation,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var result = await operation(cancellationToken).ConfigureAwait(false);
            if (result.IsSuccess || attempt >= _options.MaxAttemptsPerPart || !IsTransient(result.Failure))
            {
                return result;
            }

            await Task.Delay(_options.RetryDelayFor(attempt), _time, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Always with a fresh token, never the cancelled one. Passing the cancelled token means the
    /// abort is itself cancelled and the parts survive — <em>the</em> bug in cancel-aborts-multipart code.
    /// The timeout keeps a dead network from hanging the cancel.</summary>
    private async Task<SftpResult> AbortAsync(IObjectUpload session)
    {
        using var timeout = new CancellationTokenSource(_options.AbortTimeout, _time);
        try
        {
            return await session.AbortAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return SftpResult.Fail(SftpError.Unknown, exception.Message);
        }
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

    /// <summary>Per-part high-water marks. Progress is their sum, and the sum is only ever clamped upward
    /// by <see cref="ProgressReporter"/>: tracking the highest contiguous offset instead would leave the
    /// bar at zero for the whole transfer whenever part one happens to finish last.</summary>
    private sealed class PartProgress(int count)
    {
        private readonly long[] _bytes = new long[count];

        public long Total
        {
            get
            {
                var total = 0L;
                for (var index = 0; index < _bytes.Length; index++)
                {
                    total += Volatile.Read(ref _bytes[index]);
                }

                return total;
            }
        }

        public void Set(int index, long value)
        {
            var current = Volatile.Read(ref _bytes[index]);
            while (value > current)
            {
                var seen = Interlocked.CompareExchange(ref _bytes[index], value, current);
                if (seen == current)
                {
                    return;
                }

                current = seen;
            }
        }
    }

    private sealed class ProgressReporter(
        IProgress<TransferProgress>? progress,
        string sourcePath,
        string destinationPath,
        long totalBytes,
        TimeProvider time,
        TimeSpan interval)
    {
        private readonly TransferRateMeter _meter = new(time);
        private readonly Lock _sync = new();
        private long _lastReported;
        private long _lastReportedAt;
        private bool _started;

        public void Start()
        {
            Emit(0, isCompleted: false, force: true);
        }

        public void Advance(long transferred)
        {
            Emit(transferred, isCompleted: false, force: false);
        }

        public void Complete(long transferred)
        {
            Emit(transferred, isCompleted: true, force: true);
        }

        private void Emit(long transferred, bool isCompleted, bool force)
        {
            if (progress is null)
            {
                return;
            }

            // Reported under the lock, not merely computed under it: parts report from many threads at
            // once, and releasing the lock first would let a later value reach the sink ahead of an
            // earlier one — a bar that goes backwards for exactly the reason the clamp exists to prevent.
            lock (_sync)
            {
                var value = Math.Max(_lastReported, transferred);
                _lastReported = value;
                var now = time.GetTimestamp();
                if (!force && _started && time.GetElapsedTime(_lastReportedAt, now) < interval)
                {
                    return;
                }

                _started = true;
                _lastReportedAt = now;
                _meter.Record(value);
                progress.Report(new TransferProgress(
                    sourcePath,
                    destinationPath,
                    value,
                    totalBytes,
                    _meter.BytesPerSecond,
                    isCompleted ? TimeSpan.Zero : _meter.EstimateRemaining(value, totalBytes),
                    isCompleted));
            }
        }
    }
}

#pragma warning restore IDE0046
