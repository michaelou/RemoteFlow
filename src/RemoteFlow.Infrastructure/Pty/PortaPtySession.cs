using System.IO.Pipelines;
using Porta.Pty;
using RemoteFlow.Application.Abstractions;

namespace RemoteFlow.Infrastructure.Pty;

internal sealed class PortaPtySession : IPtySession
{
    private readonly IPtyConnection _connection;
    private readonly Pipe _output = new(new PipeOptions(
        pauseWriterThreshold: 1024 * 1024,
        resumeWriterThreshold: 512 * 1024,
        useSynchronizationContext: false));
    private readonly CancellationTokenSource _pumpCancellation = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly TaskCompletionSource<int?> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _pump;
    private int _closedRaised;
    private int _disposeStarted;
    private int _killRequested;

    public PortaPtySession(IPtyConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        ProcessId = connection.Pid;
        connection.ProcessExited += OnProcessExited;
        _pump = PumpOutputAsync(_pumpCancellation.Token);
        if (connection.WaitForExit(0))
        {
            CompleteExit(connection.ExitCode);
        }
    }

    public event EventHandler<ChannelClosedEventArgs>? Closed;

    public int ProcessId { get; }

    public PipeReader Output => _output.Reader;

    public Task<int?> Exited => _exited.Task;

    public async ValueTask WriteAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
        if (data.IsEmpty)
        {
            return;
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
            await _connection.WriterStream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            await _connection.WriterStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = _writeGate.Release();
        }
    }

    public ValueTask ResizeAsync(
        int columns,
        int rows,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columns);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rows);
        cancellationToken.ThrowIfCancellationRequested();
        _connection.Resize(columns, rows);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        _connection.ProcessExited -= OnProcessExited;
        if (!_exited.Task.IsCompleted)
        {
            _ = Interlocked.Exchange(ref _killRequested, 1);
            try
            {
                _connection.Kill();
                _ = _connection.WaitForExit(5_000);
            }
            catch
            {
                // The child can exit between the completion check and Kill.
            }

            CompleteExit(exitCode: null);
        }

        _pumpCancellation.Cancel();
        _connection.Dispose();
        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _writeGate.Dispose();
        _pumpCancellation.Dispose();
    }

    private async Task PumpOutputAsync(CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var memory = _output.Writer.GetMemory(16 * 1024);
                var read = await _connection.ReaderStream.ReadAsync(memory, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                _output.Writer.Advance(read);
                var flush = await _output.Writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (flush.IsCanceled || flush.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposeStarted) != 0)
        {
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            await _output.Writer.CompleteAsync(failure).ConfigureAwait(false);
        }
    }

    private void OnProcessExited(object? sender, PtyExitedEventArgs e)
    {
        CompleteExit(e.ExitCode);
    }

    private void CompleteExit(int? exitCode)
    {
        var killed = Volatile.Read(ref _killRequested) != 0;
        var effectiveExitCode = killed ? null : exitCode;
        if (!_exited.TrySetResult(effectiveExitCode))
        {
            return;
        }

        if (Interlocked.Exchange(ref _closedRaised, 1) == 0)
        {
            Closed?.Invoke(this, new ChannelClosedEventArgs(effectiveExitCode, killed));
        }
    }
}
