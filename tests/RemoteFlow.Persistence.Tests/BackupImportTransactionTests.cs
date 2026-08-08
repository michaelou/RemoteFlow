using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using RemoteFlow.Application.Abstractions.Backup;
using RemoteFlow.Domain.Abstractions;
using RemoteFlow.Domain.Entities;
using RemoteFlow.Domain.Enums;
using RemoteFlow.Persistence.Backup;
using RemoteFlow.TestSupport;
using Xunit;

namespace RemoteFlow.Persistence.Tests;

public sealed class BackupImportTransactionTests
{
    [Fact]
    public async Task FaultInjectedMidImportRestoresExactPriorDatabaseBytes()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await SqliteTempDbFixture.CreateAsync(token);
        await SeedTagAsync(fixture, "Before", token);
        await CheckpointAsync(fixture, token);
        SqliteConnection.ClearAllPools();
        var before = await File.ReadAllBytesAsync(fixture.DatabasePath, token);
        using var store = new EfBackupImportStore(
            fixture.Factory,
            fixture.DatabasePath,
            new ThrowAtStep(8));
        var target = new BackupDataSnapshot(
            [], [], [CreateBackupTag("After")], [], [], []);

        _ = await Assert.ThrowsAsync<InjectedImportException>(() =>
            store.ApplyAsync(target, MergeStrategy.Merge, token));

        var after = await File.ReadAllBytesAsync(fixture.DatabasePath, token);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task ReplaceLoadsSnapshotAndLeavesPreImportBak()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await SqliteTempDbFixture.CreateAsync(token);
        await SeedTagAsync(fixture, "Before", token);
        using var store = new EfBackupImportStore(fixture.Factory, fixture.DatabasePath);
        var target = CreateCompleteSnapshot();

        var result = await store.ApplyAsync(target, MergeStrategy.Replace, token);

        Assert.Equal($"{fixture.DatabasePath}.bak", result.PreImportBackupPath);
        Assert.True(File.Exists(result.PreImportBackupPath));
        var captured = await new EfBackupDataSource(fixture.Factory).CaptureAsync(cancellationToken: token);
        Assert.Equal(target.Connections, captured.Connections);
        Assert.Equal(target.Folders, captured.Folders);
        Assert.Equal(target.Tags, captured.Tags);
        Assert.Equal(target.ConnectionTags, captured.ConnectionTags);
        Assert.Equal(target.Settings, captured.Settings);
        Assert.Equal(target.HostKeys, captured.HostKeys);
    }

    private static BackupTag CreateBackupTag(string name)
    {
        return new BackupTag(Guid.NewGuid(), name, null, DateTimeOffset.UnixEpoch);
    }

    private static BackupDataSnapshot CreateCompleteSnapshot()
    {
        var created = new DateTimeOffset(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);
        var folderId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var tagId = Guid.Parse("20000000-0000-0000-0000-000000000001");
        var connectionId = Guid.Parse("30000000-0000-0000-0000-000000000001");
        var folder = new BackupFolder(
            folderId, "子 🚀", null, "/子 🚀", 0, 4, true,
            Guid.Parse("40000000-0000-0000-0000-000000000001"), created, created.AddMinutes(1));
        var tag = new BackupTag(tagId, "重要 🚨", "#FF0000", created);
        var connection = new BackupConnection(
            connectionId, "Sérver 🚀", "example.test", 22, ProtocolType.Ssh, "admin",
            AuthMethod.Password, "Notes 📝", folderId, true, EnvironmentKind.Production, "#112233", 2,
            Guid.Parse("50000000-0000-0000-0000-000000000001"), created, created.AddMinutes(2),
            new BackupCredentialReference(CredentialKind.None, string.Empty, string.Empty, null),
            new BackupSshOptions(30, "xterm-256color", "C:/key", "tmux", "/srv", HostKeyPolicy.Strict, true),
            new BackupSftpOptions("/srv", "C:/Downloads", true, true),
            new BackupRdpOptions("EXAMPLE", false, 1920, 1080, false, true, false));
        var hostKey = new BackupHostKey(
            Guid.Parse("60000000-0000-0000-0000-000000000001"), "example.test", 22, "ssh-ed25519",
            "public-key", "SHA256:fingerprint", HostKeyTrust.Trusted, HostKeySource.Pinned,
            "trusted", created, created.AddMinutes(3));
        return new BackupDataSnapshot(
            [connection],
            [folder],
            [tag],
            [new BackupConnectionTag(connectionId, tagId)],
            [new BackupSetting("terminal.fontSize", "14", created)],
            [hostKey]);
    }

    private static async Task SeedTagAsync(
        SqliteTempDbFixture fixture,
        string name,
        CancellationToken cancellationToken)
    {
        await using var context = await fixture.Factory.CreateDbContextAsync(cancellationToken);
        _ = context.Tags.Add(Tag.Create(SystemGuidProvider.Instance, name, createdUtc: DateTimeOffset.UnixEpoch).Value);
        _ = await context.SaveChangesAsync(cancellationToken);
    }

    private static async Task CheckpointAsync(
        SqliteTempDbFixture fixture,
        CancellationToken cancellationToken)
    {
        await using var context = await fixture.Factory.CreateDbContextAsync(cancellationToken);
        _ = await context.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken);
    }

    private sealed class ThrowAtStep(int targetStep) : IBackupImportFaultInjector
    {
        public void OnImportStep(int stepNumber)
        {
            if (stepNumber == targetStep)
            {
                throw new InjectedImportException();
            }
        }
    }

    private sealed class InjectedImportException : Exception;
}
