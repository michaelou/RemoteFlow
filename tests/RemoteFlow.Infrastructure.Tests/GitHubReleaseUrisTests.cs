using RemoteFlow.Infrastructure.Updates;
using Xunit;

namespace RemoteFlow.Infrastructure.Tests;

/// <summary>These three rules are the only thing standing between a URL that arrived in a JSON response and
/// a process running on the user's machine. The cases pinned here are the ones a human eye reads as
/// github.com and a naive suffix check agrees with.</summary>
public sealed class GitHubReleaseUrisTests
{
    [Theory]
    [InlineData("https://github.com/michaelou/RemoteFlow/releases/tag/v0.3.0")]
    [InlineData("https://github.com/michaelou/RemoteFlow/releases")]
    // A subdomain is fine for a link: nothing is executed, and GitHub serves pages from several.
    [InlineData("https://www.github.com/michaelou/RemoteFlow/releases")]
    public void AReleasePageOnGitHubIsOpened(string url)
    {
        Assert.True(GitHubReleaseUris.IsReleasePage(new Uri(url)));
    }

    [Theory]
    [InlineData("http://github.com/michaelou/RemoteFlow/releases")]
    // The one a suffix check written as EndsWith("github.com") would wave through.
    [InlineData("https://github.com.elsewhere.example/michaelou/RemoteFlow/releases")]
    [InlineData("https://elsewhere.example/michaelou/RemoteFlow/releases")]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    public void APageAnywhereElseFallsBack(string url)
    {
        Assert.False(GitHubReleaseUris.IsReleasePage(new Uri(url)));
    }

    [Fact]
    public void TheAssetPublishedByTheReleaseWorkflowIsAccepted()
    {
        var url = new Uri(
            "https://github.com/michaelou/RemoteFlow/releases/download/v0.3.0/RemoteFlow-0.3.0-win-x64-setup.exe");

        Assert.True(GitHubReleaseUris.IsReleaseAsset(url));
    }

    [Theory]
    // A subdomain is not good enough for something that becomes a running process, even though the page
    // rule above allows it.
    [InlineData("https://www.github.com/michaelou/RemoteFlow/releases/download/v0.3.0/setup.exe")]
    // Userinfo: the host is elsewhere.example, but it reads as github.com left to right.
    [InlineData("https://github.com@elsewhere.example/michaelou/RemoteFlow/releases/download/v0.3.0/setup.exe")]
    // On github.com, but somebody else's repository.
    [InlineData("https://github.com/someone/else/releases/download/v0.3.0/setup.exe")]
    // On this repository, but not a release download.
    [InlineData("https://github.com/michaelou/RemoteFlow/raw/main/setup.exe")]
    [InlineData("http://github.com/michaelou/RemoteFlow/releases/download/v0.3.0/setup.exe")]
    public void AnAssetUrlThatIsNotThisRepositorysReleaseDownloadIsRefused(string url)
    {
        Assert.False(GitHubReleaseUris.IsReleaseAsset(new Uri(url)));
    }

    [Theory]
    [InlineData("https://github.com/michaelou/RemoteFlow/releases/download/v0.3.0/setup.exe")]
    // Where a browser_download_url actually redirects to. Both hosts GitHub has used.
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset/1/2?token=abc")]
    [InlineData("https://release-assets.githubusercontent.com/releases/assets/1?token=abc")]
    public void TheHostsGitHubRedirectsAssetDownloadsThroughAreFollowed(string url)
    {
        Assert.True(GitHubReleaseUris.IsDownloadHop(new Uri(url)));
    }

    [Theory]
    // The leading dot in the suffix rule is what refuses this one.
    [InlineData("https://githubusercontent.com.elsewhere.example/asset")]
    [InlineData("https://elsewhere.example/asset")]
    [InlineData("http://objects.githubusercontent.com/asset")]
    [InlineData("https://objects.githubusercontent.com@elsewhere.example/asset")]
    public void ARedirectAnywhereElseIsNotFollowed(string url)
    {
        Assert.False(GitHubReleaseUris.IsDownloadHop(new Uri(url)));
    }

    [Fact]
    public void ANullUrlIsRefusedByEveryRule()
    {
        Assert.False(GitHubReleaseUris.IsReleasePage(null));
        Assert.False(GitHubReleaseUris.IsReleaseAsset(null));
        Assert.False(GitHubReleaseUris.IsDownloadHop(null));
    }

    [Theory]
    [InlineData("0.3.0", "0.3.0")]
    // MinVer puts the commit hash after a plus, which is not legal in a User-Agent product version.
    [InlineData("0.3.0+3272ddc", "0.3.03272ddc")]
    [InlineData("0.3.0-rc.1", "0.3.0-rc.1")]
    [InlineData("", "0.0.0")]
    [InlineData("+++", "0.0.0")]
    public void TheUserAgentVersionKeepsOnlyWhatAHeaderAllows(string version, string expected)
    {
        Assert.Equal(expected, GitHubReleaseUris.SanitizeUserAgentVersion(version));
    }
}
