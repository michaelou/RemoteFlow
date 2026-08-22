using Microsoft.Extensions.Time.Testing;
using RemoteFlow.Application.Services;
using Xunit;

namespace RemoteFlow.Application.Tests;

public sealed class TransferRateMeterTests
{
    [Fact]
    public void TheWindowedRateFollowsAHalvedLinkAndACumulativeAverageDoesNot()
    {
        var clock = new FakeTimeProvider();
        var meter = new TransferRateMeter(clock, TimeSpan.FromSeconds(5));
        var transferred = 0L;

        // Ten seconds at 100 MB/s, then ten seconds at 50 MB/s.
        for (var second = 0; second < 10; second++)
        {
            transferred += 100_000_000;
            clock.Advance(TimeSpan.FromSeconds(1));
            meter.Record(transferred);
        }

        for (var second = 0; second < 10; second++)
        {
            transferred += 50_000_000;
            clock.Advance(TimeSpan.FromSeconds(1));
            meter.Record(transferred);
        }

        var cumulative = transferred / 20.0;

        // The whole reason the rate became windowed: the cumulative average says 75 MB/s and an estimate
        // hours short, exactly when the user is deciding whether to leave a long transfer running.
        Assert.InRange(meter.BytesPerSecond, 49_000_000, 51_000_000);
        Assert.InRange(cumulative, 74_000_000, 76_000_000);
        Assert.True(Math.Abs(meter.BytesPerSecond - 50_000_000) < Math.Abs(cumulative - 50_000_000));
    }

    [Fact]
    public void ZeroElapsedDoesNotDivideByZero()
    {
        var clock = new FakeTimeProvider();
        var meter = new TransferRateMeter(clock);

        meter.Record(0);
        meter.Record(1_000_000);

        Assert.Equal(0, meter.BytesPerSecond);
        Assert.False(double.IsNaN(meter.BytesPerSecond));
    }

    [Fact]
    public void AZeroRateGivesNoEstimateAndCompletionGivesZero()
    {
        var clock = new FakeTimeProvider();
        var meter = new TransferRateMeter(clock);

        Assert.Null(meter.EstimateRemaining(0, 1_000));
        Assert.Equal(TimeSpan.Zero, meter.EstimateRemaining(1_000, 1_000));
        Assert.Equal(TimeSpan.Zero, meter.EstimateRemaining(2_000, 1_000));
    }

    [Fact]
    public void TheEstimateFallsOutOfTheWindowedRate()
    {
        var clock = new FakeTimeProvider();
        var meter = new TransferRateMeter(clock, TimeSpan.FromSeconds(5));
        meter.Record(0);
        clock.Advance(TimeSpan.FromSeconds(1));
        meter.Record(1_000);

        var remaining = meter.EstimateRemaining(1_000, 3_000);

        Assert.Equal(TimeSpan.FromSeconds(2), remaining);
    }
}
