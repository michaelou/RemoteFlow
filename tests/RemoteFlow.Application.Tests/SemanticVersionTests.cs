using RemoteFlow.Application.Services;
using Xunit;

namespace RemoteFlow.Application.Tests;

/// <summary>The update check is one comparison, and the wrong answer is worse than no answer: telling
/// someone their current build is out of date sends them to reinstall what they already have, and telling
/// someone on 0.9.0 that 0.10.0 is older leaves them there. Both are string-comparison bugs, so the cases
/// that produce them are the ones pinned here.</summary>
public sealed class SemanticVersionTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3, "")]
    [InlineData("v1.2.3", 1, 2, 3, "")]
    [InlineData("V0.1.0", 0, 1, 0, "")]
    [InlineData("0.1.0-rc.1", 0, 1, 0, "rc.1")]
    // The build metadata is where RemoteFlow's own informational version keeps the commit hash.
    [InlineData("0.1.0+3272ddc", 0, 1, 0, "")]
    [InlineData("0.0.0-alpha.0.57+abc1234", 0, 0, 0, "alpha.0.57")]
    public void TheVersionsRemoteFlowActuallyProducesAllParse(
        string text,
        int major,
        int minor,
        int patch,
        string prerelease)
    {
        Assert.True(SemanticVersion.TryParse(text, out var version));
        Assert.Equal(major, version.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(patch, version.Patch);
        Assert.Equal(prerelease, version.Prerelease);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("nightly")]
    [InlineData("1.2.x")]
    [InlineData("1.-2.3")]
    [InlineData("1.2.3-")]
    public void SomethingThatIsNotAVersionIsRefusedRatherThanGuessedAt(string? text)
    {
        Assert.False(SemanticVersion.TryParse(text, out _));
    }

    // The case a string comparison gets backwards, and the reason this type exists.
    [Fact]
    public void TenIsNewerThanNineEvenThoughItSortsEarlierAsText()
    {
        Assert.True(Parse("0.10.0") > Parse("0.9.0"));
        Assert.True(Parse("1.0.10") > Parse("1.0.9"));
    }

    [Theory]
    [InlineData("1.0.0", "0.9.9")]
    [InlineData("0.2.0", "0.1.9")]
    [InlineData("0.1.1", "0.1.0")]
    public void TheCoreIsComparedNumericallyFromTheLeft(string newer, string older)
    {
        Assert.True(Parse(newer) > Parse(older));
        Assert.True(Parse(older) < Parse(newer));
    }

    // A release candidate precedes the release it is a candidate for. Getting this backwards would offer
    // 0.1.0-rc.1 as an update to someone already running 0.1.0.
    [Fact]
    public void APrereleaseRanksBelowTheReleaseItPrecedes()
    {
        Assert.True(Parse("0.1.0") > Parse("0.1.0-rc.1"));
        Assert.True(Parse("0.1.0-rc.1") < Parse("0.1.0"));
        Assert.True(Parse("0.1.0-rc.1") > Parse("0.0.9"));
    }

    [Theory]
    [InlineData("1.0.0-rc.2", "1.0.0-rc.1")]
    [InlineData("1.0.0-rc.10", "1.0.0-rc.2")]
    [InlineData("1.0.0-beta", "1.0.0-alpha")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha")]
    // A numeric identifier ranks below an alphanumeric one, which is SemVer's rule and not intuition's.
    [InlineData("1.0.0-alpha", "1.0.0-1")]
    public void PrereleaseIdentifiersFollowSemVerPrecedence(string newer, string older)
    {
        Assert.True(Parse(newer) > Parse(older));
    }

    [Fact]
    public void BuildMetadataCarriesNoPrecedence()
    {
        Assert.Equal(0, Parse("0.1.0+aaaaaaa").CompareTo(Parse("0.1.0+bbbbbbb")));
        Assert.Equal(Parse("0.1.0"), Parse("0.1.0+aaaaaaa"));
    }

    [Fact]
    public void TheSameVersionIsNeitherNewerNorOlder()
    {
        Assert.Equal(0, Parse("0.1.0").CompareTo(Parse("v0.1.0")));
        Assert.False(Parse("0.1.0") > Parse("0.1.0"));
        Assert.False(Parse("0.1.0") < Parse("0.1.0"));
    }

    [Theory]
    [InlineData("0.1.0", "0.1.0")]
    [InlineData("v0.1.0", "0.1.0")]
    [InlineData("0.1.0-rc.1+abc1234", "0.1.0-rc.1")]
    public void ToStringDropsTheTagPrefixAndTheMetadataSoTheVersionOnScreenIsTheVersion(
        string text,
        string expected)
    {
        Assert.Equal(expected, Parse(text).ToString());
    }

    private static SemanticVersion Parse(string text)
    {
        Assert.True(SemanticVersion.TryParse(text, out var version));
        return version;
    }
}
