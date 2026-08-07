using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Queries;
using RemoteFlow.UI.Services;

namespace RemoteFlow.UI.ViewModels.CommandPalette;

public sealed partial class CommandPaletteViewModel : ObservableObject, IDisposable
{
    private readonly IConnectionQueryService? _queries;
    private readonly IConnectionSessionOpener? _sessionOpener;
    private readonly IRecentConnectionStore? _recent;
    private readonly IClock? _clock;
    private CancellationTokenSource? _searchCancellation;
    private bool _disposed;

    public CommandPaletteViewModel()
    {
    }

    public CommandPaletteViewModel(
        IConnectionQueryService queries,
        IConnectionSessionOpener sessionOpener,
        IRecentConnectionStore recent,
        IClock clock)
    {
        _queries = queries ?? throw new ArgumentNullException(nameof(queries));
        _sessionOpener = sessionOpener ?? throw new ArgumentNullException(nameof(sessionOpener));
        _recent = recent ?? throw new ArgumentNullException(nameof(recent));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public ObservableCollection<CommandPaletteResultViewModel> Results { get; } = [];

    public Task SearchChangesSettled { get; private set; } = Task.CompletedTask;

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial CommandPaletteResultViewModel? SelectedResult { get; set; }

    [ObservableProperty]
    public partial bool HasEmptyState { get; private set; } = true;

    [ObservableProperty]
    public partial string EmptyMessage { get; private set; } = "Type a connection name, host, or tag.";

    [ObservableProperty]
    public partial string? FeedbackMessage { get; private set; }

    public void Open()
    {
        ThrowIfDisposed();
        CancelPendingSearch();
        Results.Clear();
        SelectedResult = null;
        SearchText = string.Empty;
        FeedbackMessage = null;
        EmptyMessage = "Type a connection name, host, or tag.";
        HasEmptyState = true;
        IsOpen = true;
    }

    public void Close()
    {
        CancelPendingSearch();
        IsOpen = false;
    }

    public async Task<bool> ConnectSelectedAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedResult is null || _sessionOpener is null)
        {
            return false;
        }

        var opened = await _sessionOpener.OpenAsync(
            SelectedResult.Id,
            ConnectionOpenMode.Default,
            cancellationToken).ConfigureAwait(true);
        if (!opened)
        {
            FeedbackMessage = $"{SelectedResult.Name} could not be opened.";
            return false;
        }

        if (_recent is not null && _clock is not null)
        {
            await _recent.RecordOpenedAsync(SelectedResult.Id, _clock.UtcNow, cancellationToken).ConfigureAwait(true);
        }

        Close();
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        CancelPendingSearch();
        _disposed = true;
    }

    partial void OnSearchTextChanged(string value)
    {
        if (!IsOpen || _disposed)
        {
            return;
        }

        CancelPendingSearch();
        if (string.IsNullOrWhiteSpace(value))
        {
            Results.Clear();
            SelectedResult = null;
            EmptyMessage = "Type a connection name, host, or tag.";
            HasEmptyState = true;
            return;
        }

        _searchCancellation = new CancellationTokenSource();
        SearchChangesSettled = SearchAsync(value, _searchCancellation.Token);
    }

    private async Task SearchAsync(string text, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(75), cancellationToken).ConfigureAwait(true);
            var matches = _queries is null
                ? []
                : await _queries.SearchPaletteAsync(text, cancellationToken: cancellationToken).ConfigureAwait(true);
            Results.Clear();
            foreach (var match in matches)
            {
                Results.Add(new CommandPaletteResultViewModel(match));
            }

            SelectedResult = Results.FirstOrDefault();
            HasEmptyState = Results.Count == 0;
            EmptyMessage = HasEmptyState
                ? $"No connections match “{text.Trim()}”. Try a name, host, folder, or tag."
                : string.Empty;
            FeedbackMessage = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Results.Clear();
            SelectedResult = null;
            HasEmptyState = true;
            EmptyMessage = "Connections could not be searched.";
            FeedbackMessage = exception.Message;
        }
    }

    private void CancelPendingSearch()
    {
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = null;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
