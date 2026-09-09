namespace RemoteFlow.UI.Services;

/// <summary>What startup says about itself while it runs. Startup does a handful of things that can each
/// take a visible moment on a cold disk — a schema migration, unlocking the credential store, sweeping
/// what a previous run left behind — and the splash names whichever one is in progress rather than
/// showing an unmoving logo, so a slow start reads as work rather than as a hang.
///
/// A seam rather than a direct reference to the splash: the startup sequence lives in the composition
/// root and must not know what is drawing, and a host that shows nothing at all still needs somewhere
/// for the messages to go.</summary>
public interface IStartupProgress
{
    /// <summary>Names the step now running, in the present participle and without a trailing ellipsis —
    /// the view adds one. Safe to call from any thread.</summary>
    void Report(string message);
}

/// <summary>The progress sink for a host with nothing to draw on: the previewer, and the tests.</summary>
public sealed class NullStartupProgress : IStartupProgress
{
    public static NullStartupProgress Instance { get; } = new();

    public void Report(string message)
    {
    }
}
