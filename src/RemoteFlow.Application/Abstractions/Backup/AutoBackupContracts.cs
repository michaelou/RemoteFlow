using RemoteFlow.Application.Abstractions.Sftp;
using RemoteFlow.Domain.Common;

namespace RemoteFlow.Application.Abstractions.Backup;

public enum AutoBackupDestinationKind
{
    LocalFolder = 1,
    SftpConnection = 2,
    ObjectStorageConnection = 3,
}

public enum AutoBackupOutcome
{
    Succeeded = 1,
    Failed = 2,

    /// <summary>The configuration says to back up but something the user controls is missing — almost
    /// always the passphrase. Distinct from <see cref="Failed"/> because it is a state to fix on the Backup
    /// page, not a transport error to retry.</summary>
    Blocked = 3,
}

public sealed record AutoBackupDestination
{
    public AutoBackupDestinationKind Kind { get; init; } = AutoBackupDestinationKind.LocalFolder;

    public string? LocalFolder { get; init; }

    public Guid? ConnectionId { get; init; }

    public string? RemotePath { get; init; }
}

/// <summary>Held as a single settings row rather than one row per field. Enabling automatic backup and
/// choosing where it writes is one gesture, and three separate writes would leave a window in which the
/// runner could read "enabled" alongside a destination that had not landed yet.</summary>
public sealed record AutoBackupOptions
{
    public const int MinimumRetainedCopies = 1;
    public const int MaximumRetainedCopies = 365;
    public const int DefaultRetainedCopies = 10;

    public static AutoBackupOptions Disabled { get; } = new();

    public bool IsEnabled { get; init; }

    public int RetainedCopies { get; init; } = DefaultRetainedCopies;

    public AutoBackupDestination Destination { get; init; } = new();

    /// <summary>Always read retention through this. Settings travel inside backup archives, so this value
    /// can arrive from another machine — and a zero reaching the pruner would read as "keep nothing".</summary>
    public int ClampedRetainedCopies =>
        Math.Clamp(RetainedCopies, MinimumRetainedCopies, MaximumRetainedCopies);
}

public sealed record AutoBackupStatus
{
    public DateTimeOffset RunUtc { get; init; }

    public AutoBackupOutcome Outcome { get; init; }

    public string Destination { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public string? ArchiveName { get; init; }

    public int PrunedCopies { get; init; }

    /// <summary>Set when a change has been seen but not yet backed up, and cleared by a successful run.
    /// Written before the quiet period rather than after it, so a crash or a quit inside those 30 seconds
    /// still leaves the evidence that lets the next launch catch up.</summary>
    public bool PendingChanges { get; init; }
}

public sealed record AutoBackupArchive(string Name, string Path, DateTimeOffset CreatedUtc, long Size);

/// <summary>One place automatic backups are written. Opened per run and disposed with it: an SFTP
/// destination holds a live SSH connection open, and runs are minutes apart at best.</summary>
public interface IAutoBackupDestination : IAsyncDisposable
{
    /// <summary>What the Backup page shows the user, e.g. <c>sftp://backup-01:22/srv/backups</c>.</summary>
    string Description { get; }

    /// <summary>Where the archive is built before it is published. A local destination stages inside its
    /// own folder so publishing is a rename; a remote one stages under the cache directory.</summary>
    string GetStagingPath(string fileName);

    Task<SftpResult> PublishAsync(string stagingPath, string fileName, CancellationToken cancellationToken = default);

    Task<SftpResult<IReadOnlyList<AutoBackupArchive>>> ListAsync(CancellationToken cancellationToken = default);

    Task<SftpResult> DeleteAsync(AutoBackupArchive archive, CancellationToken cancellationToken = default);
}

public interface IAutoBackupDestinationFactory
{
    Task<SftpResult<IAutoBackupDestination>> CreateAsync(
        AutoBackupDestination destination,
        CancellationToken cancellationToken = default);
}

/// <summary>What the passphrase store can tell the rest of the app about itself. "Locked" and "not set" are
/// deliberately different answers: a credential store that will not open is a problem to report, whereas an
/// absent passphrase is an invitation to set one. Telling somebody with a locked vault that no passphrase is
/// set sends them to type a new one that cannot be saved either.</summary>
public sealed record AutoBackupPassphraseState(bool HasPassphrase, string? Problem)
{
    public static AutoBackupPassphraseState Missing { get; } = new(false, null);

    public static AutoBackupPassphraseState Present { get; } = new(true, null);

    public bool IsUsable => Problem is null;
}

/// <summary>The passphrase automatic backups encrypt credentials with. It lives in the OS keychain and
/// never in the database, which is also what stops an imported configuration from silently backing another
/// machine's data up to a stranger's server: the archive can carry <c>IsEnabled = true</c>, but it cannot
/// carry the passphrase, so the run is blocked.</summary>
public interface IAutoBackupPassphraseStore
{
    bool IsAvailable { get; }

    Task<string> GetProviderNameAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether a passphrase is stored, and — if the store itself is unusable — why. Never throws:
    /// credential stores are platform integrations, and a settings page must not crash because a keyring is
    /// locked or a D-Bus call failed.</summary>
    Task<AutoBackupPassphraseState> InspectAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads the secret, or null if there is not one to read for any reason. Only the runner calls
    /// this — the UI gets <see cref="InspectAsync"/>, because a view model that cannot read a secret cannot
    /// leak it. Also never throws; ask <see cref="InspectAsync"/> for the reason behind a null.</summary>
    Task<SecretHandle?> GetAsync(CancellationToken cancellationToken = default);

    Task<Result<bool>> SetAsync(ReadOnlyMemory<char> passphrase, CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}

/// <summary>Where the last run's outcome is remembered. Deliberately not a settings row: settings are
/// exported into every archive, so importing one would install another machine's "last run succeeded" —
/// the one thing this feature must never claim falsely.</summary>
public interface IAutoBackupStatusStore
{
    Task<AutoBackupStatus?> ReadAsync(CancellationToken cancellationToken = default);

    Task WriteAsync(AutoBackupStatus status, CancellationToken cancellationToken = default);
}

public interface IAutoBackupRunner
{
    AutoBackupStatus? LastStatus { get; }

    /// <summary>Raised on whichever thread the run finished on. A UI subscriber has to marshal.</summary>
    event EventHandler? StatusChanged;

    /// <summary>Clears staging files left behind by a run that was killed. Named for the convention
    /// already used by remote edit, RDP and the updater.</summary>
    Task SweepStaleFilesAsync(CancellationToken cancellationToken = default);

    /// <summary>Subscribes to change signals and, if a backup is owed, arms one. Must not await a backup:
    /// it runs during startup, ahead of the first paint.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>Runs one backup immediately, skipping the quiet period. Backs the "Back up now" button,
    /// which is how a typed remote path gets verified without waiting for a real edit.</summary>
    Task<AutoBackupStatus> RunNowAsync(CancellationToken cancellationToken = default);

    /// <summary>Completes once the pending quiet period and any in-flight run have finished. For tests.</summary>
    Task DrainAsync(CancellationToken cancellationToken = default);
}
