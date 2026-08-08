using RemoteFlow.Infrastructure.Diagnostics;
using Xunit;

namespace RemoteFlow.Infrastructure.Tests;

public sealed class LastErrorStoreTests
{
    private static readonly DateTimeOffset _when = new(2026, 8, 9, 14, 30, 0, TimeSpan.Zero);

    [Fact]
    public void NothingIsRecordedUntilSomethingFails()
    {
        Assert.Null(new LastErrorStore().Current);
    }

    // "One or more errors occurred" is what the outer exception almost always says, and it tells the
    // person reading the about box nothing at all.
    [Fact]
    public void TheInnermostExceptionIsWhatGetsShown()
    {
        var store = new LastErrorStore();

        store.Record(
            new InvalidOperationException(
                "one or more errors occurred",
                new IOException("the log directory is read-only")),
            "application startup",
            _when);

        var error = store.Current;
        Assert.NotNull(error);
        Assert.Equal("IOException", error.ExceptionType);
        Assert.Equal("the log directory is read-only", error.Message);
        Assert.Equal("application startup", error.Context);
        Assert.Equal(_when, error.OccurredAt);
    }

    [Fact]
    public void OnlyTheLatestFailureIsKept()
    {
        var store = new LastErrorStore();

        store.Record(new IOException("first"), "a", _when);
        store.Record(new IOException("second"), "b", _when.AddMinutes(1));

        Assert.Equal("second", store.Current?.Message);
    }

    [Fact]
    public void RecordingAndClearingBothAnnounceThemselves()
    {
        var store = new LastErrorStore();
        var notifications = 0;
        store.Changed += (_, _) => notifications++;

        store.Record(new IOException("boom"), "a background task", _when);
        store.Clear();

        Assert.Equal(2, notifications);
        Assert.Null(store.Current);
    }
}
