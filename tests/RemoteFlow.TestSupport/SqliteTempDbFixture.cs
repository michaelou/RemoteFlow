using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RemoteFlow.Persistence;

namespace RemoteFlow.TestSupport;

public sealed class SqliteTempDbFixture : IDisposable, IAsyncDisposable
{
    private bool _disposed;

    private SqliteTempDbFixture(string dataDirectory)
    {
        DataDirectory = dataDirectory;
        Factory = new RemoteFlowDbContextFactory(dataDirectory);
    }

    public string DataDirectory { get; }

    public string DatabasePath => Path.Combine(DataDirectory, RemoteFlowDatabase.FileName);

    public RemoteFlowDbContextFactory Factory { get; }

    public static async Task<SqliteTempDbFixture> CreateAsync(CancellationToken cancellationToken = default)
    {
        var dataDirectory = Path.Combine(
            Path.GetTempPath(),
            "RemoteFlow.Tests",
            Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(dataDirectory);
        var fixture = new SqliteTempDbFixture(dataDirectory);

        try
        {
            await using var context = await fixture.Factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
            return fixture;
        }
        catch
        {
            fixture.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        SqliteConnection.ClearAllPools();
        if (Directory.Exists(DataDirectory))
        {
            Directory.Delete(DataDirectory, true);
        }

        _disposed = true;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
