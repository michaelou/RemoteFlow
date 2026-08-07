using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Persistence;
using RemoteFlow.Persistence.Repositories;
using RemoteFlow.TestSupport;
using Xunit;

namespace RemoteFlow.Infrastructure.Tests;

public sealed class DbInitializerTests
{
    private static readonly DateTimeOffset _now = new(2026, 8, 7, 12, 34, 56, TimeSpan.Zero);

    [Fact]
    public async Task FreshInitializationCreatesDirectoriesMigratesAndSeedsDefaults()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = TemporaryDirectory.Create();
        var paths = TestAppPaths.Under(directory.Path);
        var factory = new RemoteFlowDbContextFactory(paths.DataDirectory);
        using var settings = new SettingsStore(factory, new FakeClock(_now));
        var initializer = new DbInitializer(factory, settings, paths, new FakeClock(_now));

        await initializer.InitializeAsync(cancellationToken);

        Assert.True(File.Exists(Path.Combine(paths.DataDirectory, RemoteFlowDatabase.FileName)));
        Assert.True(Directory.Exists(paths.ConfigDirectory));
        Assert.True(Directory.Exists(paths.CacheDirectory));
        Assert.True(Directory.Exists(paths.LogDirectory));
        Assert.Empty(Directory.GetFiles(paths.DataDirectory, "*.bak"));
        Assert.Equal(DbInitializer.CurrentSchemaVersion, await settings.Get(SettingKeys.SchemaVersion, cancellationToken));
        await using var context = factory.CreateDbContext();
        Assert.Empty(await context.Database.GetPendingMigrationsAsync(cancellationToken));
    }

    [Fact]
    public async Task ExistingDatabaseIsBackedUpOnlyWhenMigrationsArePending()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = TemporaryDirectory.Create();
        var paths = TestAppPaths.Under(directory.Path);
        paths.EnsureDirectories();
        var databasePath = Path.Combine(paths.DataDirectory, RemoteFlowDatabase.FileName);
        await File.WriteAllBytesAsync(databasePath, [], cancellationToken);
        var factory = new RemoteFlowDbContextFactory(paths.DataDirectory);
        using var settings = new SettingsStore(factory, new FakeClock(_now));
        var initializer = new DbInitializer(factory, settings, paths, new FakeClock(_now));

        await initializer.InitializeAsync(cancellationToken);

        var expectedBackup = Path.Combine(paths.DataDirectory, "remoteflow.20260807-123456.bak");
        Assert.True(File.Exists(expectedBackup));

        await initializer.InitializeAsync(cancellationToken);

        _ = Assert.Single(Directory.GetFiles(paths.DataDirectory, "*.bak"));
    }

    [Fact]
    public async Task NewerSchemaVersionIsRejectedWithoutChangingTheDatabase()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = TemporaryDirectory.Create();
        var (initializer, factory, paths, settings) = CreateInitializer(directory.Path);
        using (settings)
        {
            await initializer.InitializeAsync(cancellationToken);
            await settings.Set(SettingKeys.SchemaVersion, DbInitializer.CurrentSchemaVersion + 1, cancellationToken);
            var databasePath = Path.Combine(paths.DataDirectory, RemoteFlowDatabase.FileName);
            SqliteConnection.ClearAllPools();
            var before = await File.ReadAllBytesAsync(databasePath, cancellationToken);

            var exception = await Assert.ThrowsAsync<NewerDatabaseSchemaException>(
                () => initializer.InitializeAsync(cancellationToken));

            Assert.Contains("Upgrade RemoteFlow", exception.Message, StringComparison.Ordinal);
            Assert.Equal(DbInitializer.CurrentSchemaVersion + 1, exception.DatabaseSchemaVersion);
            SqliteConnection.ClearAllPools();
            Assert.Equal(before, await File.ReadAllBytesAsync(databasePath, cancellationToken));
            Assert.Empty(Directory.GetFiles(paths.DataDirectory, "*.bak"));
        }
    }

    [Fact]
    public async Task CorruptDatabaseProducesActionableExceptionNamingTheFile()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = TemporaryDirectory.Create();
        var paths = TestAppPaths.Under(directory.Path);
        paths.EnsureDirectories();
        var databasePath = Path.Combine(paths.DataDirectory, RemoteFlowDatabase.FileName);
        await File.WriteAllTextAsync(databasePath, "this is not sqlite", cancellationToken);
        var factory = new RemoteFlowDbContextFactory(paths.DataDirectory);
        using var settings = new SettingsStore(factory, new FakeClock(_now));
        var initializer = new DbInitializer(factory, settings, paths, new FakeClock(_now));

        var exception = await Assert.ThrowsAsync<DatabaseInitializationException>(
            () => initializer.InitializeAsync(cancellationToken));

        Assert.Contains(databasePath, exception.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(databasePath));
        _ = Assert.IsType<SqliteException>(exception.InnerException);
    }

    [Fact]
    public void GuidProviderGeneratesSortableVersionSevenValues()
    {
        var first = Domain.Abstractions.SystemGuidProvider.Instance.NewGuid();
        var second = Domain.Abstractions.SystemGuidProvider.Instance.NewGuid();

        Assert.Equal('7', first.ToString("D")[14]);
        Assert.Equal('7', second.ToString("D")[14]);
        Assert.True(first.CompareTo(second) < 0);
    }

    private static (DbInitializer Initializer, RemoteFlowDbContextFactory Factory, TestAppPaths Paths, SettingsStore Settings)
        CreateInitializer(string root)
    {
        var paths = TestAppPaths.Under(root);
        var factory = new RemoteFlowDbContextFactory(paths.DataDirectory);
        var clock = new FakeClock(_now);
        var settings = new SettingsStore(factory, clock);
        return (new DbInitializer(factory, settings, paths, clock), factory, paths, settings);
    }
}

internal sealed class TestAppPaths(
    string configDirectory,
    string dataDirectory,
    string cacheDirectory,
    string logDirectory) : IAppPaths
{
    public string ConfigDirectory { get; } = configDirectory;

    public string DataDirectory { get; } = dataDirectory;

    public string CacheDirectory { get; } = cacheDirectory;

    public string LogDirectory { get; } = logDirectory;

    public static TestAppPaths Under(string root)
    {
        return new TestAppPaths(
            Path.Combine(root, "config"),
            Path.Combine(root, "data"),
            Path.Combine(root, "cache"),
            Path.Combine(root, "logs"));
    }

    public void EnsureDirectories()
    {
        _ = Directory.CreateDirectory(ConfigDirectory);
        _ = Directory.CreateDirectory(DataDirectory);
        _ = Directory.CreateDirectory(CacheDirectory);
        _ = Directory.CreateDirectory(LogDirectory);
    }
}

internal sealed class TemporaryDirectory(string path) : IDisposable
{
    public string Path { get; } = path;

    public static TemporaryDirectory Create()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RemoteFlow.Infrastructure.Tests", Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(path);
        return new TemporaryDirectory(path);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(Path, recursive: true);
    }
}
