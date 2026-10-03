using System.Runtime.CompilerServices;
using RemoteFlow.Application.Abstractions.Ssh;
using Tmds.Ssh;

namespace RemoteFlow.Infrastructure.Ssh;

/// <summary>A running exec channel read a line at a time. Tmds.Ssh already interleaves standard output and
/// standard error in arrival order, so this is a thin loop over <see cref="RemoteProcess.ReadLineAsync(bool, bool, CancellationToken)"/>.
/// </summary>
internal sealed class TmdsRunningCommand(RemoteProcess process) : ISshRunningCommand
{
    private readonly CancellationTokenSource _disposal = new();
    private int _enumerated;
    private int _disposed;

    public int? ExitCode { get; private set; }

    public SshFailure? Failure { get; private set; }

    public async IAsyncEnumerable<SshOutputLine> ReadLinesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _enumerated, 1) != 0)
        {
            throw new InvalidOperationException("A command stream can be enumerated once.");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposal.Token);
        while (true)
        {
            SshOutputLine? next;
            try
            {
                var (isError, line) = await process.ReadLineAsync(
                    readStdout: true,
                    readStderr: true,
                    linked.Token).ConfigureAwait(false);
                if (line is null)
                {
                    ExitCode = await process.GetExitCodeAsync(linked.Token).ConfigureAwait(false);
                    yield break;
                }

                next = new SshOutputLine(isError ? SshOutputKind.StandardError : SshOutputKind.StandardOutput, line);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception) when (Volatile.Read(ref _disposed) != 0)
            {
                // Disposed from another thread while a read was pending: the caller asked for the stream to
                // stop, so ending quietly is the answer, not an error.
                yield break;
            }
            catch (Exception exception)
            {
                Failure = SshErrorMapper.Failure<object>(exception, cancellationToken).Failure;
                yield break;
            }

            yield return next.Value;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        _disposal.Cancel();
        // Disposing the process closes the channel; for `docker logs -f` that is what makes the server stop.
        process.Dispose();
        _disposal.Dispose();
        return ValueTask.CompletedTask;
    }
}
