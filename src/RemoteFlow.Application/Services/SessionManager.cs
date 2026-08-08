using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Text;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Ssh;
using RemoteFlow.Domain.Abstractions;
using RemoteFlow.Domain.Entities;
using RemoteFlow.Domain.Enums;

namespace RemoteFlow.Application.Services;

public sealed class SessionManager(
    IConnectionRepository connections,
    ISshAuthenticationMaterialProvider authentication,
    ISshTransport transport,
    IRecentConnectionStore recent,
    IClock clock,
    IGuidProvider guidProvider) : ISessionManager
{
    private static readonly TimeSpan _defaultOperationTimeout = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<Guid, SessionResources> _sessions = [];
    private int _shutdown;

    public event EventHandler<ManagedSshSession>? SessionAdded;
    public event EventHandler<ManagedSshSession>? SessionRemoved;
    public event EventHandler<SessionTransitionEventArgs>? SessionChanged;

    public IReadOnlyList<ManagedSshSession> Sessions =>
        [.. _sessions.Values.Select(item => item.Session).OrderBy(item => item.SessionId)];

    public async Task<ManagedSshSession> OpenAsync(
        Guid connectionId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _shutdown) != 0, this);
        var connection = await connections.GetByIdAsync(connectionId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Connection '{connectionId}' was not found.");
        if (connection.Protocol is not ProtocolType.Ssh and not ProtocolType.Sftp)
        {
            throw new InvalidOperationException("Only SSH and SFTP connections can open an SSH terminal session.");
        }

        var existingTitles = GetForConnection(connectionId).Select(item => item.Title).ToHashSet(StringComparer.Ordinal);
        var title = connection.Name;
        for (var suffix = 2; existingTitles.Contains(title); suffix++)
        {
            title = $"{connection.Name} ({suffix})";
        }
        var deferred = new DeferredTerminalChannel();
        var session = new ManagedSshSession(
            guidProvider.NewGuid(),
            connection.Id,
            title,
            connection.Environment,
            connection.ColorOverrideHex,
            deferred);
        var resources = new SessionResources(session, connection, deferred);
        if (!_sessions.TryAdd(session.SessionId, resources))
        {
            throw new InvalidOperationException("The generated session ID already exists.");
        }
        session.Transitioned += OnSessionTransitioned;
        SessionAdded?.Invoke(this, session);
        session.TransitionTo(SessionState.Connecting);
        await ConnectCoreAsync(resources, cancellationToken).ConfigureAwait(false);
        return session;
    }

    public IReadOnlyList<ManagedSshSession> GetForConnection(Guid connectionId)
    {
        return [.. _sessions.Values
            .Select(item => item.Session)
            .Where(item => item.ConnectionId == connectionId && item.State != SessionState.Closed)
            .OrderBy(item => item.SessionId)];
    }

    public async Task RetryAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var resources = GetRequired(sessionId);
        resources.Session.TransitionTo(
            resources.Session.State == SessionState.Disconnected
                ? SessionState.Reconnecting
                : SessionState.Connecting);
        await ConnectCoreAsync(resources, cancellationToken).ConfigureAwait(false);
    }

    public Task CancelAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GetRequired(sessionId).ConnectCancellation.Cancel();
        return Task.CompletedTask;
    }

    public async Task CloseAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var resources))
        {
            return;
        }
        resources.ConnectCancellation.Cancel();
        await resources.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (resources.Session.State != SessionState.Closed)
            {
                resources.Session.TransitionTo(SessionState.Closed);
            }
            await resources.DisposeConnectionAsync().ConfigureAwait(false);
            await resources.Channel.CompleteAsync().ConfigureAwait(false);
        }
        finally
        {
            _ = resources.Gate.Release();
        }
        if (_sessions.TryRemove(sessionId, out _))
        {
            resources.Session.Transitioned -= OnSessionTransitioned;
            SessionRemoved?.Invoke(this, resources.Session);
            resources.Dispose();
        }
    }

    public async Task ShutdownAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        _ = Interlocked.Exchange(ref _shutdown, 1);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(timeout);
        foreach (var session in Sessions)
        {
            try
            {
                await CloseAsync(session.SessionId, bounded.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (bounded.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private async Task ConnectCoreAsync(
        SessionResources resources,
        CancellationToken cancellationToken)
    {
        await resources.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            resources.ResetCancellation(cancellationToken);
            await resources.DisposeConnectionAsync().ConfigureAwait(false);
            var connection = resources.Connection;
            var materials = await authentication.CreateAsync(connection, resources.ConnectCancellation.Token).ConfigureAwait(false);
            var request = new SshConnectRequest
            {
                Host = connection.Host,
                Port = connection.Port,
                Username = connection.Username ?? throw new InvalidOperationException("The SSH username is required."),
                AuthenticationMethods = materials,
                HostKeyPolicy = connection.Ssh.HostKeyPolicy,
                KeepAliveInterval = TimeSpan.FromSeconds(connection.Ssh.KeepAliveSeconds ?? 30),
                OperationTimeout = _defaultOperationTimeout,
            };
            var connected = await transport.ConnectAsync(request, resources.ConnectCancellation.Token).ConfigureAwait(false);
            if (connected.IsFailure)
            {
                Fail(resources, connected.Failure.Message);
                return;
            }
            resources.ConnectionHandle = connected.Value;
            var shell = await connected.Value.OpenShellAsync(new TerminalSpec
            {
                TerminalType = connection.Ssh.TerminalType,
            }, resources.ConnectCancellation.Token).ConfigureAwait(false);
            if (shell.IsFailure)
            {
                Fail(resources, shell.Failure.Message);
                return;
            }
            resources.Shell = shell.Value;
            await resources.Channel.AttachAsync(shell.Value, resources.ConnectCancellation.Token).ConfigureAwait(false);
            shell.Value.Closed += (_, _) => OnChannelClosed(resources);
            await ApplyStartupAsync(connection, shell.Value, resources.ConnectCancellation.Token).ConfigureAwait(false);
            await recent.RecordOpenedAsync(connection.Id, clock.UtcNow, resources.ConnectCancellation.Token).ConfigureAwait(false);
            resources.Session.TransitionTo(SessionState.Connected);
        }
        catch (OperationCanceledException)
        {
            if (resources.Session.State is SessionState.Connecting or SessionState.Reconnecting)
            {
                Fail(resources, "The SSH connection was cancelled.");
            }
        }
        catch (Exception exception)
        {
            Fail(resources, $"The SSH connection could not be opened: {exception.Message}");
        }
        finally
        {
            _ = resources.Gate.Release();
        }
    }

    private static async Task ApplyStartupAsync(
        Connection connection,
        ISshShell shell,
        CancellationToken cancellationToken)
    {
        var commands = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(connection.Ssh.StartupDirectory))
        {
            _ = commands.Append("cd -- '")
                .Append(connection.Ssh.StartupDirectory.Replace("'", "'\\''", StringComparison.Ordinal))
                .AppendLine("'");
        }
        if (!string.IsNullOrWhiteSpace(connection.Ssh.InitialCommand))
        {
            _ = commands.AppendLine(connection.Ssh.InitialCommand);
        }
        if (commands.Length > 0)
        {
            await shell.WriteAsync(Encoding.UTF8.GetBytes(commands.ToString()), cancellationToken).ConfigureAwait(false);
        }
    }

    private static void Fail(SessionResources resources, string message)
    {
        if (resources.Session.State is SessionState.Connecting or SessionState.Reconnecting)
        {
            resources.Channel.ReportMessage(message);
            resources.Session.TransitionTo(SessionState.Failed, message);
        }
    }

    private static void OnChannelClosed(SessionResources resources)
    {
        if (resources.Session.State == SessionState.Connected)
        {
            resources.Session.TransitionTo(SessionState.Disconnected, "The SSH channel closed.");
        }
    }

    private void OnSessionTransitioned(object? sender, SessionTransitionEventArgs e)
    {
        SessionChanged?.Invoke(this, e);
    }

    private SessionResources GetRequired(Guid sessionId)
    {
        return _sessions.TryGetValue(sessionId, out var resources)
            ? resources
            : throw new KeyNotFoundException($"Session '{sessionId}' was not found.");
    }

    private sealed class SessionResources(
        ManagedSshSession session,
        Connection connection,
        DeferredTerminalChannel channel) : IDisposable
    {
        public ManagedSshSession Session { get; } = session;
        public Connection Connection { get; } = connection;
        public DeferredTerminalChannel Channel { get; } = channel;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public CancellationTokenSource ConnectCancellation { get; private set; } = new();
        public ISshConnection? ConnectionHandle { get; set; }
        public ISshShell? Shell { get; set; }

        public void ResetCancellation(CancellationToken cancellationToken)
        {
            ConnectCancellation.Dispose();
            ConnectCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }

        public async Task DisposeConnectionAsync()
        {
            if (Shell is not null)
            {
                await Shell.DisposeAsync().ConfigureAwait(false);
                Shell = null;
            }
            if (ConnectionHandle is not null)
            {
                await ConnectionHandle.DisposeAsync().ConfigureAwait(false);
                ConnectionHandle = null;
            }
        }

        public void Dispose()
        {
            ConnectCancellation.Dispose();
            Gate.Dispose();
        }
    }
}

internal sealed class DeferredTerminalChannel : ITerminalChannel
{
    private readonly Pipe _output = new();
    private readonly TaskCompletionSource<int?> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _sync = new();
    private ISshShell? _inner;
    private CancellationTokenSource _pumpCancellation = new();
    private int _columns = 120;
    private int _rows = 30;
    private int _completed;

    public PipeReader Output => _output.Reader;
    public Task<int?> Exited => _exited.Task;
    public event EventHandler<ChannelClosedEventArgs>? Closed;

    public async Task AttachAsync(ISshShell shell, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(shell);
        int columns;
        int rows;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _completed) != 0, this);
            _pumpCancellation.Cancel();
            _pumpCancellation.Dispose();
            _pumpCancellation = new CancellationTokenSource();
            _inner = shell;
            _ = PumpAsync(shell, _pumpCancellation.Token);
            columns = _columns;
            rows = _rows;
        }
        await shell.ResizeAsync(columns, rows, cancellationToken).ConfigureAwait(false);
    }

    public void ReportMessage(string message)
    {
        _ = ReportMessageAsync(message);
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        ISshShell? inner;
        lock (_sync)
        {
            inner = _inner;
        }
        return inner?.WriteAsync(data, cancellationToken) ?? ValueTask.CompletedTask;
    }

    public ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        ISshShell? inner;
        lock (_sync)
        {
            _columns = columns;
            _rows = rows;
            inner = _inner;
        }
        return inner?.ResizeAsync(columns, rows, cancellationToken) ?? ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await CompleteAsync().ConfigureAwait(false);
    }

    public async Task CompleteAsync()
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
        {
            return;
        }
        _pumpCancellation.Cancel();
        ISshShell? inner;
        lock (_sync)
        {
            inner = _inner;
            _inner = null;
        }
        if (inner is not null)
        {
            await inner.DisposeAsync().ConfigureAwait(false);
        }
        await _output.Writer.CompleteAsync().ConfigureAwait(false);
        _ = _exited.TrySetResult(null);
        Closed?.Invoke(this, new(null, wasKilled: true));
        _pumpCancellation.Dispose();
    }

    private async Task PumpAsync(ISshShell shell, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var result = await shell.Output.ReadAsync(cancellationToken).ConfigureAwait(false);
                foreach (var segment in result.Buffer)
                {
                    _ = await _output.Writer.WriteAsync(segment, cancellationToken).ConfigureAwait(false);
                }
                shell.Output.AdvanceTo(result.Buffer.End);
                if (result.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task ReportMessageAsync(string message)
    {
        _ = await _output.Writer.WriteAsync(
            Encoding.UTF8.GetBytes($"\r\n[RemoteFlow: {message}]\r\n")).ConfigureAwait(false);
    }
}
