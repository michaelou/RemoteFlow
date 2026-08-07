using RemoteFlow.Application.Abstractions;

namespace RemoteFlow.TestSupport;

public sealed class FakeClock(DateTimeOffset utcNow) : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = utcNow.ToUniversalTime();

    public void Advance(TimeSpan duration)
    {
        UtcNow += duration;
    }

    public void Set(DateTimeOffset utcNow)
    {
        UtcNow = utcNow.ToUniversalTime();
    }
}
