using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace RemoteFlow.Persistence;

public static class RemoteFlowDatabase
{
    public const string FileName = "remoteflow.db";

    public static string CreateConnectionString(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        return new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(Path.GetFullPath(dataDirectory), FileName),
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
        }.ToString();
    }

    internal static DbContextOptions<RemoteFlowDbContext> CreateOptions(string dataDirectory)
    {
        var builder = new DbContextOptionsBuilder<RemoteFlowDbContext>();
        Configure(builder, dataDirectory);
        return builder.Options;
    }

    internal static void Configure(DbContextOptionsBuilder builder, string dataDirectory)
    {
        _ = builder
            .UseSqlite(CreateConnectionString(dataDirectory))
            .AddInterceptors(SqlitePragmaConnectionInterceptor.Instance);
    }
}
