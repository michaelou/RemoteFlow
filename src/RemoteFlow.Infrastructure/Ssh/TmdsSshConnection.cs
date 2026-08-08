using System.Collections.Concurrent;
using RemoteFlow.Application.Abstractions.Sftp;
using RemoteFlow.Application.Abstractions.Ssh;
using RemoteFlow.Infrastructure.Sftp;
using Tmds.Ssh;

namespace RemoteFlow.Infrastructure.Ssh;

internal sealed class TmdsSshConnection : ISshConnection
{
    private readonly SshClient _client;
    private readonly TimeSpan _operationTimeout;
    private readonly ConcurrentDictionary<SshShellChannel, byte> _shells = new();
    private readonly CancellationTokenRegistration _disconnectedRegistration;
    private int _disconnected;
    private int _disposed;

    public TmdsSshConnection(SshClient client, TimeSpan operationTimeout)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _operationTimeout = operationTimeout;
        _disconnectedRegistration = client.Disconnected.Register(
            static state => ((TmdsSshConnection)state!).SignalDisconnected(
                SshError.ChannelClosed,
                SshErrorMessages.ToUserMessage(SshError.ChannelClosed)),
            this);
    }

    public event EventHandler<SshDisconnectedEventArgs>? Disconnected;

    public async Task<SshResult<ISshShell>> OpenShellAsync(
        TerminalSpec terminal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        if (Volatile.Read(ref _disposed) != 0)
        {
            return SshResult<ISshShell>.Fail(SshError.ChannelClosed, "The SSH connection is closed.");
        }

        Validate(terminal);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_operationTimeout);
        try
        {
            var process = await _client.ExecuteShellAsync(new ExecuteOptions
            {
                AllocateTerminal = true,
                TerminalType = terminal.TerminalType,
                TerminalWidth = terminal.Columns,
                TerminalHeight = terminal.Rows,
            }, timeout.Token).ConfigureAwait(false);
            var shell = new SshShellChannel(process);
            _ = _shells.TryAdd(shell, 0);
            shell.Closed += OnShellClosed;
            return SshResult<ISshShell>.Success(shell);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            return SshResult<ISshShell>.Fail(SshError.Timeout, "Opening the SSH shell timed out.");
        }
        catch (Exception exception)
        {
            return SshErrorMapper.Failure<ISshShell>(exception, cancellationToken);
        }
    }

    public async Task<SshResult<SshExecResult>> ExecuteAsync(
        string command,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (Volatile.Read(ref _disposed) != 0)
        {
            return SshResult<SshExecResult>.Fail(SshError.ChannelClosed, "The SSH connection is closed.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_operationTimeout);
        try
        {
            using var process = await _client.ExecuteAsync(command, timeout.Token).ConfigureAwait(false);
            process.WriteEof();
            var (standardOutput, standardError) =
                await process.ReadToEndAsStringAsync(timeout.Token).ConfigureAwait(false);
            var exitCode = await process.GetExitCodeAsync(timeout.Token).ConfigureAwait(false);
            return SshResult<SshExecResult>.Success(new(exitCode, standardOutput, standardError));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            return SshResult<SshExecResult>.Fail(SshError.Timeout, "The SSH command timed out.");
        }
        catch (Exception exception)
        {
            return SshErrorMapper.Failure<SshExecResult>(exception, cancellationToken);
        }
    }

    public ISftpService OpenSftp()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return new TmdsSftpService(_client);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var shell in _shells.Keys)
        {
            await shell.DisposeAsync().ConfigureAwait(false);
        }

        _shells.Clear();
        _disconnectedRegistration.Dispose();
        _client.Dispose();
        SignalDisconnected(null, null);
    }

    private void OnShellClosed(object? sender, EventArgs eventArgs)
    {
        if (sender is SshShellChannel shell)
        {
            shell.Closed -= OnShellClosed;
            _ = _shells.TryRemove(shell, out _);
        }
    }

    private void SignalDisconnected(SshError? error, string? message)
    {
        if (Interlocked.Exchange(ref _disconnected, 1) == 0)
        {
            Disconnected?.Invoke(this, new SshDisconnectedEventArgs(error, message));
        }
    }

    private static void Validate(TerminalSpec terminal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(terminal.TerminalType);
        ArgumentOutOfRangeException.ThrowIfLessThan(terminal.Columns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(terminal.Rows, 1);
    }

}
