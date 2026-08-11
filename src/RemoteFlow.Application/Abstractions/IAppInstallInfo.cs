namespace RemoteFlow.Application.Abstractions;

/// <summary>How this copy of RemoteFlow got onto the machine, which is what decides whether it is allowed
/// to replace itself.</summary>
public enum InstallShape
{
    /// <summary>Put here by the per-user installer, and running from the directory that installer's own
    /// uninstall entry names. The only shape that can be updated in place.</summary>
    Installer = 1,

    /// <summary>An extracted portable zip, or an install that has since been moved or copied. Nothing on
    /// the machine records where it lives, so replacing it is not RemoteFlow's to do — a new zip, extracted
    /// where the user chose, is.</summary>
    Portable = 2,

    /// <summary>A build output directory, or a platform RemoteFlow publishes no installer for.</summary>
    Development = 3,
}

/// <summary>Where this build lives and how it got there. The counterpart to <see cref="IAppVersionInfo"/>:
/// the version says what is running, this says whether anything could replace it.</summary>
public interface IAppInstallInfo
{
    InstallShape Shape { get; }

    /// <summary>The directory the running executable is in.</summary>
    string InstallDirectory { get; }

    /// <summary>One sentence saying why this copy cannot update itself, or null when <see cref="Shape"/> is
    /// <see cref="InstallShape.Installer"/>. It is a sentence rather than a log line because the answer to
    /// "why is there no button" belongs where the button would have been.</summary>
    string? Explanation { get; }
}
