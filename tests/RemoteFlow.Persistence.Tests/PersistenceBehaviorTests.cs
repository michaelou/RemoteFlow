using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using RemoteFlow.Domain.Abstractions;
using RemoteFlow.Domain.Entities;
using RemoteFlow.Domain.Enums;
using RemoteFlow.Domain.ValueObjects;
using Xunit;

namespace RemoteFlow.Persistence.Tests;

public sealed class PersistenceBehaviorTests
{
    [Fact]
    public async Task InitialMigrationCreatesExpectedSchemaAndMatchesCurrentModel()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = SqliteTestDatabase.Create();
        await using var context = database.Factory.CreateDbContext();

        await context.Database.MigrateAsync(cancellationToken);

        Assert.Empty(await context.Database.GetPendingMigrationsAsync(cancellationToken));
        Assert.Equal("wal", await ExecuteScalarAsync<string>(context, "PRAGMA journal_mode;", cancellationToken));
        Assert.Equal(1L, await ExecuteScalarAsync<long>(context, "PRAGMA foreign_keys;", cancellationToken));
        Assert.Equal(5_000L, await ExecuteScalarAsync<long>(context, "PRAGMA busy_timeout;", cancellationToken));

        var tables = await ReadUserTablesAsync(context, cancellationToken);
        Assert.Equal(
            [
                "Connections",
                "ConnectionTags",
                "Folders",
                "HostKeys",
                "RecentConnections",
                "Settings",
                "Tags",
            ],
            tables);

        var migrationsAssembly = context.GetService<IMigrationsAssembly>();
        var snapshot = Assert.IsAssignableFrom<ModelSnapshot>(migrationsAssembly.ModelSnapshot);
        var currentModel = context.GetService<IDesignTimeModel>().Model;
        var modelDiffer = context.GetService<IMigrationsModelDiffer>();

        Assert.False(modelDiffer.HasDifferences(
            snapshot.Model.GetRelationalModel(),
            currentModel.GetRelationalModel()));
    }

    [Fact]
    public async Task ForeignKeysRejectAnOrphanInsert()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = SqliteTestDatabase.Create();
        await using var context = database.Factory.CreateDbContext();
        await context.Database.MigrateAsync(cancellationToken);
        var orphanId = Guid.CreateVersion7().ToString("D").ToLowerInvariant();
        var openedUtc = DateTimeOffset.UtcNow;

        var exception = await Assert.ThrowsAsync<SqliteException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO RecentConnections (ConnectionId, LastOpenedUtc, OpenCount)
                VALUES ({{orphanId}}, {{openedUtc}}, {{1}});
                """, cancellationToken));

        Assert.Equal(19, exception.SqliteErrorCode);
    }

    [Fact]
    public async Task DeletingConnectionCascadesToTagsAndRecentHistory()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = SqliteTestDatabase.Create();
        var connection = CreateConnection();
        var tag = Tag.Create(SystemGuidProvider.Instance, "Production").Value;
        var recent = RecentConnection.Create(connection.Id).Value;
        _ = connection.AddTag(tag.Id);

        await using (var context = database.Factory.CreateDbContext())
        {
            await context.Database.MigrateAsync(cancellationToken);
            _ = context.Add(connection);
            _ = context.Add(tag);
            _ = context.Add(recent);
            _ = await context.SaveChangesAsync(cancellationToken);
        }

        await using (var context = database.Factory.CreateDbContext())
        {
            var persisted = await context.Connections.SingleAsync(
                item => item.Id == connection.Id,
                cancellationToken);
            _ = context.Remove(persisted);
            _ = await context.SaveChangesAsync(cancellationToken);
        }

        await using (var context = database.Factory.CreateDbContext())
        {
            Assert.Equal(0L, await context.ConnectionTags.LongCountAsync(cancellationToken));
            Assert.Equal(0L, await context.RecentConnections.LongCountAsync(cancellationToken));
            Assert.Equal(1L, await context.Tags.LongCountAsync(cancellationToken));
        }
    }

    [Fact]
    public async Task DeletingFolderContainingAConnectionIsRejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = SqliteTestDatabase.Create();
        var folder = Folder.Create(SystemGuidProvider.Instance, "Servers").Value;
        var connection = CreateConnection();
        _ = connection.SetFolder(folder.Id, SystemGuidProvider.Instance);

        await using (var context = database.Factory.CreateDbContext())
        {
            await context.Database.MigrateAsync(cancellationToken);
            _ = context.Add(folder);
            _ = context.Add(connection);
            _ = await context.SaveChangesAsync(cancellationToken);
        }

        await using (var context = database.Factory.CreateDbContext())
        {
            var persisted = await context.Folders.SingleAsync(
                item => item.Id == folder.Id,
                cancellationToken);
            _ = context.Remove(persisted);

            var exception = await Assert.ThrowsAsync<DbUpdateException>(
                () => context.SaveChangesAsync(cancellationToken));
            var sqliteException = Assert.IsType<SqliteException>(exception.InnerException);
            Assert.Equal(19, sqliteException.SqliteErrorCode);
        }
    }

    [Fact]
    public async Task EnumsRoundTripThroughIntegerColumnsAndGuidsUseCanonicalText()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = SqliteTestDatabase.Create();
        var connection = Connection.Create(
            SystemGuidProvider.Instance,
            "Desktop",
            "rdp.example.com",
            ProtocolType.Rdp).Value;
        _ = connection.SetDetails(
            "administrator",
            AuthMethod.Kerberos,
            null,
            EnvironmentKind.Production,
            null,
            SystemGuidProvider.Instance);
        var ssh = SshOptions.Default();
        _ = ssh.Configure(hostKeyPolicy: HostKeyPolicy.AcceptAny);
        _ = connection.SetOptions(
            ssh,
            SftpOptions.Default(),
            RdpOptions.Default(),
            SystemGuidProvider.Instance);
        var hostKey = HostKey.Create(
            SystemGuidProvider.Instance,
            "rdp.example.com",
            3389,
            "ssh-ed25519",
            "AAAAC3NzaC1lZDI1NTE5AAAA",
            "SHA256:fingerprint",
            HostKeyTrust.Revoked,
            HostKeySource.Pinned).Value;

        await using (var context = database.Factory.CreateDbContext())
        {
            await context.Database.MigrateAsync(cancellationToken);
            _ = context.Add(connection);
            _ = context.Add(hostKey);
            _ = await context.SaveChangesAsync(cancellationToken);

            await context.Database.OpenConnectionAsync(cancellationToken);
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = """
                SELECT Id, ConcurrencyStamp, Protocol, AuthMethod, Environment, Ssh_HostKeyPolicy
                FROM Connections;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            Assert.True(await reader.ReadAsync(cancellationToken));
            Assert.Equal(connection.Id.ToString("D").ToLowerInvariant(), reader.GetString(0));
            Assert.Equal(connection.ConcurrencyStamp.ToString("D").ToLowerInvariant(), reader.GetString(1));
            Assert.Equal((long)ProtocolType.Rdp, reader.GetInt64(2));
            Assert.Equal((long)AuthMethod.Kerberos, reader.GetInt64(3));
            Assert.Equal((long)EnvironmentKind.Production, reader.GetInt64(4));
            Assert.Equal((long)HostKeyPolicy.AcceptAny, reader.GetInt64(5));
        }

        await using (var context = database.Factory.CreateDbContext())
        {
            var persistedConnection = await context.Connections.AsNoTracking().SingleAsync(cancellationToken);
            var persistedHostKey = await context.HostKeys.AsNoTracking().SingleAsync(cancellationToken);

            Assert.Equal(ProtocolType.Rdp, persistedConnection.Protocol);
            Assert.Equal(AuthMethod.Kerberos, persistedConnection.AuthMethod);
            Assert.Equal(EnvironmentKind.Production, persistedConnection.Environment);
            Assert.Equal(HostKeyPolicy.AcceptAny, persistedConnection.Ssh.HostKeyPolicy);
            Assert.Equal(HostKeyTrust.Revoked, persistedHostKey.TrustState);
            Assert.Equal(HostKeySource.Pinned, persistedHostKey.Source);
        }
    }

    [Fact]
    public void FactoryCreatesIndependentShortLivedContexts()
    {
        using var database = SqliteTestDatabase.Create();
        using var first = database.Factory.CreateDbContext();
        using var second = database.Factory.CreateDbContext();

        Assert.NotSame(first, second);
    }

    private static Connection CreateConnection()
    {
        return Connection.Create(
            SystemGuidProvider.Instance,
            "Server",
            "server.example.com",
            ProtocolType.Ssh).Value;
    }

    private static async Task<T> ExecuteScalarAsync<T>(
        RemoteFlowDbContext context,
        string commandText,
        CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = commandText;
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Assert.IsType<T>(result);
    }

    private static async Task<string[]> ReadUserTablesAsync(
        RemoteFlowDbContext context,
        CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT name
            FROM sqlite_schema
            WHERE type = 'table'
              AND name NOT LIKE 'sqlite_%'
              AND substr(name, 1, 4) <> '__EF'
            ORDER BY name COLLATE NOCASE;
            """;

        var tables = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tables.Add(reader.GetString(0));
        }

        return [.. tables];
    }

    private sealed class SqliteTestDatabase(string dataDirectory) : IDisposable, IAsyncDisposable
    {
        public RemoteFlowDbContextFactory Factory { get; } = new(dataDirectory);

        public static SqliteTestDatabase Create()
        {
            var dataDirectory = Path.Combine(Path.GetTempPath(), "RemoteFlow.Persistence.Tests", Guid.NewGuid().ToString("N"));
            _ = Directory.CreateDirectory(dataDirectory);
            return new SqliteTestDatabase(dataDirectory);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dataDirectory, true);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
