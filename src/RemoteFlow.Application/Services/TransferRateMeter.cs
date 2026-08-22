namespace RemoteFlow.Application.Services;

/// <summary>A windowed transfer rate over <c>(timestamp, cumulative bytes)</c> samples.
///
/// <c>transferred / elapsed</c> is not merely imprecise over hours but actively misleading: a
/// five-hundred-gigabyte transfer that runs at 100 MB/s and then drops to 5 MB/s reports about 52 MB/s and
/// an estimate hours short — exactly when the user is deciding whether to leave it running. A five-second
/// window converges on the rate the link is actually giving.</summary>
public sealed class TransferRateMeter
{
    public static TimeSpan DefaultWindow { get; } = TimeSpan.FromSeconds(5);

    private const int _capacity = 64;

    private readonly TimeProvider _time;
    private readonly TimeSpan _window;
    private readonly long[] _timestamps = new long[_capacity];
    private readonly long[] _bytes = new long[_capacity];
    private readonly Lock _sync = new();
    private int _start;
    private int _count;

    public TransferRateMeter(TimeProvider? timeProvider = null, TimeSpan? window = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _window = window ?? DefaultWindow;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_window, TimeSpan.Zero);
    }

    /// <summary>Bytes per second over the window, or zero when there is not yet enough to say.</summary>
    public double BytesPerSecond
    {
        get
        {
            lock (_sync)
            {
                if (_count < 2)
                {
                    return 0;
                }

                var oldest = _start;
                var newest = (_start + _count - 1) % _capacity;
                var elapsed = _time.GetElapsedTime(_timestamps[oldest], _timestamps[newest]);

                // Zero elapsed is the ordinary case on a fast local run, not an error: two samples inside
                // one tick of the clock say nothing about the rate, and dividing by it would say NaN.
                return elapsed <= TimeSpan.Zero
                    ? 0
                    : (_bytes[newest] - _bytes[oldest]) / elapsed.TotalSeconds;
            }
        }
    }

    /// <summary>Records a cumulative byte count. Samples that fall out of the window are dropped, so the
    /// rate follows the link rather than the whole history.</summary>
    public void Record(long cumulativeBytes)
    {
        lock (_sync)
        {
            var now = _time.GetTimestamp();
            if (_count == _capacity)
            {
                _start = (_start + 1) % _capacity;
                _count--;
            }

            var slot = (_start + _count) % _capacity;
            _timestamps[slot] = now;
            _bytes[slot] = cumulativeBytes;
            _count++;

            // Always keep two samples, so a long stall still has something to divide.
            while (_count > 2 && _time.GetElapsedTime(_timestamps[_start], now) > _window)
            {
                _start = (_start + 1) % _capacity;
                _count--;
            }
        }
    }

    /// <summary>The estimate the progress bar shows. Completion is <see cref="TimeSpan.Zero"/> rather than
    /// null, and a zero rate is null rather than infinity.</summary>
    public TimeSpan? EstimateRemaining(long transferred, long totalBytes)
    {
        if (totalBytes >= 0 && transferred >= totalBytes)
        {
            return TimeSpan.Zero;
        }

        var rate = BytesPerSecond;
        return rate <= 0 ? null : TimeSpan.FromSeconds((totalBytes - transferred) / rate);
    }
}
