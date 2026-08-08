using System.Collections.Concurrent;
using System.IO.Pipelines;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Ssh;

namespace RemoteFlow.TestSupport;

public sealed class FakeSshTransport : ISshTransport
{
    private readonly ConcurrentQueue<SshFailure> _connectFailures = new();

    public List<SshConnectRequest> ConnectRequests { get; } = [];

    public List<FakeSshConnection> Connections { get; } = [];

    public FakeSshConnection? LastConnection { get; private set; }

    public void FailNextConnect(SshError error, string? message = null)
    {
        _connectFailures.Enqueue(new SshFailure(error, message ?? $"Scripted SSH failure: {error}."));
    }

    public Task<SshResult<ISshConnection>> ConnectAsync(
        SshConnectRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ConnectRequests.Add(request);

        if (_connectFailures.TryDequeue(out var failure))
        {
            return Task.FromResult(SshResult<ISshConnection>.Fail(failure.Error, failure.Message));
        }

        LastConnection = new FakeSshConnection();
        Connections.Add(LastConnection);
        return Task.FromResult(SshResult<ISshConnection>.Success(LastConnection));
    }
}

public sealed class FakeSshConnection : ISshConnection
{
    private readonly ConcurrentQueue<SshFailure> _shellFailures = new();
    private readonly ConcurrentQueue<SshFailure> _execFailures = new();
    private int _disconnected;

    public bool IsDisconnected => Volatile.Read(ref _disconnected) != 0;

    public FakeSshShell? LastShell { get; private set; }

    public FakeSftpService Sftp { get; } = new();

    public Func<string, SshExecResult> Execute { get; set; } = command => new(0, command, string.Empty);

    public event EventHandler<SshDisconnectedEventArgs>? Disconnected;

    public void FailNextShell(SshError error, string? message = null)
    {
        _shellFailures.Enqueue(new SshFailure(error, message ?? $"Scripted shell failure: {error}."));
    }

    public void FailNextExecute(SshError error, string? message = null)
    {
        _execFailures.Enqueue(new SshFailure(error, message ?? $"Scripted exec failure: {error}."));
    }

    public Task<SshResult<ISshShell>> OpenShellAsync(
        TerminalSpec terminal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        cancellationToken.ThrowIfCancellationRequested();

        if (_shellFailures.TryDequeue(out var failure))
        {
            return Task.FromResult(SshResult<ISshShell>.Fail(failure.Error, failure.Message));
        }

        LastShell = new FakeSshShell();
        return Task.FromResult(SshResult<ISshShell>.Success(LastShell));
    }

    public Task<SshResult<SshExecResult>> ExecuteAsync(
        string command,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        cancellationToken.ThrowIfCancellationRequested();

        return _execFailures.TryDequeue(out var failure)
            ? Task.FromResult(SshResult<SshExecResult>.Fail(failure.Error, failure.Message))
            : Task.FromResult(SshResult<SshExecResult>.Success(Execute(command)));
    }

    public ISftpService OpenSftp()
    {
        return Sftp;
    }

    public async ValueTask DisconnectAsync(
        SshError? error = SshError.NetworkChanged,
        string? message = "The scripted connection was disconnected.")
    {
        if (Interlocked.Exchange(ref _disconnected, 1) != 0)
        {
            return;
        }

        if (LastShell is not null)
        {
            await LastShell.DisconnectAsync().ConfigureAwait(false);
        }

        Disconnected?.Invoke(this, new SshDisconnectedEventArgs(error, message));
    }

    public ValueTask DisposeAsync()
    {
        return DisconnectAsync(null, null);
    }
}

public sealed class FakeSshShell : ISshShell
{
    private readonly Pipe _output = new();
    private readonly TaskCompletionSource<int?> _exited =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _closed;

    public bool EchoInput { get; set; } = true;

    public PipeReader Output => _output.Reader;

    public Task<int?> Exited => _exited.Task;

    public List<byte[]> Writes { get; } = [];

    public List<(int Columns, int Rows)> Resizes { get; } = [];

    public event EventHandler<ChannelClosedEventArgs>? Closed;

    public async ValueTask WriteAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfClosed();
        Writes.Add(data.ToArray());
        if (EchoInput)
        {
            _ = await _output.Writer.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        }
    }

    public ValueTask ResizeAsync(
        int columns,
        int rows,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfClosed();
        Resizes.Add((columns, rows));
        return ValueTask.CompletedTask;
    }

    public async Task PublishAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        ThrowIfClosed();
        _ = await _output.Writer.WriteAsync(data, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisconnectAsync(int? exitCode = null, bool wasKilled = false)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        await _output.Writer.CompleteAsync().ConfigureAwait(false);
        _ = _exited.TrySetResult(exitCode);
        Closed?.Invoke(this, new ChannelClosedEventArgs(exitCode, wasKilled));
    }

    public ValueTask DisposeAsync()
    {
        return DisconnectAsync(wasKilled: true);
    }

    private void ThrowIfClosed()
    {
        if (Volatile.Read(ref _closed) != 0)
        {
            throw new InvalidOperationException("The fake SSH shell is closed.");
        }
    }
}

public sealed class FakeSftpService : ISftpService
{
    private readonly ConcurrentDictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _directories = new(StringComparer.Ordinal);

    public Task<IReadOnlyList<SftpEntry>> ListDirectoryAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var prefix = path.TrimEnd('/') + "/";
        var entries = _files
            .Where(item => item.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(item => new SftpEntry(
                item.Key[prefix.Length..],
                item.Key,
                false,
                item.Value.LongLength,
                DateTimeOffset.UnixEpoch))
            .ToArray();
        return Task.FromResult<IReadOnlyList<SftpEntry>>(entries);
    }

    public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _files.TryGetValue(path, out var contents)
            ? Task.FromResult<Stream>(new MemoryStream(contents, writable: false))
            : throw new FileNotFoundException("The scripted remote file does not exist.", path);
    }

    public Task<Stream> OpenWriteAsync(
        string path,
        bool overwrite,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return overwrite || !_files.ContainsKey(path)
            ? Task.FromResult<Stream>(new CapturingWriteStream(bytes => _files[path] = bytes))
            : throw new IOException("The scripted remote file already exists.");
    }

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _directories[path] = 0;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = _files.TryRemove(path, out _);
        _ = _directories.TryRemove(path, out _);
        return Task.CompletedTask;
    }

    public Task MoveAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_files.TryRemove(sourcePath, out var contents))
        {
            throw new FileNotFoundException("The scripted remote file does not exist.", sourcePath);
        }

        _files[destinationPath] = contents;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    private sealed class CapturingWriteStream(Action<byte[]> capture) : MemoryStream
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                capture(ToArray());
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            capture(ToArray());
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
