using System.Collections.Concurrent;
using RemoteFlow.Application.Abstractions.Sftp;
using RemoteFlow.Application.Abstractions.Ssh;
using Renci.SshNet;

namespace RemoteFlow.Infrastructure.Ssh;

internal sealed class SshNetConnection : ISshConnection
{
    private readonly SshClient _client;
    private readonly TimeSpan _operationTimeout;
    private readonly Func<CancellationToken, SftpClient> _sftpFactory;
    private readonly ConcurrentDictionary<SshNetShellChannel, byte> _shells = new();
    private int _disconnected;
    private int _disposed;

    public SshNetConnection(
        SshClient client,
        TimeSpan operationTimeout,
        Func<CancellationToken, SftpClient> sftpFactory)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _operationTimeout = operationTimeout;
        _sftpFactory = sftpFactory ?? throw new ArgumentNullException(nameof(sftpFactory));
        _client.ErrorOccurred += OnErrorOccurred;
    }

    public event EventHandler<SshDisconnectedEventArgs>? Disconnected;

    public Task<SshResult<ISshShell>> OpenShellAsync(
        TerminalSpec terminal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _disposed) != 0 || !_client.IsConnected)
        {
            return Task.FromResult(SshResult<ISshShell>.Fail(
                SshError.ChannelClosed,
                SshErrorMessages.ToUserMessage(SshError.ChannelClosed)));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(terminal.TerminalType);
        ArgumentOutOfRangeException.ThrowIfLessThan(terminal.Columns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(terminal.Rows, 1);
        try
        {
            var stream = _client.CreateShellStream(
                terminal.TerminalType,
                (uint)terminal.Columns,
                (uint)terminal.Rows,
                0,
                0,
                16 * 1024);
            var shell = new SshNetShellChannel(stream);
            _ = _shells.TryAdd(shell, 0);
            shell.Closed += OnShellClosed;
            return Task.FromResult(SshResult<ISshShell>.Success(shell));
        }
        catch (Exception exception)
        {
            return Task.FromResult(SshErrorMapper.Failure<ISshShell>(exception, cancellationToken));
        }
    }

    public async Task<SshResult<SshExecResult>> ExecuteAsync(
        string command,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (Volatile.Read(ref _disposed) != 0 || !_client.IsConnected)
        {
            return SshResult<SshExecResult>.Fail(
                SshError.ChannelClosed,
                SshErrorMessages.ToUserMessage(SshError.ChannelClosed));
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_operationTimeout);
        try
        {
            using var sshCommand = _client.CreateCommand(command);
            sshCommand.CommandTimeout = _operationTimeout;
            await sshCommand.ExecuteAsync(timeout.Token).ConfigureAwait(false);
            return SshResult<SshExecResult>.Success(new(
                sshCommand.ExitStatus ?? -1,
                sshCommand.Result,
                sshCommand.Error));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            return SshResult<SshExecResult>.Fail(SshError.Timeout, SshErrorMessages.ToUserMessage(SshError.Timeout));
        }
        catch (Exception exception)
        {
            return SshErrorMapper.Failure<SshExecResult>(exception, cancellationToken);
        }
    }

    public ISftpService OpenSftp()
    {
        return new SshNetSftpService(_sftpFactory, _operationTimeout);
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
        _client.ErrorOccurred -= OnErrorOccurred;
        _client.Dispose();
        SignalDisconnected(null, null);
    }

    private void OnErrorOccurred(object? sender, Renci.SshNet.Common.ExceptionEventArgs eventArgs)
    {
        var failure = SshErrorMapper.Failure<object>(eventArgs.Exception, CancellationToken.None).Failure;
        SignalDisconnected(failure.Error, failure.Message);
    }

    private void OnShellClosed(object? sender, EventArgs eventArgs)
    {
        if (sender is SshNetShellChannel shell)
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
}
