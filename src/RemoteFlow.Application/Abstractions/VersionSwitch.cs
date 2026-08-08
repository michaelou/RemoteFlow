namespace RemoteFlow.Application.Abstractions;

/// <summary>The <c>--version</c> startup switch: the one thing the app can be asked from a script without
/// opening a window.</summary>
public static class VersionSwitch
{
    private const string _switch = "--version";

    /// <summary>True when the process was asked for its version and must print it and exit instead of
    /// starting the UI.</summary>
    public static bool IsRequested(IReadOnlyList<string>? arguments)
    {
        if (arguments is null)
        {
            return false;
        }

        for (var index = 0; index < arguments.Count; index++)
        {
            if (string.Equals(arguments[index], _switch, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>One line naming the product, the version, and the commit it was built from. A build with no
    /// source control information says so rather than omitting the field, so the reader can tell the
    /// difference between "no commit recorded" and "this line has no commit in it".</summary>
    public static string Format(IAppVersionInfo version)
    {
        ArgumentNullException.ThrowIfNull(version);
        var commit = string.IsNullOrEmpty(version.CommitSha) ? "unknown" : version.CommitSha;
        return $"RemoteFlow {version.Version} (commit {commit})";
    }
}
