using Avalonia.Threading;

namespace RemoteFlow.RdpSpike;

/// <summary>Drives the window through the sequence the ADR's answers are drawn from, so a run is repeatable
/// and the log of one run can be compared with the log of the next. Everything it does is something a
/// person can also do with the buttons; nothing here is behaviour the manual path does not have.</summary>
internal sealed class AutoScript
{
    private readonly List<(TimeSpan At, string Label, Action Step)> _steps = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Action<string> _log;

    private TimeSpan _elapsed = TimeSpan.Zero;
    private int _next;

    public AutoScript(Action<string> log)
    {
        _log = log;
        _timer.Tick += OnTick;
    }

    public AutoScript Then(double seconds, string label, Action step)
    {
        _steps.Add((TimeSpan.FromSeconds(seconds), label, step));
        return this;
    }

    public void Start()
    {
        _log($"auto: {_steps.Count} steps queued");
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _elapsed += _timer.Interval;
        while (_next < _steps.Count && _elapsed >= _steps[_next].At)
        {
            var step = _steps[_next++];
            _log($"auto [{step.At.TotalSeconds:F1}s]: {step.Label}");
            try
            {
                step.Step();
            }
            catch (Exception exception)
            {
                _log($"auto: step '{step.Label}' threw {exception.GetType().Name}: {exception.Message}");
            }
        }

        if (_next >= _steps.Count)
        {
            _timer.Stop();
            _log("auto: finished");
        }
    }
}
