namespace RemoteFlow.Application.Abstractions;

public interface IDbInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

public class DatabaseInitializationException(
    string message,
    string databasePath,
    string? backupPath,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string DatabasePath { get; } = string.IsNullOrWhiteSpace(databasePath)
        ? throw new ArgumentException("The database path is required.", nameof(databasePath))
        : databasePath;

    public string? BackupPath { get; } = backupPath;
}

public sealed class NewerDatabaseSchemaException(
    string databasePath,
    int databaseSchemaVersion,
    int applicationSchemaVersion) : DatabaseInitializationException(
        $"The database '{databasePath}' uses schema version {databaseSchemaVersion}, but this " +
        $"RemoteFlow build supports version {applicationSchemaVersion}. Upgrade RemoteFlow before opening this database.",
        databasePath,
        null)
{
    public int DatabaseSchemaVersion { get; } = databaseSchemaVersion;

    public int ApplicationSchemaVersion { get; } = applicationSchemaVersion;
}
