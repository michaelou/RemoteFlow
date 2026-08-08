using System.Buffers;
using System.IO.Pipelines;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Ssh;
using Renci.SshNet;

namespace RemoteFlow.Infrastructure.Ssh;

internal sealed class SshNetShellChannel : ISshShell
{
    private const int _bufferSize = 16 * 1024;
    private readonly ShellStream _stream;
    private readonly Pipe _output = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource<int?> _exited =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _pump;
    private int _closed;
    private int _disposed;

    public SshNetShellChannel(ShellStream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
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
        await _stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
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
        _stream.ChangeWindowSize((uint)columns, (uint)rows, 0, 0);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        _stream.Dispose();
        await _pump.ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private async Task PumpOutputAsync()
    {
        var buffer = ArrayPool<byte>.Shared.Rent(_bufferSize);
        var wasKilled = false;
        try
        {
            while (true)
            {
                var bytesRead = await _stream.ReadAsync(
                    buffer.AsMemory(0, _bufferSize),
                    _lifetime.Token).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    break;
                }

                _ = await _output.Writer.WriteAsync(
                    buffer.AsMemory(0, bytesRead),
                    _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            wasKilled = true;
        }
        catch (ObjectDisposedException) when (_lifetime.IsCancellationRequested)
        {
            wasKilled = true;
        }
        catch (IOException)
        {
            wasKilled = true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            await _output.Writer.CompleteAsync().ConfigureAwait(false);
            int? exitCode = wasKilled ? null : 0;
            _ = _exited.TrySetResult(exitCode);
            if (Interlocked.Exchange(ref _closed, 1) == 0)
            {
                Closed?.Invoke(this, new ChannelClosedEventArgs(exitCode, wasKilled));
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}
