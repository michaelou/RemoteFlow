using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Sftp;
using RemoteFlow.UI.Services;

namespace RemoteFlow.UI.ViewModels.Transfers;

public enum ManagedTransferStatus
{
    Queued = 1,
    Active = 2,
    Completed = 3,
    Failed = 4,
    Cancelled = 5,
}

public sealed record TransferQueueRequest(
    TransferDirection Direction,
    string SourcePath,
    string DestinationPath,
    Func<IProgress<TransferProgress>, CancellationToken, Task<TransferResult>> Operation);

public sealed partial class TransferItemViewModel : ObservableObject, IDisposable
{
    private readonly TransfersPageViewModel _owner;
    private CancellationTokenSource _cancellation = new();
    private CancellationTokenRegistration _externalCancellation;
    private TaskCompletionSource<TransferResult> _completion = NewCompletion();

    internal TransferItemViewModel(TransfersPageViewModel owner, TransferQueueRequest request)
    {
        _owner = owner;
        Request = request;
        Id = Guid.NewGuid();
        Direction = request.Direction;
        SourcePath = request.SourcePath;
        DestinationPath = request.DestinationPath;
    }

    public Guid Id { get; }

    public TransferDirection Direction { get; }

    public string SourcePath { get; }

    public string DestinationPath { get; }

    public string Name => Path.GetFileName((Direction == TransferDirection.Download
        ? SourcePath
        : DestinationPath).TrimEnd('/', '\\'));

    public string DirectionText => Direction == TransferDirection.Download ? "Download" : "Upload";

    public string StatusText => Status switch
    {
        ManagedTransferStatus.Queued => "Queued",
        ManagedTransferStatus.Active => "Transferring",
        ManagedTransferStatus.Completed => "Completed",
        ManagedTransferStatus.Failed => "Failed",
        ManagedTransferStatus.Cancelled => "Cancelled",
        _ => Status.ToString(),
    };

    public string ProgressText => TotalBytes <= 0
        ? FormatBytes(BytesTransferred)
        : $"{FormatBytes(BytesTransferred)} of {FormatBytes(TotalBytes)}";

    public string RateText => BytesPerSecond <= 0 ? "—" : $"{FormatBytes((long)BytesPerSecond)}/s";

    public string EtaText => EstimatedRemaining is null
        ? "—"
        : EstimatedRemaining.Value <= TimeSpan.Zero
            ? "Done"
            : EstimatedRemaining.Value.ToString(@"hh\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture);

    public double ProgressPercent => TotalBytes <= 0
        ? 0
        : Math.Clamp(BytesTransferred * 100d / TotalBytes, 0, 100);

    public bool CanCancel => Status is ManagedTransferStatus.Queued or ManagedTransferStatus.Active;

    public bool CanRetry => Status is ManagedTransferStatus.Failed or ManagedTransferStatus.Cancelled;

    public bool CanReveal => Status == ManagedTransferStatus.Completed &&
        Direction == TransferDirection.Download;

    public Task<TransferResult> Completion => _completion.Task;

    public int ProgressUpdateCount { get; private set; }

    internal TransferQueueRequest Request { get; }

    internal CancellationToken CancellationToken => _cancellation.Token;

    [ObservableProperty]
    public partial ManagedTransferStatus Status { get; internal set; } = ManagedTransferStatus.Queued;

    [ObservableProperty]
    public partial long BytesTransferred { get; private set; }

    [ObservableProperty]
    public partial long TotalBytes { get; private set; }

    [ObservableProperty]
    public partial double BytesPerSecond { get; private set; }

    [ObservableProperty]
    public partial TimeSpan? EstimatedRemaining { get; private set; }

    [ObservableProperty]
    public partial string? FailureReason { get; internal set; }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _cancellation.Cancel();
    }

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private Task RetryAsync()
    {
        return _owner.RetryAsync(this);
    }

    [RelayCommand(CanExecute = nameof(CanReveal))]
    private Task RevealAsync()
    {
        return _owner.RevealAsync(this);
    }

    internal void ResetForRetry()
    {
        _cancellation.Dispose();
        _cancellation = new CancellationTokenSource();
        _completion = NewCompletion();
        FailureReason = null;
        BytesTransferred = 0;
        TotalBytes = 0;
        BytesPerSecond = 0;
        EstimatedRemaining = null;
        NotifyComputedProperties();
    }

    internal void ApplyProgress(TransferProgress progress)
    {
        BytesTransferred = progress.BytesTransferred;
        TotalBytes = progress.TotalBytes;
        BytesPerSecond = progress.BytesPerSecond;
        EstimatedRemaining = progress.EstimatedRemaining;
        ProgressUpdateCount++;
        NotifyComputedProperties();
    }

    internal void Complete(TransferResult result)
    {
        _ = _completion.TrySetResult(result);
    }

    internal void NotifyStatusChanged()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(CanReveal));
        CancelCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
        RevealCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        _externalCancellation.Dispose();
        _cancellation.Dispose();
        GC.SuppressFinalize(this);
    }

    internal void SetExternalCancellation(CancellationTokenRegistration registration)
    {
        _externalCancellation = registration;
    }

    private void NotifyComputedProperties()
    {
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(RateText));
        OnPropertyChanged(nameof(EtaText));
        OnPropertyChanged(nameof(ProgressPercent));
    }

    private static TaskCompletionSource<TransferResult> NewCompletion()
    {
        return new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }
}

public sealed partial class TransfersPageViewModel : PageViewModel, IDisposable
{
    private static readonly TimeSpan _progressInterval = TimeSpan.FromMilliseconds(100);
    private readonly IUiDispatcher _dispatcher;
    private readonly IFileRevealService _reveal;
    private readonly SemaphoreSlim _concurrency;
    private readonly Lock _stateLock = new();
    private int _disposed;

    public TransfersPageViewModel(
        IUiDispatcher dispatcher,
        IFileRevealService reveal,
        int maxConcurrentTransfers = 3) : base("Transfers")
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrentTransfers, 1);
        _dispatcher = dispatcher;
        _reveal = reveal;
        _concurrency = new SemaphoreSlim(maxConcurrentTransfers, maxConcurrentTransfers);
    }

    public ObservableCollection<TransferItemViewModel> Items { get; } = [];

    public ObservableCollection<TransferItemViewModel> QueuedItems { get; } = [];

    public ObservableCollection<TransferItemViewModel> ActiveItems { get; } = [];

    public ObservableCollection<TransferItemViewModel> CompletedItems { get; } = [];

    public ObservableCollection<TransferItemViewModel> FailedItems { get; } = [];

    public int ActiveCount => ActiveItems.Count;

    public int QueuedCount => QueuedItems.Count;

    public int CompletedCount => CompletedItems.Count;

    public int FailedCount => FailedItems.Count;

    public bool IsEmpty => Items.Count == 0;

    public bool HasCompleted => CompletedItems.Count > 0;

    public bool HasActive => ActiveItems.Count > 0;

    public bool HasQueued => QueuedItems.Count > 0;

    public bool HasFailed => FailedItems.Count > 0;

    public string AggregateStatus => ActiveCount == 0 && QueuedCount == 0
        ? FailedCount > 0
            ? $"{FailedCount} transfer{Plural(FailedCount)} failed"
            : "No active transfers"
        : $"{ActiveCount} active, {QueuedCount} queued";

    public async Task<TransferItemViewModel> QueueAsync(
        TransferQueueRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var item = new TransferItemViewModel(this, request);
        if (cancellationToken.CanBeCanceled)
        {
            item.SetExternalCancellation(cancellationToken.Register(() => item.CancelCommand.Execute(null)));
        }
        await _dispatcher.InvokeAsync(() =>
        {
            lock (_stateLock)
            {
                Items.Add(item);
                QueuedItems.Add(item);
                NotifyAggregates();
            }
        }, CancellationToken.None);
        _ = RunAsync(item);
        return item;
    }

    [RelayCommand(CanExecute = nameof(HasCompleted))]
    public void ClearCompleted()
    {
        lock (_stateLock)
        {
            foreach (var item in CompletedItems.ToArray())
            {
                _ = CompletedItems.Remove(item);
                _ = Items.Remove(item);
                item.Dispose();
            }
            NotifyAggregates();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        foreach (var item in Items.Where(item => item.CanCancel))
        {
            item.CancelCommand.Execute(null);
        }
        foreach (var item in Items)
        {
            item.Dispose();
        }
        _concurrency.Dispose();
        GC.SuppressFinalize(this);
    }

    internal async Task RetryAsync(TransferItemViewModel item)
    {
        if (!item.CanRetry)
        {
            return;
        }
        item.ResetForRetry();
        await TransitionAsync(item, ManagedTransferStatus.Queued, null).ConfigureAwait(false);
        _ = RunAsync(item);
    }

    internal async Task RevealAsync(TransferItemViewModel item)
    {
        if (!item.CanReveal)
        {
            return;
        }
        var result = await _reveal.RevealAsync(item.DestinationPath).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            await _dispatcher.InvokeAsync(() => item.FailureReason = result.ErrorMessage, CancellationToken.None);
        }
    }

    private async Task RunAsync(TransferItemViewModel item)
    {
        TransferResult? completedResult = null;
        try
        {
            await _concurrency.WaitAsync(item.CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var cancelled = CancelledResult(item);
            await TransitionAsync(item, ManagedTransferStatus.Cancelled, "The transfer was cancelled.")
                .ConfigureAwait(false);
            item.Complete(cancelled);
            return;
        }

        try
        {
            await TransitionAsync(item, ManagedTransferStatus.Active, null).ConfigureAwait(false);
            await using var progress = new CoalescingProgress(item, _dispatcher, _progressInterval);
            TransferResult result;
            try
            {
                result = await item.Request.Operation(progress, item.CancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                result = CancelledResult(item);
            }
            catch (Exception exception)
            {
                result = new TransferResult([new TransferItemResult(
                    item.SourcePath,
                    item.DestinationPath,
                    TransferItemStatus.Failed,
                    new SftpFailure(SftpError.Unknown, exception.Message))]);
            }
            await progress.FlushAsync().ConfigureAwait(false);
            var status = result.IsSuccess
                ? ManagedTransferStatus.Completed
                : result.IsCancelled
                    ? ManagedTransferStatus.Cancelled
                    : ManagedTransferStatus.Failed;
            var failure = result.Items.FirstOrDefault(entry => entry.Failure is not null)?.Failure?.Message;
            await TransitionAsync(item, status, failure).ConfigureAwait(false);
            completedResult = result;
        }
        finally
        {
            _ = _concurrency.Release();
        }
        item.Complete(completedResult);
    }

    private async Task TransitionAsync(
        TransferItemViewModel item,
        ManagedTransferStatus status,
        string? failure)
    {
        await _dispatcher.InvokeAsync(() =>
        {
            lock (_stateLock)
            {
                _ = QueuedItems.Remove(item);
                _ = ActiveItems.Remove(item);
                _ = CompletedItems.Remove(item);
                _ = FailedItems.Remove(item);
                item.Status = status;
                item.FailureReason = failure;
                switch (status)
                {
                    case ManagedTransferStatus.Queued:
                        QueuedItems.Add(item);
                        break;
                    case ManagedTransferStatus.Active:
                        ActiveItems.Add(item);
                        break;
                    case ManagedTransferStatus.Completed:
                        CompletedItems.Insert(0, item);
                        break;
                    case ManagedTransferStatus.Failed:
                    case ManagedTransferStatus.Cancelled:
                        FailedItems.Insert(0, item);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(status));
                }
                item.NotifyStatusChanged();
                NotifyAggregates();
            }
        }, CancellationToken.None);
    }

    private void NotifyAggregates()
    {
        OnPropertyChanged(nameof(ActiveCount));
        OnPropertyChanged(nameof(QueuedCount));
        OnPropertyChanged(nameof(CompletedCount));
        OnPropertyChanged(nameof(FailedCount));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasCompleted));
        OnPropertyChanged(nameof(HasActive));
        OnPropertyChanged(nameof(HasQueued));
        OnPropertyChanged(nameof(HasFailed));
        OnPropertyChanged(nameof(AggregateStatus));
        ClearCompletedCommand.NotifyCanExecuteChanged();
    }

    private static string Plural(int count)
    {
        return count == 1 ? string.Empty : "s";
    }

    private static TransferResult CancelledResult(TransferItemViewModel item)
    {
        return new TransferResult([new TransferItemResult(
            item.SourcePath,
            item.DestinationPath,
            TransferItemStatus.Cancelled,
            new SftpFailure(SftpError.Cancelled, "The transfer was cancelled."))]);
    }

    private sealed class CoalescingProgress(
        TransferItemViewModel item,
        IUiDispatcher dispatcher,
        TimeSpan interval) : IProgress<TransferProgress>, IAsyncDisposable
    {
        private readonly Lock _sync = new();
        private TransferProgress? _pending;
        private Task? _scheduled;
        private int _disposed;

        public void Report(TransferProgress value)
        {
            lock (_sync)
            {
                _pending = value;
                _scheduled ??= FlushAfterDelayAsync();
            }
        }

        public async Task FlushAsync()
        {
            TransferProgress? latest;
            lock (_sync)
            {
                latest = _pending;
                _pending = null;
            }
            if (latest is not null)
            {
                await dispatcher.InvokeAsync(() => item.ApplyProgress(latest), CancellationToken.None);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                await FlushAsync().ConfigureAwait(false);
            }
        }

        private async Task FlushAfterDelayAsync()
        {
            await Task.Delay(interval).ConfigureAwait(false);
            await FlushAsync().ConfigureAwait(false);
            lock (_sync)
            {
                _scheduled = null;
                if (_pending is not null && Volatile.Read(ref _disposed) == 0)
                {
                    _scheduled = FlushAfterDelayAsync();
                }
            }
        }
    }
}
