using Porta.Pty;
using RemoteFlow.Application.Abstractions;

namespace RemoteFlow.Infrastructure.Pty;

public sealed class PortaPtyService : IPtyService
{
    public async Task<IPtySession> SpawnAsync(
        PtySpawnOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        Validate(options);
        cancellationToken.ThrowIfCancellationRequested();
        IPtyConnection? connection = null;
        try
        {
            connection = await PtyProvider.SpawnAsync(new PtyOptions
            {
                Name = "RemoteFlow",
                App = options.ShellPath,
                CommandLine = [.. options.Arguments],
                Cwd = options.WorkingDirectory,
                Environment = new Dictionary<string, string>(options.EnvironmentVariables, StringComparer.Ordinal),
                Cols = options.Columns,
                Rows = options.Rows,
            }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var session = new PortaPtySession(connection);
            connection = null;
            return session;
        }
        finally
        {
            if (connection is not null)
            {
                TryKill(connection);
                connection.Dispose();
            }
        }
    }

    private static void Validate(PtySpawnOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ShellPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.WorkingDirectory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.Columns);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.Rows);
        if (options.Arguments.Any(argument => argument is null))
        {
            throw new ArgumentException("Shell arguments cannot contain null values.", nameof(options));
        }

        if (options.EnvironmentVariables.Any(variable =>
                string.IsNullOrEmpty(variable.Key) || variable.Value is null))
        {
            throw new ArgumentException("Environment variable names and values cannot be null.", nameof(options));
        }
    }

    private static void TryKill(IPtyConnection connection)
    {
        try
        {
            connection.Kill();
            _ = connection.WaitForExit(5_000);
        }
        catch
        {
            // Spawn cleanup is best-effort and must preserve the original failure.
        }
    }
}
