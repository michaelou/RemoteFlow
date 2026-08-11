namespace RemoteFlow.Application.Abstractions;

/// <summary>What a check found. Four outcomes rather than a nullable version, because "there is no
/// release yet" and "the check failed" are different things to say to someone and neither of them means
/// the build is current.</summary>
public enum UpdateCheckOutcome
{
    /// <summary>The newest release is this build, or older than it.</summary>
    UpToDate = 1,

    /// <summary>A newer release exists. <see cref="UpdateCheckResult.LatestVersion"/> names it.</summary>
    UpdateAvailable = 2,

    /// <summary>The project has published no release at all. True of RemoteFlow until the first tag, and
    /// worth distinguishing from a failure: nothing is wrong, there is simply nothing to compare
    /// against.</summary>
    NoReleaseYet = 3,

    /// <summary>The check did not complete — no network, a refused connection, a rate limit, a response
    /// that did not parse. <see cref="UpdateCheckResult.ErrorMessage"/> says which.</summary>
    Failed = 4,
}

/// <summary>The installer for this machine, as named by the newest release, together with the
/// <c>checksums.txt</c> published alongside it.
///
/// Null whenever the release publishes nothing this build can both use and verify: no installer for this
/// architecture, no checksums file, or a URL that is not on the release download path this repository
/// publishes to. An artefact whose hash was never published is one RemoteFlow will not run, so the honest
/// representation of that is no package rather than an unverifiable one — the install button does not
/// appear, and the release page link does.</summary>
public sealed record UpdatePackage(
    string FileName,
    Uri DownloadUrl,
    long SizeInBytes,
    Uri ChecksumsUrl);

/// <summary>The outcome of one check. Failures are reported rather than thrown: not knowing whether a
/// newer version exists is a sentence on a settings page, not an error the application should raise.</summary>
public sealed record UpdateCheckResult(
    UpdateCheckOutcome Outcome,
    string? LatestVersion = null,
    Uri? ReleasePageUrl = null,
    string? ErrorMessage = null,
    UpdatePackage? Package = null)
{
    public static UpdateCheckResult UpToDate(string latestVersion, Uri releasePageUrl)
    {
        return new(UpdateCheckOutcome.UpToDate, latestVersion, releasePageUrl);
    }

    public static UpdateCheckResult UpdateAvailable(
        string latestVersion,
        Uri releasePageUrl,
        UpdatePackage? package = null)
    {
        return new(UpdateCheckOutcome.UpdateAvailable, latestVersion, releasePageUrl, Package: package);
    }

    public static UpdateCheckResult NoReleaseYet()
    {
        return new(UpdateCheckOutcome.NoReleaseYet);
    }

    public static UpdateCheckResult Failed(string errorMessage)
    {
        return new(UpdateCheckOutcome.Failed, ErrorMessage: errorMessage);
    }
}

/// <summary>Asks whether a newer release exists, and nothing else.
///
/// This is the only part of RemoteFlow that makes a network request the user did not configure, so the
/// contract is deliberately narrow: one request, to the project's own release list, returning a version
/// number, a link, and — when the release has one this build could use — the name and address of the
/// installer that would replace it. The check itself downloads nothing and installs nothing; that only
/// happens if the user presses the button the <see cref="UpdateCheckResult.Package"/> makes available, and
/// it is <see cref="IUpdateInstaller"/> that does it. Nothing about the machine is sent beyond what any
/// HTTP request carries. Whether the check runs at all is the user's choice — a press of the button in the
/// about box, or <see cref="SettingKeys.CheckForUpdates"/>, which is off until switched on.</summary>
public interface IUpdateChecker
{
    Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default);
}
