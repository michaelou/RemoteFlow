using RemoteFlow.Application.Abstractions.Backup;
using RemoteFlow.Application.Abstractions.Sftp;

namespace RemoteFlow.Application.Services.Backup;

public sealed class LocalFolderBackupDestination(string folder) : IAutoBackupDestination
{
    public string Description { get; } = string.IsNullOrWhiteSpace(folder)
        ? throw new ArgumentException("A backup folder is required.", nameof(folder))
        : Path.GetFullPath(folder);

    /// <summary>Stages inside the destination folder itself, so publishing is a same-volume move and
    /// therefore atomic. The <c>.part</c> name never parses as an archive, so a half-written file is
    /// invisible to retention.</summary>
    public string GetStagingPath(string fileName)
    {
        return Path.Combine(Description, fileName + AutoBackupNaming.PartialSuffix);
    }

    public Task<SftpResult> PublishAsync(
        string stagingPath,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            File.Move(stagingPath, Path.Combine(Description, fileName), overwrite: false);
            return Task.FromResult(SftpResult.Success());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(SftpResult.Fail(
                Classify(exception),
                $"The backup could not be written to '{Description}': {exception.Message}"));
        }
    }

    public Task<SftpResult<IReadOnlyList<AutoBackupArchive>>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var archives = new List<AutoBackupArchive>();
            // The search pattern narrows, but AutoBackupNaming.TryParse decides. Nothing it rejects is ever
            // handed to retention, which is what keeps unrelated files in this folder safe.
            foreach (var path in Directory.EnumerateFiles(Description, AutoBackupNaming.SearchPattern))
            {
                var name = Path.GetFileName(path);
                if (!AutoBackupNaming.TryParse(name, out var createdUtc))
                {
                    continue;
                }

                archives.Add(new AutoBackupArchive(name, path, createdUtc, new FileInfo(path).Length));
            }

            return Task.FromResult(SftpResult<IReadOnlyList<AutoBackupArchive>>.Success(archives));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(SftpResult<IReadOnlyList<AutoBackupArchive>>.Fail(
                Classify(exception),
                $"The backup folder '{Description}' could not be listed: {exception.Message}"));
        }
    }

    public Task<SftpResult> DeleteAsync(
        AutoBackupArchive archive,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archive);
        try
        {
            File.Delete(archive.Path);
            return Task.FromResult(SftpResult.Success());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(SftpResult.Fail(
                Classify(exception),
                $"'{archive.Name}' could not be deleted: {exception.Message}"));
        }
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    /// <summary>Creates the folder up front so a first run against a path that does not exist yet succeeds
    /// rather than reporting a failure the user would have to go and fix by hand.</summary>
    public static SftpResult<IAutoBackupDestination> Create(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return SftpResult<IAutoBackupDestination>.Fail(
                SftpError.InvalidPath, "Choose a folder for automatic backups.");
        }

        if (!Path.IsPathRooted(folder))
        {
            return SftpResult<IAutoBackupDestination>.Fail(
                SftpError.InvalidPath,
                $"'{folder}' is not a full path. Automatic backups need an absolute folder.");
        }

        try
        {
            _ = Directory.CreateDirectory(folder);
            SweepAbandonedStagingFiles(folder);
            return SftpResult<IAutoBackupDestination>.Success(new LocalFolderBackupDestination(folder));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return SftpResult<IAutoBackupDestination>.Fail(
                Classify(exception),
                $"The backup folder '{folder}' is not usable: {exception.Message}");
        }
    }

    /// <summary>Clears part-written archives a crash left behind. A local destination stages inside the
    /// backup folder itself, so the runner's cache sweep never sees these. Only files matching our own
    /// naming plus the ".part" suffix are touched, and only once they are a day old — which is long past
    /// any run that could still be writing one.</summary>
    private static void SweepAbandonedStagingFiles(string folder)
    {
        try
        {
            var cutoff = DateTime.UtcNow - TimeSpan.FromDays(1);
            foreach (var path in Directory.EnumerateFiles(
                folder, AutoBackupNaming.SearchPattern + AutoBackupNaming.PartialSuffix))
            {
                var name = Path.GetFileName(path);
                var archiveName = name[..^AutoBackupNaming.PartialSuffix.Length];
                if (AutoBackupNaming.IsAutoBackupName(archiveName) && File.GetLastWriteTimeUtc(path) < cutoff)
                {
                    File.Delete(path);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Leftovers are wasted disk, not a reason to refuse to back up.
        }
    }

    private static SftpError Classify(Exception exception)
    {
        return exception switch
        {
            UnauthorizedAccessException => SftpError.PermissionDenied,
            DirectoryNotFoundException or FileNotFoundException => SftpError.NotFound,
            ArgumentException or NotSupportedException => SftpError.InvalidPath,
            _ => SftpError.Unknown,
        };
    }
}
