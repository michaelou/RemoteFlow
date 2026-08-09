using RemoteFlow.Application.Abstractions;

namespace RemoteFlow.Infrastructure.Diagnostics;

/// <summary>Holds one error: the last one. A history would be a second, unredacted copy of the log file
/// living in memory, and the log file is the thing that is meant to be read.</summary>
public sealed class LastErrorStore : ILastErrorStore
{
    private LastError? _current;

    public event EventHandler? Changed;

    /// <summary>Errors arrive from whichever thread failed and are read on the UI thread, so the field is
    /// published rather than merely assigned.</summary>
    public LastError? Current => Volatile.Read(ref _current);

    public void Record(Exception exception, string context, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentException.ThrowIfNullOrWhiteSpace(context);

        // The innermost exception is the one that says what actually failed; the outer layers usually
        // repeat "one or more errors occurred".
        var root = exception;
        while (root.InnerException is not null)
        {
            root = root.InnerException;
        }

        Volatile.Write(
            ref _current,
            new LastError(occurredAt, context, root.GetType().Name, root.Message));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        Volatile.Write(ref _current, null);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
