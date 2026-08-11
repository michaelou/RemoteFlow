using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Infrastructure.Platform;

namespace RemoteFlow.Infrastructure.Updates;

/// <summary>Runs a verified installer over the top of the running application.
///
/// The ordering is the design. Starting the installer and then trying to leave would race it against the
/// process whose files it is about to replace, and losing that race means the restart manager terminating
/// an application mid-write. So nothing is started here when the button is pressed: the installer is
/// queued, the application closes itself in the ordinary way, and <see cref="RunPendingInstall"/> is the
/// last thing the entry point does before returning.
///
/// The Inno-specific parts are unreachable off Windows by construction rather than by an
/// <c>OperatingSystem.IsWindows()</c> check in each of them: <see cref="CanInstall"/> is false for every
/// shape but <see cref="InstallShape.Installer"/>, and nothing but Windows can be that shape.</summary>
public sealed class UpdateInstaller : IUpdateInstaller
{
    private const string _markerFileName = "pending.json";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly IAppInstallInfo _install;
    private readonly IAppPaths _paths;
    private readonly IProcessRunner _processes;
    private readonly ReleaseAssetDownloader _downloader;
    private readonly ILogger<UpdateInstaller> _logger;
    private readonly Action _releaseRunningInstanceMutex;

    private VerifiedUpdate? _pending;
    private bool _started;

    public UpdateInstaller(
        IAppInstallInfo install,
        IAppPaths paths,
        IProcessRunner processes,
        ReleaseAssetDownloader downloader,
        ILogger<UpdateInstaller> logger)
        : this(install, paths, processes, downloader, logger, RunningInstanceMutex.Release)
    {
    }

    /// <summary>Takes the mutex release as a delegate so a test can assert it happens, and happens before
    /// the launch, without a real named mutex being involved.</summary>
    public UpdateInstaller(
        IAppInstallInfo install,
        IAppPaths paths,
        IProcessRunner processes,
        ReleaseAssetDownloader downloader,
        ILogger<UpdateInstaller> logger,
        Action releaseRunningInstanceMutex)
    {
        ArgumentNullException.ThrowIfNull(install);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(downloader);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(releaseRunningInstanceMutex);
        _install = install;
        _paths = paths;
        _processes = processes;
        _downloader = downloader;
        _logger = logger;
        _releaseRunningInstanceMutex = releaseRunningInstanceMutex;
    }

    public bool CanInstall => _install.Shape == InstallShape.Installer;

    public string? Unavailable => CanInstall ? null : _install.Explanation;

    public Task<UpdateDownloadResult> DownloadAsync(
        UpdatePackage package,
        string version,
        IProgress<double>? progress,
        CancellationToken cancellationToken = default)
    {
        return CanInstall
            ? _downloader.DownloadAsync(package, version, progress, cancellationToken)
            : Task.FromResult(UpdateDownloadResult.Failed(
                Unavailable ?? "This copy of RemoteFlow cannot install updates."));
    }

    public void ScheduleInstall(VerifiedUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (!CanInstall)
        {
            return;
        }

        _pending = update;
    }

    public void RunPendingInstall()
    {
        // At most once. The entry point's finally can run on a path where something else already asked.
        if (_pending is not { } update || _started)
        {
            return;
        }

        _started = true;
        try
        {
            // Setup writes its log here and does not create the directory; a missing one is a hard "failed
            // to initialize" before a single file is copied.
            _paths.EnsureDirectories();
            var logPath = Path.Combine(
                _paths.LogDirectory,
                $"update-{Sanitize(update.Version)}.log");
            WriteMarker(update, logPath);

            // Before the launch, not after: by the time Setup checks AppMutex, this process's answer has
            // to already be no, or it will stop and ask a user who is watching the window disappear.
            _releaseRunningInstanceMutex();

            var request = new ProcessLaunchRequest(
                update.InstallerPath,
                BuildArguments(logPath),
                // Never the install directory and never Setup's own temp directory: a process holding
                // either as its working directory keeps it from being cleaned up.
                WorkingDirectory: Path.GetTempPath(),
                UseShellExecute: false);

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Starting the {Version} installer at {Path}; logging to {Log}.",
                    update.Version,
                    update.InstallerPath,
                    logPath);
            }

            // The runner starts the process and returns without waiting, so this does not block the exit.
            _processes.RunAsync(request).GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            // There is no UI left to tell and no caller to return to — the window has gone and the host has
            // stopped. The marker file is what makes this visible at the next launch.
            _logger.LogError(exception, "The update installer could not be started.");
        }
    }

    /// <summary>The exact command line. Changing it is changing the update, so it is one method with one
    /// reason for each switch, and a test asserts the whole list.</summary>
    private static string[] BuildArguments(string logPath)
    {
        return
        [
            // A progress window and no questions. /VERYSILENT would leave the screen empty for the ten
            // seconds this takes, immediately after the application vanished, which reads as a crash.
            "/SILENT",

            // Cancelling half way through leaves a partly replaced install and no application on screen to
            // explain it.
            "/NOCANCEL",

            // Nothing here needs a reboot, and Setup must not be free to ask for one.
            "/NORESTART",

            // Not an Inno switch. Setup passes parameters it does not recognise through to [Code], where
            // the second [Run] entry's Check reads this one and relaunches RemoteFlow. Without it a silent
            // install — a CI smoke test, an unattended deployment — starts nothing, which is right.
            "/UPDATE",

            // The only record of what a failed install did, and the application will not be running to see
            // it happen.
            $"/LOG={logPath}",
        ];

        // Deliberately absent: /DIR, because Setup reads its own uninstall entry and lands where this copy
        // already is — and passing a directory would override that and could create a second install.
        // /TASKS, because UsePreviousTasks restores the desktop-icon choice the user actually made.
        // /SUPPRESSMSGBOXES, because this process has exited: a suppressed message is a silent failure with
        // nobody left to report it, and a suppressed answer defaults to Cancel.
    }

    public async Task<string?> TakeFailedUpdateReportAsync(CancellationToken cancellationToken = default)
    {
        var markerPath = Path.Combine(_downloader.DownloadDirectory, _markerFileName);
        PendingUpdate? marker;
        try
        {
            if (!File.Exists(markerPath))
            {
                return null;
            }

            var content = await File.ReadAllTextAsync(markerPath, cancellationToken).ConfigureAwait(false);
            marker = JsonSerializer.Deserialize<PendingUpdate>(content, _json);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }

        // Read once and cleared either way: a marker kept after it has been reported would say the same
        // thing at every launch forever.
        TryDelete(markerPath);
        if (marker is null || string.IsNullOrWhiteSpace(marker.Version))
        {
            return null;
        }

        // A marker whose version is the one now running means the install worked and it is just litter.
        var current = CurrentVersion();
        return string.Equals(marker.Version, current, StringComparison.Ordinal)
            ? null
            : string.Format(
                CultureInfo.CurrentCulture,
                "The update to RemoteFlow {0} did not finish, and this is still {1}. The installer's log is " +
                "at {2}, and the installer itself is still at {3} if you would like to run it yourself.",
                marker.Version,
                current,
                marker.LogPath,
                marker.InstallerPath);
    }

    public Task SweepStaleFilesAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () =>
            {
                var directory = _downloader.DownloadDirectory;
                if (!Directory.Exists(directory))
                {
                    return;
                }

                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // The marker is swept by TakeFailedUpdateReportAsync, which reads it first.
                    if (string.Equals(Path.GetFileName(file), _markerFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    TryDelete(file);
                }
            },
            cancellationToken);
    }

    /// <summary>Written before the application exits, because a failed install is the one failure RemoteFlow
    /// cannot watch happen. Inno rolls back by removing what it installed rather than by restoring what it
    /// replaced, so the bad case leaves no application at all — and the next launch, from the Start menu
    /// shortcut that survives, is the only chance to say what happened.</summary>
    private void WriteMarker(VerifiedUpdate update, string logPath)
    {
        try
        {
            _ = Directory.CreateDirectory(_downloader.DownloadDirectory);
            var marker = new PendingUpdate(update.Version, update.InstallerPath, logPath);
            File.WriteAllText(
                Path.Combine(_downloader.DownloadDirectory, _markerFileName),
                JsonSerializer.Serialize(marker, _json));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Not being able to record the attempt is no reason not to make it. It only costs the report.
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(exception, "The pending-update marker could not be written.");
            }
        }
    }

    private static string CurrentVersion()
    {
        return AssemblyVersionInfo.ForEntryAssembly().Version;
    }

    /// <summary>A version reaches this from a network response and goes into a filename, so it keeps only
    /// what a version is made of.</summary>
    private static string Sanitize(string version)
    {
        var cleaned = new string([.. version.Where(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '-')]);
        return cleaned.Length == 0 ? "unknown" : cleaned;
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(exception, "{Path} could not be deleted.", path);
            }
        }
    }

    private sealed record PendingUpdate(string Version, string InstallerPath, string LogPath);
}
