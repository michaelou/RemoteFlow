namespace RemoteFlow.Application.Abstractions;

/// <summary>An installer that has been downloaded and whose SHA-256 matched the digest the release
/// published for it.
///
/// This type exists so that the method which executes a file cannot be handed an arbitrary path. The only
/// way to obtain one is to have run <see cref="IUpdateInstaller.DownloadAsync"/> through to a verified
/// end, which makes "nothing unverified is ever run" a property of the types rather than of remembering to
/// check.</summary>
public sealed record VerifiedUpdate(string InstallerPath, string Version, string Sha256);

/// <summary>Either a verified installer or a sentence explaining why there is not one. Nothing here throws
/// on a failure the user can do something about, for the same reason <see cref="UpdateCheckResult"/> does
/// not: a download that did not finish is something to say, not something to raise.</summary>
public sealed record UpdateDownloadResult(VerifiedUpdate? Update, string? ErrorMessage = null)
{
    public bool Succeeded => Update is not null;

    public static UpdateDownloadResult Verified(VerifiedUpdate update)
    {
        return new(update);
    }

    public static UpdateDownloadResult Failed(string errorMessage)
    {
        return new(null, errorMessage);
    }
}

/// <summary>Downloads a release installer, proves it is the file the release published, and runs it once
/// RemoteFlow has stopped.
///
/// Deliberately several steps rather than one call. Downloading has to be cancellable and report progress;
/// verifying is the entire security argument and has to be able to fail loudly; and executing must not
/// happen while this process still holds the files the installer is about to replace. So the installer is
/// queued by <see cref="ScheduleInstall"/> and only started by <see cref="RunPendingInstall"/>, which the
/// entry point calls on its way out, after the window has closed and the host has stopped.</summary>
public interface IUpdateInstaller
{
    /// <summary>Whether this build is allowed to replace itself: a per-user install running from the
    /// directory its own uninstall entry names. False for a portable copy, a copy that has been moved, a
    /// build output directory, and every platform RemoteFlow publishes no installer for.</summary>
    bool CanInstall { get; }

    /// <summary>Why not, in a sentence, when <see cref="CanInstall"/> is false.</summary>
    string? Unavailable { get; }

    /// <summary>Fetches the release's <c>checksums.txt</c>, streams the installer next to it, and compares
    /// the hash of what arrived with the digest that file records for that filename. Reports progress from
    /// 0 to 1. A download that cannot be verified leaves nothing on disk.</summary>
    Task<UpdateDownloadResult> DownloadAsync(
        UpdatePackage package,
        string version,
        IProgress<double>? progress,
        CancellationToken cancellationToken = default);

    /// <summary>Queues an installer to run at shutdown. Nothing starts here.</summary>
    void ScheduleInstall(VerifiedUpdate update);

    /// <summary>Runs whatever <see cref="ScheduleInstall"/> queued, at most once. Called from the entry
    /// point's teardown, after the host has stopped, so the process is moments from exiting and releasing
    /// the files the installer will replace.</summary>
    void RunPendingInstall();

    /// <summary>Reports an update that was started and did not arrive, then forgets it. Returns null when
    /// the last update either succeeded or never happened.
    ///
    /// A failed install is the one failure RemoteFlow cannot observe as it happens, because it is not
    /// running: Inno Setup rolls back by removing what it installed rather than by restoring what it
    /// replaced, so the worst case leaves no application at all. This is how the next launch — from the
    /// Start menu shortcut, which survives — gets to say so and name the log.</summary>
    Task<string?> TakeFailedUpdateReportAsync(CancellationToken cancellationToken = default);

    /// <summary>Deletes installers left in the cache by an earlier update. Called at startup, beside the
    /// other sweeps, and deliberately not straight after an install: the downloaded file is the only way
    /// back if the install destroyed what it was replacing.</summary>
    Task SweepStaleFilesAsync(CancellationToken cancellationToken = default);
}
