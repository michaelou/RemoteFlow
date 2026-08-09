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

/// <summary>The outcome of one check. Failures are reported rather than thrown: not knowing whether a
/// newer version exists is a sentence on a settings page, not an error the application should raise.</summary>
public sealed record UpdateCheckResult(
    UpdateCheckOutcome Outcome,
    string? LatestVersion = null,
    Uri? ReleasePageUrl = null,
    string? ErrorMessage = null)
{
    public static UpdateCheckResult UpToDate(string latestVersion, Uri releasePageUrl)
    {
        return new(UpdateCheckOutcome.UpToDate, latestVersion, releasePageUrl);
    }

    public static UpdateCheckResult UpdateAvailable(string latestVersion, Uri releasePageUrl)
    {
        return new(UpdateCheckOutcome.UpdateAvailable, latestVersion, releasePageUrl);
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
/// number and a link. It downloads nothing, installs nothing, and sends nothing about the machine it runs
/// on beyond what any HTTP request carries. Whether it runs at all is the user's choice — a press of the
/// button in the about box, or <see cref="SettingKeys.CheckForUpdates"/>, which is off until switched
/// on.</summary>
public interface IUpdateChecker
{
    Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default);
}
