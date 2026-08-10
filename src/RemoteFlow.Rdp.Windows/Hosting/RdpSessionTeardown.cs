using RemoteFlow.Application.Abstractions;

namespace RemoteFlow.Rdp.Windows.Hosting;

internal static class RdpSessionTeardown
{
    /// <summary>Owns the load-bearing shutdown order: stop UI callbacks, request and bounded-wait for
    /// native disconnect, destroy the hosting HWND, then let the session release the control RCW.</summary>
    public static async ValueTask DisposeAsync(
        IEmbeddedRdpSession session,
        Action unsubscribeEvents,
        Action disposeContainer)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(unsubscribeEvents);
        ArgumentNullException.ThrowIfNull(disposeContainer);

        try
        {
            unsubscribeEvents();
        }
        catch (Exception)
        {
            // A stale UI observer cannot be allowed to block native resource cleanup.
        }
        try
        {
            await session.DisconnectAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception)
        {
            // Shutdown continues even if a platform session violates the non-throwing disconnect contract.
        }

        try
        {
            disposeContainer();
        }
        finally
        {
            await session.DisposeAsync().ConfigureAwait(true);
        }
    }
}
