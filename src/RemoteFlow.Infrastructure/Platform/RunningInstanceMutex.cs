namespace RemoteFlow.Infrastructure.Platform;

/// <summary>A named mutex whose only job is to exist while RemoteFlow does.
///
/// <c>AppMutex</c> in <c>build/windows/RemoteFlow.iss</c> names this mutex. While it exists, the installer
/// and the uninstaller stop and ask the user to close RemoteFlow rather than replacing or deleting files
/// underneath a running copy. That matters most for the uninstaller, which had no such check before, and it
/// fires earlier than <c>CloseApplications</c> can — before Setup has touched a file, rather than at the
/// point where it is already preparing to.
///
/// Session-local rather than <c>Global\</c>: the install is per-user, so another account's running copy is
/// not this install's business, and creating an object in the global namespace needs a privilege a standard
/// user is not guaranteed to hold.</summary>
public static class RunningInstanceMutex
{
    /// <summary>Must match <c>AppMutex</c> in <c>build/windows/RemoteFlow.iss</c> exactly. Windows compares
    /// mutex names case-sensitively, so a difference in case is a difference in name, and a mismatch fails
    /// silently in the unhelpful direction: the installer would conclude nothing is running.</summary>
    public const string Name = "RemoteFlow-6A084A9C-3CFB-4C8F-A7A8-AA5B34D9C91F";

    // Static, so the collector cannot finalise the handle out from under a running application. A local
    // Mutex that falls out of scope is collected, its handle closes, and the installer sees no running
    // instance -- the exact failure this class exists to prevent.
    private static Mutex? _handle;

    /// <summary>Creates the mutex, or opens the one another instance already made. Never blocks and never
    /// takes ownership: this is a flag for another process to look at, not a lock.</summary>
    public static void Acquire()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        _handle ??= new Mutex(initiallyOwned: false, Name);
    }

    /// <summary>Drops this process's handle. Called immediately before the installer is started, so that by
    /// the time Setup asks whether RemoteFlow is running, this process's answer is already no.</summary>
    public static void Release()
    {
        // Dispose, never ReleaseMutex: the latter throws when the calling thread does not own the mutex,
        // and nothing here ever owns it.
        _handle?.Dispose();
        _handle = null;
    }
}
