namespace RemoteFlow.Application.Abstractions.Sftp;

public enum TransferDirection
{
    Upload = 1,
    Download = 2,
}

public enum TransferConflictDecision
{
    Skip = 1,
    Overwrite = 2,
    Cancel = 3,
}

public enum TransferItemStatus
{
    Completed = 1,
    Skipped = 2,
    Cancelled = 3,
    Failed = 4,
    Conflict = 5,
}

public sealed record TransferConflict(
    TransferDirection Direction,
    string SourcePath,
    string DestinationPath,
    long? ExistingSize);

public interface ITransferConflictResolver
{
    ValueTask<TransferConflictDecision> ResolveAsync(
        TransferConflict conflict,
        CancellationToken cancellationToken = default);
}

public sealed record TransferProgress(
    string SourcePath,
    string DestinationPath,
    long BytesTransferred,
    long TotalBytes,
    double BytesPerSecond,
    TimeSpan? EstimatedRemaining,
    bool IsCompleted);

public sealed record TransferItemResult(
    string SourcePath,
    string DestinationPath,
    TransferItemStatus Status,
    SftpFailure? Failure = null);

public sealed class TransferResult(IReadOnlyList<TransferItemResult> items)
{
    public IReadOnlyList<TransferItemResult> Items { get; } =
        items ?? throw new ArgumentNullException(nameof(items));

    public bool IsSuccess => Items.All(item =>
        item.Status is TransferItemStatus.Completed or TransferItemStatus.Skipped);

    public bool IsCancelled => Items.Any(item => item.Status == TransferItemStatus.Cancelled);
}

public sealed record TransferOptions
{
    public int MaxConcurrentTransfers { get; init; } = 3;

    public int BufferSize { get; init; } = 64 * 1024;
}
