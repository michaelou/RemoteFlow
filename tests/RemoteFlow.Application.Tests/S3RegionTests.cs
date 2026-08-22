using RemoteFlow.Application.Abstractions.Storage;
using Xunit;

namespace RemoteFlow.Application.Tests;

/// <summary>The region list is hand-maintained reference data, so what is worth pinning is its shape and
/// the near-miss suggestion that turns a DNS failure into a one-click fix.</summary>
public sealed class S3RegionTests
{
    [Fact]
    public void EveryRegionHasACodeAndAName()
    {
        Assert.NotEmpty(S3Regions.All);
        Assert.Equal(
            S3Regions.All.Count,
            S3Regions.All.Select(region => region.Code).Distinct(StringComparer.Ordinal).Count());
        foreach (var region in S3Regions.All)
        {
            Assert.Matches("^[a-z]{2}(-[a-z]+)+-[0-9]$", region.Code);
            Assert.False(string.IsNullOrWhiteSpace(region.DisplayName));
            Assert.StartsWith(region.Code, region.Label, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("eu-west-1", true)]
    [InlineData("EU-WEST-1", true)]
    [InlineData(" eu-west-1 ", true)]
    [InlineData("eu-west", false)]
    [InlineData("auto", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyARealRegionCodeIsKnown(string? code, bool expected)
    {
        Assert.Equal(expected, S3Regions.IsKnown(code));
    }

    [Fact]
    public void AHalfTypedRegionSuggestsTheOnesItCouldHaveBeen()
    {
        // The reported mistake: 'eu-west' looks like a region, is not one, and the only thing that says so
        // is "no such host s3.eu-west.amazonaws.com" at connect time.
        Assert.Equal(["eu-west-1", "eu-west-2", "eu-west-3"], S3Regions.Suggest("eu-west"));
        Assert.Equal(["us-east-1", "us-east-2"], S3Regions.Suggest("us-east"));
        Assert.Empty(S3Regions.Suggest("nonsense"));
        Assert.Empty(S3Regions.Suggest(null));
    }
}
