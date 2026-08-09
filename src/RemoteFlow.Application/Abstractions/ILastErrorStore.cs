namespace RemoteFlow.Application.Abstractions;

/// <summary>What the about box shows about the last thing that went wrong. The exception type and message
/// only: a stack trace belongs in the log file, and the log file is redacted, whereas this string is on
/// screen and will end up pasted into an issue.</summary>
public sealed record LastError(DateTimeOffset OccurredAt, string Context, string ExceptionType, string Message);

/// <summary>Remembers the most recent unhandled error so the about box can say what happened and point at
/// the log folder.
///
/// This is the whole of RemoteFlow's crash reporting. Nothing is uploaded, nothing is queued for upload,
/// and no report is assembled anywhere but on the user's own disk — the application has no telemetry and
/// no cloud dependency, and a crash reporter is the usual way that stops being true.</summary>
public interface ILastErrorStore
{
    /// <summary>Raised on whichever thread failed, so a subscriber that touches the UI has to marshal.
    /// Push rather than poll: the about box is a long-lived singleton, and an error that arrives while it
    /// is on screen should appear there.</summary>
    event EventHandler? Changed;

    LastError? Current { get; }

    void Record(Exception exception, string context, DateTimeOffset occurredAt);

    void Clear();
}
