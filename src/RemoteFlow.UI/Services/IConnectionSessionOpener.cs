namespace RemoteFlow.UI.Services;

public enum ConnectionOpenMode
{
    Default = 0,
    Sftp = 1,
    Rdp = 2,
    RdpExternal = 3,
}

/// <summary>Whether the connection opened, and — when it did not — what to tell the person who asked.
/// A bare false leaves them to guess between "no RDP client" and "the host refused".</summary>
public sealed record ConnectionOpenResult(
    bool Opened,
    string? Message = null,
    string? RecoveryActionLabel = null,
    ConnectionOpenMode? RecoveryMode = null)
{
    public static ConnectionOpenResult Success()
    {
        return new(true);
    }

    public static ConnectionOpenResult Failure(string? message = null)
    {
        return new(false, message);
    }

    public static ConnectionOpenResult RecoverableFailure(
        string message,
        string recoveryActionLabel,
        ConnectionOpenMode recoveryMode)
    {
        return new(false, message, recoveryActionLabel, recoveryMode);
    }
}

public interface IConnectionSessionOpener
{
    Task<ConnectionOpenResult> OpenAsync(
        Guid connectionId,
        ConnectionOpenMode mode,
        CancellationToken cancellationToken = default);
}

public sealed class DeferredConnectionSessionOpener : IConnectionSessionOpener
{
    public event Func<Guid, ConnectionOpenMode, CancellationToken, Task<ConnectionOpenResult>>? OpenRequested;

    public async Task<ConnectionOpenResult> OpenAsync(
        Guid connectionId,
        ConnectionOpenMode mode,
        CancellationToken cancellationToken = default)
    {
        var handler = OpenRequested;
        return handler is null
            ? ConnectionOpenResult.Failure()
            : await handler(connectionId, mode, cancellationToken).ConfigureAwait(false);
    }
}
