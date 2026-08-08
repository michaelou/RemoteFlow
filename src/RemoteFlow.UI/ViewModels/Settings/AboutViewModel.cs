using RemoteFlow.Application.Abstractions;

namespace RemoteFlow.UI.ViewModels.Settings;

/// <summary>What the about box shows: which build this is, and which commit it came from. Both are read
/// once at construction — a running process cannot change either.</summary>
public sealed class AboutViewModel
{
    private const string _unknownCommit = "unknown";

    public AboutViewModel(IAppVersionInfo version)
    {
        ArgumentNullException.ThrowIfNull(version);
        Version = version.Version;
        Commit = string.IsNullOrEmpty(version.CommitSha) ? _unknownCommit : version.CommitSha;
    }

    // Instance rather than static: the about box binds to it, and Avalonia bindings cannot see statics.
    public string ProductName { get; } = "RemoteFlow";

    /// <summary>The SemVer version, for example <c>0.1.0</c> or <c>0.0.0-alpha.0.57</c>.</summary>
    public string Version { get; }

    /// <summary>The full commit hash, or <c>unknown</c> when the build recorded none. The full hash rather
    /// than a short prefix, because this is the line people paste into a bug report.</summary>
    public string Commit { get; }
}
