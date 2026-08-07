namespace RemoteFlow.UI.Services;

public enum ConnectionOpenMode
{
    Default = 0,
    Sftp = 1,
    Rdp = 2,
}

public interface IConnectionSessionOpener
{
    Task<bool> OpenAsync(
        Guid connectionId,
        ConnectionOpenMode mode,
        CancellationToken cancellationToken = default);
}

public sealed class DeferredConnectionSessionOpener : IConnectionSessionOpener
{
    public event Func<Guid, ConnectionOpenMode, CancellationToken, Task<bool>>? OpenRequested;

    public async Task<bool> OpenAsync(
        Guid connectionId,
        ConnectionOpenMode mode,
        CancellationToken cancellationToken = default)
    {
        var handler = OpenRequested;
        return handler is not null && await handler(connectionId, mode, cancellationToken).ConfigureAwait(false);
    }
}
