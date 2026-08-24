using RemoteFlow.Application.Abstractions.Sftp;
using RemoteFlow.Application.Services.Backup;
using Xunit;

namespace RemoteFlow.Application.Tests;

public sealed class AutoBackupDestinationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "remoteflow-destination-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void CreateMakesTheFolderSoAFirstRunDoesNotHaveToFail()
    {
        var folder = Path.Combine(_root, "backups", "nested");

        var created = LocalFolderBackupDestination.Create(folder);

        Assert.True(created.IsSuccess);
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public void CreateRejectsARelativePathRatherThanGuessingWhereItMeans()
    {
        var created = LocalFolderBackupDestination.Create("backups");

        Assert.True(created.IsFailure);
        Assert.Equal(SftpError.InvalidPath, created.Failure.Error);
    }

    [Fact]
    public void CreateRejectsAnEmptyFolder()
    {
        Assert.True(LocalFolderBackupDestination.Create(null).IsFailure);
        Assert.True(LocalFolderBackupDestination.Create("   ").IsFailure);
    }

    /// <summary>A local destination stages inside the backup folder, so a crash leaves a part file where
    /// the runner's cache sweep will never look for it.</summary>
    [Fact]
    public async Task CreateClearsAnAbandonedStagingFileButNotARecentOne()
    {
        _ = Directory.CreateDirectory(Folder);
        var abandoned = AutoBackupNaming.Create(DateTimeOffset.UtcNow, Guid.NewGuid()) + AutoBackupNaming.PartialSuffix;
        var inFlight = AutoBackupNaming.Create(DateTimeOffset.UtcNow, Guid.NewGuid()) + AutoBackupNaming.PartialSuffix;
        var unrelated = "notes.zip.part";
        await WriteAsync(abandoned);
        await WriteAsync(inFlight);
        await WriteAsync(unrelated);
        File.SetLastWriteTimeUtc(Path.Combine(Folder, abandoned), DateTime.UtcNow - TimeSpan.FromDays(3));

        var created = LocalFolderBackupDestination.Create(Folder);

        Assert.True(created.IsSuccess);
        Assert.False(File.Exists(Path.Combine(Folder, abandoned)));
        // A run that is still writing, and a file that was never ours, both survive.
        Assert.True(File.Exists(Path.Combine(Folder, inFlight)));
        Assert.True(File.Exists(Path.Combine(Folder, unrelated)));
    }

    [Fact]
    public async Task PublishMovesThePartFileOntoItsFinalName()
    {
        await using var destination = Open();
        var name = AutoBackupNaming.Create(DateTimeOffset.UtcNow, Guid.NewGuid());
        var staging = destination.GetStagingPath(name);
        await File.WriteAllTextAsync(staging, "archive", TestContext.Current.CancellationToken);

        var published = await destination.PublishAsync(staging, name, TestContext.Current.CancellationToken);

        Assert.True(published.IsSuccess);
        Assert.False(File.Exists(staging));
        Assert.True(File.Exists(Path.Combine(Folder, name)));
        Assert.EndsWith(AutoBackupNaming.PartialSuffix, staging, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListIgnoresUnrelatedFilesPartialUploadsAndManualExports()
    {
        await using var destination = Open();
        var ours = AutoBackupNaming.Create(DateTimeOffset.UtcNow, Guid.NewGuid());
        await WriteAsync(ours);
        await WriteAsync("notes.zip");
        await WriteAsync("RemoteFlow-backup-20260824-120000.zip");
        await WriteAsync(ours + AutoBackupNaming.PartialSuffix);

        var listed = await destination.ListAsync(TestContext.Current.CancellationToken);

        Assert.True(listed.IsSuccess);
        var archive = Assert.Single(listed.Value);
        Assert.Equal(ours, archive.Name);
    }

    [Fact]
    public async Task ListReportsTheTimestampEncodedInTheNameRatherThanTheFileSystemTime()
    {
        await using var destination = Open();
        var created = new DateTimeOffset(2026, 8, 24, 13, 15, 0, TimeSpan.Zero);
        var name = AutoBackupNaming.Create(created, Guid.NewGuid());
        await WriteAsync(name);

        var listed = await destination.ListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(created, Assert.Single(listed.Value).CreatedUtc);
    }

    [Fact]
    public async Task DeleteRemovesTheArchive()
    {
        await using var destination = Open();
        var name = AutoBackupNaming.Create(DateTimeOffset.UtcNow, Guid.NewGuid());
        await WriteAsync(name);
        var archive = Assert.Single((await destination.ListAsync(TestContext.Current.CancellationToken)).Value);

        var deleted = await destination.DeleteAsync(archive, TestContext.Current.CancellationToken);

        Assert.True(deleted.IsSuccess);
        Assert.False(File.Exists(Path.Combine(Folder, name)));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Not a test failure.
        }
    }

    private string Folder => Path.Combine(_root, "backups");

    private LocalFolderBackupDestination Open()
    {
        _ = Directory.CreateDirectory(Folder);
        return new LocalFolderBackupDestination(Folder);
    }

    private Task WriteAsync(string name)
    {
        return File.WriteAllTextAsync(Path.Combine(Folder, name), "x", TestContext.Current.CancellationToken);
    }
}
