using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace RemoteFlow.UI.Services;

/// <summary>Asks the application to close, for the one caller that needs to: an update cannot replace the
/// files of a process that is still running them.</summary>
public interface IApplicationShutdown
{
    /// <summary>Asks the desktop lifetime to close. Returns false when there is no desktop lifetime to ask,
    /// which is every headless test.
    ///
    /// True means the request was made, not that the application is leaving. The main window asks about
    /// open terminals first and the answer may be no, so a caller must not treat this as a promise —
    /// whatever it queued for shutdown will simply run at the next one instead.</summary>
    bool Request();
}

public sealed class ApplicationShutdownService : IApplicationShutdown
{
    public bool Request()
    {
        if (global::Avalonia.Application.Current?.ApplicationLifetime
            is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return false;
        }

        // Posted rather than called. Shutting down unwinds the message loop this is running on, and the
        // command that asked for it has not returned yet.
        Dispatcher.UIThread.Post(() => _ = desktop.TryShutdown());
        return true;
    }
}
