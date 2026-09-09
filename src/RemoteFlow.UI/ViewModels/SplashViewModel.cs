using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.UI.Services;

namespace RemoteFlow.UI.ViewModels;

/// <summary>What the splash shows while the application starts. The version is on it deliberately: the
/// splash is the one screen everybody sees, and "which build is this" is the first question asked of a
/// start that went wrong.</summary>
public sealed partial class SplashViewModel(IAppVersionInfo? version = null) : ObservableObject, IStartupProgress
{
    // Instance rather than static: Avalonia bindings cannot see statics.
    public string ProductName { get; } = "RemoteFlow";

    public string Tagline { get; } = "Connections, terminals, and transfers in one place.";

    /// <summary>Empty when the build recorded no version, which hides the line rather than showing a
    /// confident <c>0.0.0</c>.</summary>
    public string VersionText { get; } = version is null ? string.Empty : $"Version {version.Version}";

    /// <summary>The step now running. Seeded with something true rather than blank, because on a warm
    /// start the splash can come and go before the first report arrives.</summary>
    [ObservableProperty]
    public partial string StatusMessage { get; private set; } = "Starting";

    public void Report(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        // Startup is awaited on the UI thread, but the steps it calls are other people's code and one of
        // them resuming on a pool thread would otherwise raise a change notification off-thread, which
        // Avalonia turns into a crash rather than a repaint.
        if (Dispatcher.UIThread.CheckAccess())
        {
            StatusMessage = message;
            return;
        }

        Dispatcher.UIThread.Post(() => StatusMessage = message);
    }
}
