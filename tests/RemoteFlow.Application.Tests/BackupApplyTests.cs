using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Backup;
using RemoteFlow.Application.Services;
using RemoteFlow.Domain.Enums;
using Xunit;

namespace RemoteFlow.Application.Tests;

public sealed class BackupApplyTests
{
    [Fact]
    public async Task PreferLocalReimportIsIdempotent()
    {
        var archive = CreateArchive("Server", "server.test", "/Root", "Production");
        var local = Snapshot(archive);
        var store = new RecordingImportStore();
        var service = CreateService(archive, local, store);

        var result = await service.ApplyAsync(
            new BackupApplyRequest("backup.zip", MergeStrategy.Merge, MergeConflictPolicy.PreferLocal),
            TestContext.Current.CancellationToken);

        Assert.NotNull(store.Target);
        Assert.Equal(local.Connections, store.Target.Connections);
        Assert.Equal(local.Folders, store.Target.Folders);
        Assert.Equal(local.Tags, store.Target.Tags);
        Assert.Equal(local.ConnectionTags, store.Target.ConnectionTags);
        Assert.Equal(local.Settings, store.Target.Settings);
        Assert.Equal(local.HostKeys, store.Target.HostKeys);
        Assert.Equal(0, result.Replaced);
        Assert.Equal(0, result.Renamed);
    }

    [Fact]
    public async Task RenameImportedPreservesBothAndUsesRequiredSuffix()
    {
        var imported = CreateArchive("Server", "server.test", "/Root", "Production");
        var localArchive = CreateArchive("Local server", "server.test", "/Root", "production", alternateIds: true);
        var store = new RecordingImportStore();
        var service = CreateService(imported, Snapshot(localArchive), store);

        var result = await service.ApplyAsync(
            new BackupApplyRequest("backup.zip", MergeStrategy.Merge, MergeConflictPolicy.RenameImported),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, store.Target!.Connections.Count);
        Assert.Contains(store.Target.Connections, item => item.Name == "Server (imported)");
        Assert.Contains(store.Target.Folders, item => item.Name == "Root (imported)");
        Assert.Contains(store.Target.Tags, item => item.Name == "Production (imported)");
        Assert.Equal(3, result.Renamed);
    }

    [Fact]
    public async Task PreferImportedOverwritesAndReportsReplacements()
    {
        var imported = CreateArchive("Imported 🚀", "server.test", "/Root", "Production");
        var localArchive = CreateArchive("Local", "server.test", "/Root", "production", alternateIds: true);
        var store = new RecordingImportStore();
        var service = CreateService(imported, Snapshot(localArchive), store);

        var result = await service.ApplyAsync(
            new BackupApplyRequest("backup.zip", MergeStrategy.Merge, MergeConflictPolicy.PreferImported),
            TestContext.Current.CancellationToken);

        Assert.Equal("Imported 🚀", store.Target!.Connections.Single().Name);
        Assert.True(result.Replaced >= 3);
    }

    [Fact]
    public async Task ReplaceRequiresTypedConfirmationAndReturnsBackupPath()
    {
        var archive = CreateArchive("Server", "server.test", "/Root", "Production");
        var store = new RecordingImportStore();
        var service = CreateService(archive, EmptySnapshot(), store);
        var request = new BackupApplyRequest("backup.zip", MergeStrategy.Replace);

        var exception = await Assert.ThrowsAsync<BackupArchiveException>(() =>
            service.ApplyAsync(request, TestContext.Current.CancellationToken));
        Assert.Contains("typing REPLACE", exception.Message, StringComparison.Ordinal);

        var result = await service.ApplyAsync(
            request with { ReplaceConfirmation = "REPLACE" },
            TestContext.Current.CancellationToken);
        Assert.Equal(MergeStrategy.Replace, store.Strategy);
        Assert.Equal("remoteflow.db.bak", result.PreImportBackupPath);
    }

    [Fact]
    public async Task MissingCredentialsAreReportedAndClearedRatherThanSilentlyRetained()
    {
        var archive = CreateArchive("Server", "server.test", "/Root", "Production", hasCredential: true);
        var store = new RecordingImportStore();
        var service = CreateService(archive, EmptySnapshot(), store);

        var result = await service.ApplyAsync(
            new BackupApplyRequest("backup.zip", MergeStrategy.Merge),
            TestContext.Current.CancellationToken);

        _ = Assert.Single(result.MissingCredentials);
        Assert.Contains("Server", result.MissingCredentials[0], StringComparison.Ordinal);
        Assert.Equal(CredentialKind.None, store.Target!.Connections.Single().Credential.Kind);
    }

    [Fact]
    public async Task DeepPathsTagsUnicodeAndEmojiSurviveApplyPlan()
    {
        var archive = CreateArchive("Sérver 🚀", "unicode.test", "/Root/Deep/子", "重要 🚨");
        var store = new RecordingImportStore();
        var service = CreateService(archive, EmptySnapshot(), store);

        _ = await service.ApplyAsync(
            new BackupApplyRequest("backup.zip", MergeStrategy.Replace, ReplaceConfirmation: "REPLACE"),
            TestContext.Current.CancellationToken);

        Assert.Equal("/Root/Deep/子", store.Target!.Folders.Single().Path);
        Assert.Equal("重要 🚨", store.Target.Tags.Single().Name);
        Assert.Equal("Sérver 🚀", store.Target.Connections.Single().Name);
    }

    [Fact]
    public async Task ApplyAnnouncesAReloadSoOpenViewsDoNotKeepThePreImportData()
    {
        var archive = CreateArchive("Server", "server.test", "/Root", "Production");
        var store = new RecordingImportStore();
        var notifier = new ConnectionChangeNotifier();
        var service = CreateService(archive, EmptySnapshot(), store, notifier);
        var changes = new List<ConnectionChangedEventArgs>();
        notifier.ConnectionChanged += (_, args) => changes.Add(args);

        _ = await service.ApplyAsync(
            new BackupApplyRequest("backup.zip", MergeStrategy.Replace, ReplaceConfirmation: "REPLACE"),
            TestContext.Current.CancellationToken);

        var change = Assert.Single(changes);
        Assert.Equal(ConnectionChangeKind.Reloaded, change.Kind);
        Assert.Equal(Guid.Empty, change.ConnectionId);
    }

    [Fact]
    public async Task AFailedApplyAnnouncesNothing()
    {
        var archive = CreateArchive("Server", "server.test", "/Root", "Production");
        var notifier = new ConnectionChangeNotifier();
        var service = CreateService(archive, EmptySnapshot(), new RecordingImportStore(), notifier);
        var changed = false;
        notifier.ConnectionChanged += (_, _) => changed = true;

        _ = await Assert.ThrowsAsync<BackupArchiveException>(() => service.ApplyAsync(
            new BackupApplyRequest("backup.zip", MergeStrategy.Replace),
            TestContext.Current.CancellationToken));

        Assert.False(changed);
    }

    private static BackupService CreateService(
        BackupArchive archive,
        BackupDataSnapshot local,
        RecordingImportStore store,
        IConnectionChangeNotifier? changeNotifier = null)
    {
        return new BackupService(
            new StaticSource(local),
            new StaticSerializer(archive),
            new FixedClock(),
            store,
            changeNotifier: changeNotifier);
    }

    private static BackupArchive CreateArchive(
        string connectionName,
        string host,
        string folderPath,
        string tagName,
        bool alternateIds = false,
        bool hasCredential = false)
    {
        var prefix = alternateIds ? "2" : "1";
        var folderId = Guid.Parse($"{prefix}0000000-0000-0000-0000-000000000001");
        var tagId = Guid.Parse($"{prefix}0000000-0000-0000-0000-000000000002");
        var connectionId = Guid.Parse($"{prefix}0000000-0000-0000-0000-000000000003");
        var name = folderPath[(folderPath.LastIndexOf('/') + 1)..];
        var depth = folderPath.Count(character => character == '/') - 1;
        var folder = new BackupFolder(
            folderId, name, null, folderPath, depth, 0, true, Guid.NewGuid(),
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var tag = new BackupTag(tagId, tagName, null, DateTimeOffset.UnixEpoch);
        var credential = hasCredential
            ? new BackupCredentialReference(CredentialKind.Password, "source-key", "source-store", DateTimeOffset.UnixEpoch)
            : new BackupCredentialReference(CredentialKind.None, string.Empty, string.Empty, null);
        var connection = new BackupConnection(
            connectionId, connectionName, host, 22, ProtocolType.Ssh, "admin", AuthMethod.Password,
            "Notes 📝", folderId, false, EnvironmentKind.Production, null, null, Guid.NewGuid(),
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, credential,
            new BackupSshOptions(null, "xterm-256color", null, null, null, HostKeyPolicy.Strict, true),
            new BackupSftpOptions(null, null, false, false),
            new BackupRdpOptions(null, false, null, null, false, true, false));
        var counts = new BackupEntityCounts(1, 1, 1, 1, 0, 0);
        return new BackupArchive(
            new BackupManifest(1, "test", DateTimeOffset.UnixEpoch, null, counts, false),
            [connection], [folder], [tag], [new BackupConnectionTag(connectionId, tagId)], [], []);
    }

    private static BackupDataSnapshot Snapshot(BackupArchive archive)
    {
        return new BackupDataSnapshot(
            archive.Connections, archive.Folders, archive.Tags, archive.ConnectionTags, archive.Settings, archive.HostKeys);
    }

    private static BackupDataSnapshot EmptySnapshot()
    {
        return new BackupDataSnapshot([], [], [], [], [], []);
    }

    private sealed class RecordingImportStore : IBackupImportStore
    {
        public BackupDataSnapshot? Target { get; private set; }

        public MergeStrategy Strategy { get; private set; }

        public Task<BackupImportStoreResult> ApplyAsync(
            BackupDataSnapshot target,
            MergeStrategy strategy,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Target = target;
            Strategy = strategy;
            return Task.FromResult(new BackupImportStoreResult("remoteflow.db.bak"));
        }
    }

    private sealed class StaticSource(BackupDataSnapshot snapshot) : IBackupDataSource
    {
        public Task<BackupDataSnapshot> CaptureAsync(
            IProgress<BackupProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            return Task.FromResult(archive);
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
    }
}
