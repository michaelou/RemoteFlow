namespace RemoteFlow.Application.Abstractions;

public sealed record ClipboardReadResult(bool Succeeded, string? Text, string? ErrorMessage)
{
    public static ClipboardReadResult Success(string? text)
    {
        return new ClipboardReadResult(true, text, null);
    }

    public static ClipboardReadResult Failure(string errorMessage)
    {
        return new ClipboardReadResult(false, null, errorMessage);
    }
}

public sealed record ClipboardWriteResult(bool Succeeded, string? ErrorMessage)
{
    public static ClipboardWriteResult Success { get; } = new(true, null);

    public static ClipboardWriteResult Failure(string errorMessage)
    {
        return new ClipboardWriteResult(false, errorMessage);
    }
}

public interface IClipboardService
{
    Task<ClipboardReadResult> ReadTextAsync(CancellationToken cancellationToken = default);

    Task<ClipboardWriteResult> WriteTextAsync(
        string text,
        CancellationToken cancellationToken = default);
}
