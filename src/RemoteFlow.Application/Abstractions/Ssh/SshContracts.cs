using RemoteFlow.Domain.Enums;
using RemoteFlow.Application.Abstractions.Sftp;

namespace RemoteFlow.Application.Abstractions.Ssh;

public enum SshError
{
    DnsFailure = 1,
    ConnectionRefused = 2,
    Timeout = 3,
    AuthFailed = 4,
    HostKeyUnknown = 5,
    HostKeyMismatch = 6,
    HostKeyRevoked = 7,
    ChannelClosed = 8,
    NetworkChanged = 9,
    Cancelled = 10,
}

public sealed record SshFailure(SshError Error, string Message);

public sealed class SshResult<T>
{
    private SshResult(T value)
    {
        SuccessfulValue = value;
        IsSuccess = true;
    }

    private SshResult(SshFailure failure)
    {
        FailureValue = failure;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public T Value => IsSuccess
        ? SuccessfulValue!
        : throw new InvalidOperationException("A failed SSH result has no value.");

    public SshFailure Failure => IsFailure
        ? FailureValue!
        : throw new InvalidOperationException("A successful SSH result has no failure.");

    private T? SuccessfulValue { get; }

    private SshFailure? FailureValue { get; }

#pragma warning disable CA1000 // Result factories are intentionally discoverable on the result type.
    public static SshResult<T> Success(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(value);
    }

    public static SshResult<T> Fail(SshError error, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return new(new SshFailure(error, message));
    }
#pragma warning restore CA1000
}

public abstract class SshAuthMaterial
{
    private SshAuthMaterial() { }

    public sealed class None : SshAuthMaterial;

    public sealed class Password(string value) : SshAuthMaterial
    {
        public string Value { get; } = value;

        public override string ToString()
        {
            return "Password { Value = [REDACTED] }";
        }
    }

    public sealed class PrivateKey(string keyData, string? passphrase = null) : SshAuthMaterial
    {
        public string KeyData { get; } = keyData;

        public string? Passphrase { get; } = passphrase;

        public override string ToString()
        {
            return "PrivateKey { KeyData = [REDACTED], Passphrase = [REDACTED] }";
        }
    }

    public sealed class Agent : SshAuthMaterial;

    public sealed class KeyboardInteractive(
        Func<IReadOnlyList<SshAuthenticationPrompt>, CancellationToken, ValueTask<IReadOnlyList<string>>> respondAsync)
        : SshAuthMaterial
    {
        public Func<IReadOnlyList<SshAuthenticationPrompt>, CancellationToken, ValueTask<IReadOnlyList<string>>> RespondAsync
        { get; } = respondAsync;
    }
}

public sealed record SshAuthenticationPrompt(string Prompt, bool IsSecret);

public interface IKeyboardInteractivePrompt
{
    ValueTask<IReadOnlyList<string>> RespondAsync(
        IReadOnlyList<SshAuthenticationPrompt> prompts,
        CancellationToken cancellationToken = default);
}

public sealed record SshConnectRequest
{
    public required string Host { get; init; }

    public int Port { get; init; } = 22;

    public required string Username { get; init; }

    public SshAuthMaterial Authentication { get; init; } = new SshAuthMaterial.None();

    public IReadOnlyList<SshAuthMaterial> AuthenticationMethods { get; init; } = [];

    public int MaxAuthenticationAttempts { get; init; } = 3;

    public HostKeyPolicy HostKeyPolicy { get; init; } = HostKeyPolicy.Strict;

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(30);
}

public sealed record TerminalSpec
{
    public string TerminalType { get; init; } = "xterm-256color";

    public int Columns { get; init; } = 120;

    public int Rows { get; init; } = 30;
}

public sealed record HostKeyInfo(
    string Algorithm,
    byte[] PublicKey,
    string Sha256Fingerprint);

public sealed record SshExecResult(int ExitCode, string StandardOutput, string StandardError);

public enum SshOutputKind
{
    StandardOutput = 0,
    StandardError = 1,
}

public readonly record struct SshOutputLine(SshOutputKind Kind, string Text);

/// <summary>A command whose output is read while it is still running — <c>docker logs -f</c>, <c>tail -f</c>
/// — rather than collected once it has finished, which is all <see cref="ISshConnection.ExecuteAsync"/>
/// offers. No operation timeout applies once the channel is open: the command runs until it exits, the
/// connection drops, or the stream is disposed. Disposing closes the channel, which is what stops a
/// follow-mode command on the server.</summary>
public interface ISshRunningCommand : IAsyncDisposable
{
    /// <summary>Standard output and standard error interleaved in the order they arrived, one line at a
    /// time. The enumeration ends when the command exits or the channel closes; it can be enumerated
    /// once.</summary>
    IAsyncEnumerable<SshOutputLine> ReadLinesAsync(CancellationToken cancellationToken = default);

    /// <summary>The command's exit code, once the enumeration has ended because the command exited.
    /// Null while it is running and when the channel closed without reporting one.</summary>
    int? ExitCode { get; }

    /// <summary>Why the enumeration ended early, when it did. A command that exits — whatever its exit
    /// code — is not a failure; a channel that broke underneath it is.</summary>
    SshFailure? Failure { get; }
}

public sealed class SshDisconnectedEventArgs(SshError? error, string? message) : EventArgs
{
    public SshError? Error { get; } = error;

    public string? Message { get; } = message;
}

public interface ISshShell : ITerminalChannel;

public interface ISshConnection : IAsyncDisposable
{
    Task<SshResult<ISshShell>> OpenShellAsync(
        TerminalSpec terminal,
        CancellationToken cancellationToken = default);

    Task<SshResult<SshExecResult>> ExecuteAsync(
        string command,
        CancellationToken cancellationToken = default);

    /// <summary>Starts a command and hands back its output as a stream. The operation timeout bounds
    /// opening the channel only, not how long the command may run.</summary>
    Task<SshResult<ISshRunningCommand>> StartCommandAsync(
        string command,
        CancellationToken cancellationToken = default);

    ISftpService OpenSftp();

    event EventHandler<SshDisconnectedEventArgs>? Disconnected;
}

public interface ISshTransport
{
    Task<SshResult<ISshConnection>> ConnectAsync(
        SshConnectRequest request,
        CancellationToken cancellationToken = default);
}
