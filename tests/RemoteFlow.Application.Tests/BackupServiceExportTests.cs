using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Backup;
using RemoteFlow.Application.Services;
using RemoteFlow.Domain.Enums;
using Xunit;

namespace RemoteFlow.Application.Tests;

public sealed class BackupServiceExportTests
{
    [Fact]
    public async Task EmptyDatabaseProducesValidArchiveAndSummary()
    {
        var serializer = new RecordingSerializer();
        var service = CreateService(EmptySnapshot(), serializer);
        var request = new BackupExportRequest("empty.zip", BackupExportScope.All);

        var result = await service.ExportAsync(request, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(serializer.Written);
        Assert.Equal(new BackupEntityCounts(0, 0, 0, 0, 0, 0), serializer.Written.Manifest.Counts);
        Assert.Contains("0 connections", result.Summary, StringComparison.Ordinal);
        Assert.EndsWith("empty.zip", result.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubtreeIncludesDescendantsAndAllAncestorsIncludingEmptyFolders()
    {
        var snapshot = CreateSnapshot();
        var serializer = new RecordingSerializer();
        var service = CreateService(snapshot, serializer);

        _ = await service.ExportAsync(
            new BackupExportRequest("subtree.zip", BackupExportScope.FolderSubtree(Ids.ChildFolder)),
            cancellationToken: TestContext.Current.CancellationToken);

        var archive = Assert.IsType<BackupArchive>(serializer.Written);
        Assert.Equal([Ids.RootFolder, Ids.ChildFolder, Ids.EmptyDescendant], archive.Folders.Select(folder => folder.Id));
        Assert.Equal([Ids.SelectedConnection], archive.Connections.Select(connection => connection.Id));
        Assert.DoesNotContain(archive.Connections, connection => connection.Id == Ids.OtherConnection);
    }

    [Fact]
    public async Task SelectedConnectionsIncludeOnlyTheirTagsFolderPathAndHostKeys()
    {
        var serializer = new RecordingSerializer();
        var service = CreateService(CreateSnapshot(), serializer);

        _ = await service.ExportAsync(
            new BackupExportRequest(
                "selected.zip",
                BackupExportScope.SelectedConnections([Ids.SelectedConnection])),
            cancellationToken: TestContext.Current.CancellationToken);

        var archive = Assert.IsType<BackupArchive>(serializer.Written);
        Assert.Equal([Ids.SelectedConnection], archive.Connections.Select(connection => connection.Id));
        Assert.Equal([Ids.RootFolder, Ids.ChildFolder], archive.Folders.Select(folder => folder.Id));
        Assert.Equal([Ids.SelectedTag], archive.Tags.Select(tag => tag.Id));
        Assert.Equal([Ids.SelectedTag], archive.ConnectionTags.Select(link => link.TagId));
        _ = Assert.Single(archive.HostKeys);
        Assert.Equal("selected.test", archive.HostKeys[0].Host);
    }

    [Fact]
    public async Task TogglesExcludeSettingsAndHostKeysAndCredentialsDefaultOff()
    {
        var serializer = new RecordingSerializer();
        var service = CreateService(CreateSnapshot(), serializer);
        var request = new BackupExportRequest(
            "minimal.zip",
            BackupExportScope.All,
            IncludeSettings: false,
            IncludeHostKeys: false);

        _ = await service.ExportAsync(request, cancellationToken: TestContext.Current.CancellationToken);

        var archive = Assert.IsType<BackupArchive>(serializer.Written);
        Assert.False(request.IncludeCredentials);
        Assert.False(archive.Manifest.IncludesCredentials);
        Assert.Empty(archive.Settings);
        Assert.Empty(archive.HostKeys);
        Assert.Null(archive.EncryptedCredentials);
    }

    [Fact]
    public async Task CredentialToggleIsHonestlyRejectedUntilEncryptionFeatureIsAvailable()
    {
        var service = CreateService(CreateSnapshot(), new RecordingSerializer());
        var request = new BackupExportRequest(
            "credentials.zip",
            BackupExportScope.All,
            IncludeCredentials: true);

        var exception = await Assert.ThrowsAsync<BackupArchiveException>(() =>
            service.ExportAsync(request, cancellationToken: TestContext.Current.CancellationToken));

        Assert.False(service.CanExportCredentials);
        Assert.Contains("not available", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProgressHasMultipleStagesAndCancellationStopsBeforeWrite()
    {
        using var cancellation = new CancellationTokenSource();
        var progressItems = new List<BackupProgress>();
        var progress = new InlineProgress<BackupProgress>(item =>
        {
            progressItems.Add(item);
            if (item.CompletedUnits == 3)
            {
                cancellation.Cancel();
            }
        });
        var serializer = new RecordingSerializer();
        var source = new StagedDataSource(CreateSnapshot());
        var service = new BackupService(source, serializer, new FixedClock());

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ExportAsync(
                new BackupExportRequest("cancelled.zip", BackupExportScope.All),
                progress,
                cancellation.Token));

        Assert.True(progressItems.Count >= 4);
        Assert.Null(serializer.Written);
    }

    [Fact]
    public async Task SerializerWriteFailureIsClearAndDoesNotReturnSuccess()
    {
        var service = CreateService(CreateSnapshot(), new FailingSerializer());

        var exception = await Assert.ThrowsAsync<BackupArchiveException>(() =>
            service.ExportAsync(
                new BackupExportRequest("forbidden/backup.zip", BackupExportScope.All),
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("No partial archive", exception.Message, StringComparison.Ordinal);
    }

    private static BackupService CreateService(BackupDataSnapshot snapshot, IBackupArchiveSerializer serializer)
    {
        return new BackupService(new StagedDataSource(snapshot), serializer, new FixedClock());
    }

    private static BackupDataSnapshot EmptySnapshot()
    {
        return new BackupDataSnapshot([], [], [], [], [], []);
    }

    private static BackupDataSnapshot CreateSnapshot()
    {
        var created = new DateTimeOffset(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);
        var folders = new[]
        {
            Folder(Ids.RootFolder, "Root", "/Root", 0, null, created),
            Folder(Ids.ChildFolder, "Child", "/Root/Child", 1, Ids.RootFolder, created),
            Folder(Ids.EmptyDescendant, "Empty", "/Root/Child/Empty", 2, Ids.ChildFolder, created),
            Folder(Ids.OtherFolder, "Other", "/Other", 0, null, created),
        };
        var connections = new[]
        {
            Connection(Ids.SelectedConnection, "Selected", "selected.test", Ids.ChildFolder, created),
            Connection(Ids.OtherConnection, "Other", "other.test", Ids.OtherFolder, created),
        };
        var tags = new[]
        {
            new BackupTag(Ids.SelectedTag, "Selected tag", null, created),
            new BackupTag(Ids.OtherTag, "Other tag", null, created),
        };
        var links = new[]
        {
            new BackupConnectionTag(Ids.SelectedConnection, Ids.SelectedTag),
            new BackupConnectionTag(Ids.OtherConnection, Ids.OtherTag),
        };
        var settings = new[] { new BackupSetting("example", "true", created) };
        var hostKeys = new[]
        {
            HostKey(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), "selected.test", created),
            HostKey(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), "other.test", created),
        };
        return new BackupDataSnapshot(connections, folders, tags, links, settings, hostKeys);
    }

    private static BackupFolder Folder(
        Guid id,
        string name,
        string path,
        int depth,
        Guid? parentId,
        DateTimeOffset created)
    {
        return new BackupFolder(id, name, parentId, path, depth, 0, true, Guid.NewGuid(), created, created);
    }

    private static BackupConnection Connection(
        Guid id,
        string name,
        string host,
        Guid folderId,
        DateTimeOffset created)
    {
        return new BackupConnection(
            id,
            name,
            host,
            22,
            ProtocolType.Ssh,
            "user",
            AuthMethod.None,
            null,
            folderId,
            false,
            EnvironmentKind.Unspecified,
            null,
            null,
            Guid.NewGuid(),
            created,
            created,
            new BackupCredentialReference(CredentialKind.None, string.Empty, string.Empty, null),
            new BackupSshOptions(null, "xterm-256color", null, null, null, HostKeyPolicy.Strict, true),
            new BackupSftpOptions(null, null, false, false),
            new BackupRdpOptions(null, false, null, null, false, true, false));
    }

    private static BackupHostKey HostKey(Guid id, string host, DateTimeOffset created)
    {
        return new BackupHostKey(
            id,
            host,
            22,
            "ssh-ed25519",
            "public-key",
            "SHA256:fingerprint",
            HostKeyTrust.Trusted,
            HostKeySource.Pinned,
            null,
            created,
            created);
    }

    private static class Ids
    {
        public static readonly Guid RootFolder = Guid.Parse("10000000-0000-0000-0000-000000000001");
        public static readonly Guid ChildFolder = Guid.Parse("10000000-0000-0000-0000-000000000002");
        public static readonly Guid EmptyDescendant = Guid.Parse("10000000-0000-0000-0000-000000000003");
        public static readonly Guid OtherFolder = Guid.Parse("10000000-0000-0000-0000-000000000004");
        public static readonly Guid SelectedConnection = Guid.Parse("20000000-0000-0000-0000-000000000001");
        public static readonly Guid OtherConnection = Guid.Parse("20000000-0000-0000-0000-000000000002");
        public static readonly Guid SelectedTag = Guid.Parse("30000000-0000-0000-0000-000000000001");
        public static readonly Guid OtherTag = Guid.Parse("30000000-0000-0000-0000-000000000002");
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 8, 8, 13, 0, 0, TimeSpan.Zero);
    }

    private sealed class StagedDataSource(BackupDataSnapshot snapshot) : IBackupDataSource
    {
        public Task<BackupDataSnapshot> CaptureAsync(
            IProgress<BackupProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            for (var index = 1; index <= 6; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new BackupProgress($"Stage {index}", index, 8));
            }

            return Task.FromResult(snapshot);
        }
    }

    private sealed class RecordingSerializer : IBackupArchiveSerializer
    {
        public BackupArchive? Written { get; private set; }

        public Task WriteAsync(string path, BackupArchive archive, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Written = archive;
            return Task.CompletedTask;
        }

        public Task<BackupArchive> ReadAsync(string path, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FailingSerializer : IBackupArchiveSerializer
    {
        public Task WriteAsync(string path, BackupArchive archive, CancellationToken cancellationToken = default)
        {
            throw new BackupArchiveException("The backup could not be written. No partial archive was kept.");
        }

        public Task<BackupArchive> ReadAsync(string path, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value)
        {
            report(value);
        }
    }
}
