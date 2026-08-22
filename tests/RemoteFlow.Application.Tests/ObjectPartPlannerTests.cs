using RemoteFlow.Application.Abstractions.Sftp;
using RemoteFlow.Application.Abstractions.Storage;
using RemoteFlow.Application.Services;
using Xunit;

namespace RemoteFlow.Application.Tests;

/// <summary>The part ladder is pure arithmetic, which makes it the cheapest place in this feature to be
/// certain: a gap or an overlap here is a corrupted multi-gigabyte object that no checksum downstream would
/// have caught in time.</summary>
public sealed class ObjectPartPlannerTests
{
    private const long _mib = 1024 * 1024;

    [Theory]
    [InlineData(1L)]
    [InlineData(5L * 1024 * 1024)]
    [InlineData(8L * 1024 * 1024)]
    [InlineData((8L * 1024 * 1024) + 1)]
    [InlineData(100L * 1024 * 1024)]
    [InlineData(4L * 1024 * 1024 * 1024)]
    [InlineData(64L * 1024 * 1024 * 1024)]
    [InlineData(500_000_000_000L)]
    [InlineData(5L * 1024 * 1024 * 1024 * 1024)]
    public void EveryPlanIsContiguousAndInsideTheProviderLimits(long totalBytes)
    {
        var limits = ObjectPartLimits.Default;

        var plan = ObjectPartPlanner.Plan(totalBytes, limits);

        Assert.True(plan.IsSuccess);
        var parts = plan.Value.Parts;
        Assert.Equal(totalBytes, parts.Sum(part => part.Length));
        Assert.InRange(parts.Count, 1, limits.MaximumPartCount);
        for (var index = 0; index < parts.Count; index++)
        {
            // Contiguous, with no gap and no overlap: every part starts exactly where the last one ended.
            Assert.Equal(index + 1, parts[index].PartNumber);
            Assert.Equal(index == 0 ? 0 : parts[index - 1].Offset + parts[index - 1].Length, parts[index].Offset);
            Assert.InRange(parts[index].Length, 1, limits.MaximumPartSize);

            // Only the last part is allowed to be short, which is exactly what both providers permit.
            if (index < parts.Count - 1)
            {
                Assert.True(parts[index].Length >= limits.MinimumPartSize);
                Assert.Equal(plan.Value.PartSize, parts[index].Length);
            }
        }
    }

    [Fact]
    public void TheLadderLandsWhereTheArithmeticSaysItShould()
    {
        var fourGibibytes = ObjectPartPlanner.Plan(4L * 1024 * 1024 * 1024, ObjectPartLimits.Default);
        var fiveHundredGigabytes = ObjectPartPlanner.Plan(500_000_000_000L, ObjectPartLimits.Default);

        Assert.Equal(8 * _mib, fourGibibytes.Value.PartSize);
        Assert.Equal(512, fourGibibytes.Value.Parts.Count);
        Assert.Equal(64 * _mib, fiveHundredGigabytes.Value.PartSize);
        Assert.Equal(7_451, fiveHundredGigabytes.Value.Parts.Count);
    }

    [Fact]
    public void SmallObjectsStillGetTheEightMebibyteFloorAndHugeOnesTheGibibyteCeiling()
    {
        // The floor is 8 MiB rather than S3's 5 MiB: headroom against a slightly misreported total.
        Assert.Equal(8 * _mib, ObjectPartPlanner.PartSizeFor(1, ObjectPartLimits.Default));
        Assert.Equal(
            ObjectPartPlanner.PartSizeCeiling,
            ObjectPartPlanner.PartSizeFor(9L * 1024 * 1024 * 1024 * 1024, ObjectPartLimits.Default));
    }

    [Fact]
    public void AnObjectAboveTheProvidersCapIsRejectedBeforeAnyNetworkCall()
    {
        var limits = ObjectPartLimits.Default;
        var beyond = ObjectPartPlanner.MaximumObjectSize(limits) + 1;

        var refused = ObjectPartPlanner.Plan(beyond, limits);

        Assert.True(refused.IsFailure);
        Assert.Equal(SftpError.NotSupported, refused.Failure.Error);
    }

    [Fact]
    public void AnUnknownOrZeroLengthHasNoPlanAtAll()
    {
        var zero = ObjectPartPlanner.Plan(0, ObjectPartLimits.Default);
        var negative = ObjectPartPlanner.Plan(-1, ObjectPartLimits.Default);

        Assert.Equal(SftpError.InvalidPath, zero.Failure.Error);
        Assert.Equal(SftpError.InvalidPath, negative.Failure.Error);
    }

    [Fact]
    public void AProvidersOwnCeilingWinsOverTheLadder()
    {
        // What lets a test drive many small parts out of an object it can hold in memory, and what would
        // keep a provider with a tighter cap than S3 honest.
        var limits = new ObjectPartLimits(1024, 8192, 10_000);

        var plan = ObjectPartPlanner.Plan(100_000, limits);

        Assert.Equal(8192, plan.Value.PartSize);
        Assert.Equal(13, plan.Value.Parts.Count);
        Assert.Equal(100_000 - (12 * 8192), plan.Value.Parts[^1].Length);
    }
}
