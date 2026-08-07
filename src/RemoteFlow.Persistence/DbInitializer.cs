using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RemoteFlow.Application.Abstractions;

namespace RemoteFlow.Persistence;

public sealed class DbInitializer(
    IDbContextFactory<RemoteFlowDbContext> contextFactory,
    ISettingsStore settingsStore,
    IAppPaths appPaths,
    IClock clock) : IDbInitializer
{
    public const int CurrentSchemaVersion = 1;

    private readonly IDbContextFactory<RemoteFlowDbContext> _contextFactory =
        contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    private readonly ISettingsStore _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
    private readonly IAppPaths _appPaths = appPaths ?? throw new ArgumentNullException(nameof(appPaths));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _appPaths.EnsureDirectories();
        var databasePath = Path.Combine(_appPaths.DataDirectory, RemoteFlowDatabase.FileName);
        string? backupPath = null;

        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await GuardAgainstNewerSchemaAsync(context, databasePath, cancellationToken).ConfigureAwait(false);

            var pendingMigrations = await context.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false);
            if (pendingMigrations.Any() && File.Exists(databasePath))
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                backupPath = CreateBackup(databasePath);
            }

            await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
            await _settingsStore.SeedDefaults(cancellationToken).ConfigureAwait(false);
        }
        catch (NewerDatabaseSchemaException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var recovery = backupPath is null
                ? $"No backup was created. The database file remains at '{databasePath}'."
                : $"The pre-migration backup remains at '{backupPath}'.";
            throw new DatabaseInitializationException(
                $"RemoteFlow could not initialize the database '{databasePath}'. {recovery}",
                databasePath,
                backupPath,
                exception);
        }
    }

    private static async Task GuardAgainstNewerSchemaAsync(
        RemoteFlowDbContext context,
        string databasePath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(databasePath))
        {
            return;
        }

        var connection = context.Database.GetDbConnection();
        await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM Settings WHERE Key = 'SchemaVersion' LIMIT 1;";

        try
        {
            var rawValue = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (rawValue is string json &&
                JsonSerializer.Deserialize<int>(json) is var schemaVersion &&
                schemaVersion > CurrentSchemaVersion)
            {
                throw new NewerDatabaseSchemaException(databasePath, schemaVersion, CurrentSchemaVersion);
            }
        }
        catch (Microsoft.Data.Sqlite.SqliteException exception) when (
            exception.SqliteErrorCode == 1 &&
            exception.Message.Contains("no such table: Settings", StringComparison.Ordinal))
        {
            // A pre-migration database may not have the Settings table yet.
        }
        finally
        {
            await context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private string CreateBackup(string databasePath)
    {
        var timestamp = _clock.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var directory = Path.GetDirectoryName(databasePath)!;
        var baseName = Path.GetFileNameWithoutExtension(databasePath);
        var backupPath = Path.Combine(directory, $"{baseName}.{timestamp}.bak");
        for (var suffix = 1; File.Exists(backupPath); suffix++)
        {
            backupPath = Path.Combine(directory, $"{baseName}.{timestamp}-{suffix}.bak");
        }

        File.Copy(databasePath, backupPath, overwrite: false);
        return backupPath;
    }
}
