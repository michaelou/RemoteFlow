using System.Buffers;
using System.IO.Pipelines;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Ssh;
using Tmds.Ssh;

namespace RemoteFlow.Infrastructure.Ssh;

internal sealed class SshShellChannel : ISshShell
{
    private const int _bufferSize = 16 * 1024;
    private readonly RemoteProcess _process;
    private readonly Pipe _output = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource<int?> _exited =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _pump;
    private int _closed;
    private int _disposed;

    public SshShellChannel(RemoteProcess process)
    {
        _process = process ?? throw new ArgumentNullException(nameof(process));
        _pump = PumpOutputAsync();
    }

    public PipeReader Output => _output.Reader;

    public Task<int?> Exited => _exited.Task;

    public event EventHandler<ChannelClosedEventArgs>? Closed;

    public async ValueTask WriteAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _process.WriteAsync(data, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask ResizeAsync(
        int columns,
        int rows,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        _ = _process.SetTerminalSize(columns, rows)
            ? true
            : throw new InvalidOperationException("The SSH shell closed before it could be resized.");

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        _process.Dispose();
        await _pump.ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private async Task PumpOutputAsync()
    {
        var standardOutput = ArrayPool<byte>.Shared.Rent(_bufferSize);
        var standardError = ArrayPool<byte>.Shared.Rent(_bufferSize);
        int? exitCode = null;
        var wasKilled = false;
        try
        {
            while (true)
            {
                var (isError, bytesRead) = await _process.ReadAsync(
                    standardOutput.AsMemory(0, _bufferSize),
                    standardError.AsMemory(0, _bufferSize),
                    _lifetime.Token).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    break;
                }

                var source = isError ? standardError : standardOutput;
                _ = await _output.Writer.WriteAsync(
                    source.AsMemory(0, bytesRead),
                    _lifetime.Token).ConfigureAwait(false);
            }

            exitCode = await _process.GetExitCodeAsync(_lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            wasKilled = true;
        }
        catch (ObjectDisposedException) when (_lifetime.IsCancellationRequested)
        {
            wasKilled = true;
        }
        catch (SshException)
        {
            wasKilled = true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(standardOutput);
            ArrayPool<byte>.Shared.Return(standardError);
            await _output.Writer.CompleteAsync().ConfigureAwait(false);
            _ = _exited.TrySetResult(exitCode);
            SignalClosed(exitCode, wasKilled);
        }
    }

    private void SignalClosed(int? exitCode, bool wasKilled)
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            Closed?.Invoke(this, new ChannelClosedEventArgs(exitCode, wasKilled));
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}
