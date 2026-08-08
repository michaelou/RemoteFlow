using System.IO.Compression;
using System.Text;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Backup;
using RemoteFlow.Application.Services;
using RemoteFlow.Domain.Enums;
using RemoteFlow.Infrastructure.Backup;
using Xunit;

namespace RemoteFlow.Application.Tests;

public sealed class BackupInspectionTests
{
    [Fact]
    public async Task InspectionIsReadOnlyAndLeavesDatabaseBytesIdentical()
    {
        using var directory = TemporaryDirectory.Create();
        var databasePath = Path.Combine(directory.Path, "remoteflow.db");
        var originalBytes = Encoding.UTF8.GetBytes("database-state-before-inspection");
        await File.WriteAllBytesAsync(databasePath, originalBytes, TestContext.Current.CancellationToken);
        var source = new FileObservingDataSource(databasePath, EmptySnapshot());
        var service = CreateService(EmptyArchive(), source);

        _ = await service.InspectAsync("backup.zip", TestContext.Current.CancellationToken);

        Assert.Equal(1, source.CaptureCount);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(databasePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConflictsUseHumanReadableNamesPathsAndEndpoints()
    {
        var imported = CreateConflictArchive(imported: true);
        var local = CreateConflictSnapshot(imported: false);
        var service = CreateService(imported, new FileObservingDataSource(null, local));

        var inspection = await service.InspectAsync("backup.zip", TestContext.Current.CancellationToken);

        Assert.Equal(3, inspection.Conflicts.Count);
        Assert.Contains(inspection.Conflicts, conflict =>
            conflict.Kind == BackupConflictKind.FolderPath && conflict.Description.Contains("/Team", StringComparison.Ordinal));
        Assert.Contains(inspection.Conflicts, conflict =>
            conflict.Kind == BackupConflictKind.TagName && conflict.Description.Contains("Production", StringComparison.Ordinal));
        Assert.Contains(inspection.Conflicts, conflict =>
            conflict.Kind == BackupConflictKind.ConnectionIdentity &&
            conflict.Description.Contains("admin@server.test:22", StringComparison.Ordinal));
        Assert.All(inspection.Conflicts, conflict =>
        {
            Assert.DoesNotContain(conflict.ImportedId.ToString(), conflict.Description, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(conflict.LocalId.ToString(), conflict.Description, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task CorruptArchiveFailsSpecificallyBeforeLocalDataIsRead()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "truncated.zip");
        await File.WriteAllBytesAsync(path, [0x50, 0x4B, 0x03], TestContext.Current.CancellationToken);
        var source = new FileObservingDataSource(null, EmptySnapshot());
        var service = new BackupService(source, new ZipBackupArchiveSerializer(), new FixedClock());

        var exception = await Assert.ThrowsAsync<BackupArchiveException>(() =>
            service.InspectAsync(path, TestContext.Current.CancellationToken));

        Assert.Contains("valid or complete zip", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, source.CaptureCount);
    }

    [Fact]
    public async Task NewerVersionIsRefusedAtInspectionBeforeLocalDataIsRead()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "future.zip");
        var serializer = new ZipBackupArchiveSerializer();
        await serializer.WriteAsync(path, EmptyArchive(), TestContext.Current.CancellationToken);
        RewriteManifest(path, json => json.Replace(
            "\"formatVersion\": 1",
            "\"formatVersion\": 2",
            StringComparison.Ordinal));
        var source = new FileObservingDataSource(null, EmptySnapshot());
        var service = new BackupService(source, serializer, new FixedClock());

        var exception = await Assert.ThrowsAsync<BackupArchiveException>(() =>
            service.InspectAsync(path, TestContext.Current.CancellationToken));

        Assert.Contains("version 2", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, source.CaptureCount);
    }

    [Fact]
    public async Task MissingSemanticallyOptionalTagsEntryMeansNoTags()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "missing-tags.zip");
        var serializer = new ZipBackupArchiveSerializer();
        await serializer.WriteAsync(path, EmptyArchive(), TestContext.Current.CancellationToken);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            zip.GetEntry(BackupFormat.TagsEntry)!.Delete();
        }
        var service = new BackupService(
            new FileObservingDataSource(null, EmptySnapshot()),
            serializer,
            new FixedClock());

        var inspection = await service.InspectAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(0, inspection.Counts.Tags);
    }

    [Fact]
    public async Task MergeAndReplacePreviewsAreDistinctAndUseArchiveCounts()
    {
        var archive = CreateConflictArchive(imported: true);
        var local = CreateConflictSnapshot(imported: false);
        var service = CreateService(archive, new FileObservingDataSource(null, local));

        var inspection = await service.InspectAsync("backup.zip", TestContext.Current.CancellationToken);

        Assert.Equal(archive.ActualCounts, inspection.Counts);
        Assert.Equal(MergeStrategy.Merge, inspection.MergePreview.Strategy);
        Assert.Equal(MergeStrategy.Replace, inspection.ReplacePreview.Strategy);
        Assert.Equal(archive.ActualCounts, inspection.ReplacePreview.Adds);
        Assert.Equal(local.Connections.Count, inspection.ReplacePreview.Removes.Connections);
        Assert.Contains("without deleting", inspection.MergePreview.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("removes all", inspection.ReplacePreview.Description, StringComparison.OrdinalIgnoreCase);
    }

    private static BackupService CreateService(BackupArchive archive, IBackupDataSource source)
    {
        return new BackupService(source, new StaticSerializer(archive), new FixedClock());
    }

    private static BackupArchive EmptyArchive()
    {
        var counts = new BackupEntityCounts(0, 0, 0, 0, 0, 0);
        return new BackupArchive(
            new BackupManifest(1, "test", DateTimeOffset.UnixEpoch, null, counts, false),
            [],
            [],
            [],
            [],
            [],
            []);
    }

    private static BackupDataSnapshot EmptySnapshot()
    {
        return new BackupDataSnapshot([], [], [], [], [], []);
    }

    private static BackupArchive CreateConflictArchive(bool imported)
    {
        var snapshot = CreateConflictSnapshot(imported);
        var counts = new BackupEntityCounts(1, 1, 1, 0, 0, 0);
        return new BackupArchive(
            new BackupManifest(1, "test", DateTimeOffset.UnixEpoch, null, counts, false),
            snapshot.Connections,
            snapshot.Folders,
            snapshot.Tags,
            [],
            [],
            []);
    }

    private static BackupDataSnapshot CreateConflictSnapshot(bool imported)
    {
        var idPrefix = imported ? "1" : "2";
        var folder = new BackupFolder(
            Guid.Parse($"{idPrefix}0000000-0000-0000-0000-000000000001"),
            imported ? "Team imported" : "Team local",
            null,
            "/Team",
            0,
            0,
            true,
            Guid.Parse($"{idPrefix}0000000-0000-0000-0000-000000000002"),
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);
        var tag = new BackupTag(
            Guid.Parse($"{idPrefix}0000000-0000-0000-0000-000000000003"),
            imported ? "Production" : "production",
            imported ? "#FF0000" : null,
            DateTimeOffset.UnixEpoch);
        var connection = new BackupConnection(
            Guid.Parse($"{idPrefix}0000000-0000-0000-0000-000000000004"),
            imported ? "Imported server" : "Local server",
            "server.test",
            22,
            ProtocolType.Ssh,
            "admin",
            AuthMethod.None,
            imported ? "new notes" : "old notes",
            folder.Id,
            false,
            EnvironmentKind.Production,
            null,
            null,
            Guid.Parse($"{idPrefix}0000000-0000-0000-0000-000000000005"),
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            new BackupCredentialReference(CredentialKind.None, string.Empty, string.Empty, null),
            new BackupSshOptions(null, "xterm-256color", null, null, null, HostKeyPolicy.Strict, true),
            new BackupSftpOptions(null, null, false, false),
            new BackupRdpOptions(null, false, null, null, false, true, false));
        return new BackupDataSnapshot([connection], [folder], [tag], [], [], []);
    }

    private static void RewriteManifest(string path, Func<string, string> rewrite)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        var entry = zip.GetEntry(BackupFormat.ManifestEntry) ?? throw new InvalidDataException("Missing manifest.");
        string json;
        using (var reader = new StreamReader(entry.Open(), Encoding.UTF8))
        {
            json = reader.ReadToEnd();
        }

        entry.Delete();
        var replacement = zip.CreateEntry(BackupFormat.ManifestEntry);
        using var writer = new StreamWriter(replacement.Open(), new UTF8Encoding(false));
        writer.Write(rewrite(json));
    }

    private sealed class FileObservingDataSource(string? databasePath, BackupDataSnapshot snapshot) : IBackupDataSource
    {
        public int CaptureCount { get; private set; }

        public Task<BackupDataSnapshot> CaptureAsync(
            IProgress<BackupProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CaptureCount++;
            if (databasePath is not null)
            {
                _ = File.ReadAllBytes(databasePath);
            }

            return Task.FromResult(snapshot);
        }
    }

    private sealed class StaticSerializer(BackupArchive archive) : IBackupArchiveSerializer
    {
        public Task WriteAsync(string path, BackupArchive value, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<BackupArchive> ReadAsync(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(archive);
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"RemoteFlow-{Guid.NewGuid():N}");
            _ = Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
