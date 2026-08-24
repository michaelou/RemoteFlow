using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Backup;
using RemoteFlow.Domain.Abstractions;

namespace RemoteFlow.Application.Services.Backup;

/// <summary>Watches for changes to connections, folders and tags, waits for them to stop, and writes one
/// backup. Everything awkward about this class comes from one fact: the change signals are plain
/// synchronous events raised from inside a save, sometimes with a SQLite write transaction still open. So
/// <see cref="Schedule"/> does no I/O, takes no lock a run holds, and swallows everything — a backup that
/// goes wrong must never surface as a connection that failed to save.</summary>
public sealed class AutoBackupRunner(
    ISettingsStore settings,
    IBackupService backup,
    IAutoBackupDestinationFactory destinations,
    IAutoBackupPassphraseStore passphrases,
    IAutoBackupStatusStore statusStore,
    IConnectionChangeNotifier connectionChanges,
    IWorkspaceChangeNotifier workspaceChanges,
    IClock clock,
    IGuidProvider guids,
    IAppPaths paths,
    TimeProvider? time = null) : IAutoBackupRunner, IDisposable
{
    /// <summary>Fixed, not configurable. One save of a connection raises three or four separate signals and
    /// a drag-reorder raises two per item, so the wait is not a nicety — without it a single edit would
    /// produce a fistful of archives.</summary>
    public static TimeSpan QuietPeriod { get; } = TimeSpan.FromSeconds(30);

    private readonly ISettingsStore _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    private readonly IBackupService _backup = backup ?? throw new ArgumentNullException(nameof(backup));
    private readonly IAutoBackupDestinationFactory _destinations = destinations ?? throw new ArgumentNullException(nameof(destinations));
    private readonly IAutoBackupPassphraseStore _passphrases = passphrases ?? throw new ArgumentNullException(nameof(passphrases));
    private readonly IAutoBackupStatusStore _statusStore = statusStore ?? throw new ArgumentNullException(nameof(statusStore));
    private readonly IConnectionChangeNotifier _connectionChanges = connectionChanges ?? throw new ArgumentNullException(nameof(connectionChanges));
    private readonly IWorkspaceChangeNotifier _workspaceChanges = workspaceChanges ?? throw new ArgumentNullException(nameof(workspaceChanges));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly IGuidProvider _guids = guids ?? throw new ArgumentNullException(nameof(guids));
    private readonly IAppPaths _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly Lock _sync = new();
    private readonly CancellationTokenSource _shutdown = new();

    private CancellationTokenSource? _debounce;
    private Task _pending = Task.CompletedTask;
    private AutoBackupStatus? _status;
    private int _initialized;
    private int _disposed;

    public event EventHandler? StatusChanged;

    public AutoBackupStatus? LastStatus => Volatile.Read(ref _status);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0)
        {
            return;
        }

        _status = await _statusStore.ReadAsync(cancellationToken).ConfigureAwait(false);
        _connectionChanges.ConnectionChanged += OnConnectionChanged;
        _workspaceChanges.WorkspaceChanged += OnWorkspaceChanged;

        var options = await ReadOptionsAsync(cancellationToken).ConfigureAwait(false);
        // RemoteFlow is the only writer of this database, so if the last run succeeded and nothing has
        // changed since, the data still matches that archive. What is left is a run cut short by a quit or
        // a crash, and a run that failed or was blocked — which is exactly what the status records.
        var due = options.IsEnabled &&
            (_status is null || _status.PendingChanges || _status.Outcome != AutoBackupOutcome.Succeeded);
        if (due)
        {
            // Scheduled rather than run outright: startup itself can produce signals, and they should
            // coalesce into the same archive.
            Schedule();
        }
    }

    public async Task<AutoBackupStatus> RunNowAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, cancellationToken);
        return await RunCoreAsync(linked.Token).ConfigureAwait(false);
    }

    public async Task DrainAsync(CancellationToken cancellationToken = default)
    {
        Task pending;
        lock (_sync)
        {
            pending = _pending;
        }

        try
        {
            await pending.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A superseded quiet period is the normal way this ends.
        }

        await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        _ = _runGate.Release();
    }

    public Task SweepStaleFilesAsync(CancellationToken cancellationToken = default)
    {
        // A run killed at shutdown leaves a part-built archive behind. There is no age rule to apply: it
        // holds nothing that is not already in the database, so anything here is safe to remove.
        var staging = Path.Combine(_paths.CacheDirectory, AutoBackupDestinationFactory.StagingDirectoryName);
        try
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Leftovers are wasted disk, not a reason to fail startup.
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _connectionChanges.ConnectionChanged -= OnConnectionChanged;
        _workspaceChanges.WorkspaceChanged -= OnWorkspaceChanged;
        // A run in flight is cancelled rather than awaited. The archive is a full snapshot and the next
        // launch will make another; holding process exit open on an SSH handshake is the worse trade.
        _shutdown.Cancel();
        lock (_sync)
        {
            _debounce?.Dispose();
            _debounce = null;
        }

        _shutdown.Dispose();
        _runGate.Dispose();
    }

    private void OnConnectionChanged(object? sender, ConnectionChangedEventArgs e)
    {
        // Reloaded means a backup import just rewrote the store. Backing that up would overwrite the newest
        // archive with a copy of what was restored, and the configuration itself may have just been
        // replaced by another machine's — not a state to start writing from.
        if (e.Kind == ConnectionChangeKind.Reloaded)
        {
            return;
        }

        Schedule();
    }

    private void OnWorkspaceChanged(object? sender, WorkspaceChangedEventArgs e)
    {
        Schedule();
    }

    /// <summary>Arms the quiet period, restarting it if it was already running. Called synchronously from a
    /// domain event — no await, no I/O, no repository access, and nothing thrown.</summary>
    private void Schedule()
    {
        if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _initialized) == 0)
        {
            return;
        }

        try
        {
            CancellationTokenSource? superseded;
            lock (_sync)
            {
                superseded = _debounce;
                _debounce = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                // Deliberately not awaited: the caller is part-way through saving a connection. _pending
                // exists so DrainAsync and the tests can wait where the caller must not.
                _pending = DebounceThenRunAsync(_debounce.Token);
            }

            // Cancelled outside the lock so a continuation can never run while it is held.
            superseded?.Cancel();
            superseded?.Dispose();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            RecordSynchronously(AutoBackupOutcome.Failed, string.Empty, exception.Message);
        }
    }

    private async Task DebounceThenRunAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Marked before the wait, not after. A crash or a quit inside these 30 seconds has to leave
            // evidence that the data moved on, or the next launch has no way to know a backup is owed.
            await MarkPendingAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(QuietPeriod, _time, cancellationToken).ConfigureAwait(false);
            _ = await RunCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a later change, or shutting down. Neither is a failure.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _ = await RecordAsync(
                AutoBackupOutcome.Failed, string.Empty, exception.Message, pending: true, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private async Task<AutoBackupStatus> RunCoreAsync(CancellationToken cancellationToken)
    {
        // Waits rather than skips: a run already in flight may have captured its snapshot before the change
        // that triggered this one, and the wait is bounded by that run.
        await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Read fresh every run. An import rewrites this row through the import store rather than the
            // settings store, so SettingChanged never fires and a cached copy would quietly go stale.
            var options = await ReadOptionsAsync(cancellationToken).ConfigureAwait(false);
            if (!options.IsEnabled)
            {
                return await RecordAsync(
                    AutoBackupOutcome.Blocked,
                    string.Empty,
                    "Automatic backup is turned off.",
                    pending: false,
                    cancellationToken).ConfigureAwait(false);
            }

            using var passphrase = await _passphrases.GetAsync(cancellationToken).ConfigureAwait(false);
            if (passphrase is null)
            {
                // Never silently downgrade to a credential-free archive: that would leave the user believing
                // they hold a backup they can actually restore from. This is also what stops an imported
                // configuration from backing this machine's data up somewhere it should not go.
                // Asked only on this path, so the ordinary run still costs one credential read.
                var state = await _passphrases.InspectAsync(cancellationToken).ConfigureAwait(false);
                return await RecordAsync(
                    AutoBackupOutcome.Blocked,
                    string.Empty,
                    state.Problem is null
                        ? "Automatic backup is on but no passphrase is set on this machine. Set one on the Backup page."
                        : $"Automatic backup cannot read its passphrase: {state.Problem}",
                    pending: true,
                    cancellationToken).ConfigureAwait(false);
            }

            var opened = await _destinations.CreateAsync(options.Destination, cancellationToken).ConfigureAwait(false);
            if (opened.IsFailure)
            {
                return await RecordAsync(
                    AutoBackupOutcome.Failed,
                    string.Empty,
                    opened.Failure.Message,
                    pending: true,
                    cancellationToken).ConfigureAwait(false);
            }

            await using var destination = opened.Value;
            var fileName = AutoBackupNaming.Create(_clock.UtcNow, _guids.NewGuid());
            var staging = destination.GetStagingPath(fileName);
            try
            {
                var export = await _backup.ExportAsync(
                    new BackupExportRequest(
                        staging,
                        BackupExportScope.All,
                        IncludeSettings: true,
                        IncludeHostKeys: true,
                        IncludeCredentials: true,
                        IncludeMachineName: true,
                        CredentialPassphrase: passphrase.Secret,
                        AllowWeakPassphrase: false),
                    progress: null,
                    cancellationToken).ConfigureAwait(false);

                var published = await destination
                    .PublishAsync(staging, fileName, cancellationToken).ConfigureAwait(false);
                if (published.IsFailure)
                {
                    return await RecordAsync(
                        AutoBackupOutcome.Failed,
                        destination.Description,
                        published.Failure.Message,
                        pending: true,
                        cancellationToken).ConfigureAwait(false);
                }

                var (prunedCopies, pruningDescription) = await PruneAsync(
                    destination, fileName, options.ClampedRetainedCopies, cancellationToken).ConfigureAwait(false);
                var counts = export.Counts;
                return await RecordAsync(
                    AutoBackupOutcome.Succeeded,
                    destination.Description,
                    $"Backed up {counts.Connections} connections, {counts.Folders} folders and " +
                        $"{counts.Tags} tags. {pruningDescription}",
                    pending: false,
                    cancellationToken,
                    fileName,
                    prunedCopies).ConfigureAwait(false);
            }
            finally
            {
                TryDeleteStaging(staging);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return await RecordAsync(
                AutoBackupOutcome.Failed, string.Empty, exception.Message, pending: true, CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                _ = _runGate.Release();
            }
        }
    }

    private static async Task<(int Deleted, string Description)> PruneAsync(
        IAutoBackupDestination destination,
        string justWritten,
        int retainedCopies,
        CancellationToken cancellationToken)
    {
        var listed = await destination.ListAsync(cancellationToken).ConfigureAwait(false);
        if (listed.IsFailure)
        {
            return (0, $"Older backups could not be listed for cleanup: {listed.Failure.Message}");
        }

        // If our own archive is not in the listing then this is not the view of the destination we think it
        // is, and deleting under that assumption is how every backup gets lost at once.
        if (!listed.Value.Any(archive => string.Equals(archive.Name, justWritten, StringComparison.Ordinal)))
        {
            return (0, "Older backups were left alone: the new archive was not visible in the destination listing.");
        }

        var ordered = listed.Value
            .OrderByDescending(archive => archive.CreatedUtc)
            .ThenByDescending(archive => archive.Name, StringComparer.Ordinal)
            .ToArray();
        var expired = ordered.Skip(retainedCopies).Reverse().ToArray();
        var deleted = 0;
        var failures = 0;
        foreach (var archive in expired)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var removed = await destination.DeleteAsync(archive, cancellationToken).ConfigureAwait(false);
            if (removed.IsSuccess)
            {
                deleted++;
            }
            else
            {
                failures++;
            }
        }

        // A destination that will not let us delete should still receive backups, so a failed prune is
        // reported alongside a successful run rather than turning it into a failed one.
        var description = failures > 0
            ? $"Kept the {retainedCopies} newest; {failures} older archives could not be deleted."
            : deleted > 0
                ? $"Kept the {retainedCopies} newest and removed {deleted} older."
                : $"Keeping the {retainedCopies} newest.";
        return (deleted, description);
    }

    private async Task<AutoBackupOptions> ReadOptionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _settings.Get(SettingKeys.AutoBackup, cancellationToken).ConfigureAwait(false)
                ?? AutoBackupOptions.Disabled;
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            return AutoBackupOptions.Disabled;
        }
    }

    private Task MarkPendingAsync(CancellationToken cancellationToken)
    {
        var current = LastStatus;
        return current is { PendingChanges: true }
            ? Task.CompletedTask
            : RecordAsync(
                current?.Outcome ?? AutoBackupOutcome.Blocked,
                current?.Destination ?? string.Empty,
                current?.Message ?? "Waiting to make the first automatic backup.",
                pending: true,
                cancellationToken,
                current?.ArchiveName,
                current?.PrunedCopies ?? 0,
                keepRunTime: current?.RunUtc);
    }

    private async Task<AutoBackupStatus> RecordAsync(
        AutoBackupOutcome outcome,
        string destination,
        string message,
        bool pending,
        CancellationToken cancellationToken,
        string? archiveName = null,
        int prunedCopies = 0,
        DateTimeOffset? keepRunTime = null)
    {
        var status = new AutoBackupStatus
        {
            RunUtc = keepRunTime ?? _clock.UtcNow,
            Outcome = outcome,
            Destination = destination,
            Message = message,
            ArchiveName = archiveName,
            PrunedCopies = prunedCopies,
            PendingChanges = pending,
        };
        Volatile.Write(ref _status, status);
        try
        {
            await _statusStore.WriteAsync(status, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            // Losing the record is worth far less than the archive, which is already written.
        }

        RaiseStatusChanged();
        return status;
    }

    private void RecordSynchronously(AutoBackupOutcome outcome, string destination, string message)
    {
        Volatile.Write(ref _status, new AutoBackupStatus
        {
            RunUtc = _clock.UtcNow,
            Outcome = outcome,
            Destination = destination,
            Message = message,
            PendingChanges = true,
        });
        RaiseStatusChanged();
    }

    private void RaiseStatusChanged()
    {
        try
        {
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A subscriber that throws is its own problem, not the backup's.
        }
    }

    private static void TryDeleteStaging(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Swept at the next launch.
        }
    }
}
