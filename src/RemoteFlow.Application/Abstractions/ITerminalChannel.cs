using System.IO.Pipelines;

namespace RemoteFlow.Application.Abstractions;

public sealed class ChannelClosedEventArgs(int? exitCode, bool wasKilled) : EventArgs
{
    public int? ExitCode { get; } = exitCode;

    public bool WasKilled { get; } = wasKilled;
}

public interface ITerminalChannel : IAsyncDisposable
{
    PipeReader Output { get; }

    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default);

    Task<int?> Exited { get; }

    event EventHandler<ChannelClosedEventArgs>? Closed;
}

public sealed record PtySpawnOptions
{
    public required string ShellPath { get; init; }

    public IReadOnlyList<string> Arguments { get; init; } = [];

    public required string WorkingDirectory { get; init; }

    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public int Columns { get; init; } = 120;

    public int Rows { get; init; } = 30;
}

public interface IPtySession : ITerminalChannel
{
    int ProcessId { get; }
}

public interface IPtyService
{
    Task<IPtySession> SpawnAsync(
        PtySpawnOptions options,
        CancellationToken cancellationToken = default);
}
