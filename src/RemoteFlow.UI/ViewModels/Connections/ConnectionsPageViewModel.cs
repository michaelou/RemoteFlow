using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Queries;
using RemoteFlow.Application.Services;
using RemoteFlow.Domain.Abstractions;
using RemoteFlow.Domain.Common;
using RemoteFlow.Domain.Entities;
using RemoteFlow.Domain.Enums;
using RemoteFlow.UI.Services;

namespace RemoteFlow.UI.ViewModels.Connections;

public sealed class ExplorerActionRequestedEventArgs(
    Guid? connectionId,
    Guid? folderId,
    ExplorerAction action) : EventArgs
{
    public Guid? ConnectionId { get; } = connectionId;

    public Guid? FolderId { get; } = folderId;

    public ExplorerAction Action { get; } = action;
}

public sealed partial class ConnectionsPageViewModel : PageViewModel, IDisposable
{
    private readonly IConnectionQueryService _queries;
    private readonly IFolderRepository _folders;
    private readonly ITagRepository _tags;
    private readonly IConnectionService _connections;
    private readonly IFolderService _folderService;
    private readonly IRecentConnectionStore _recent;
    private readonly ISettingsStore _settings;
    private readonly IConnectionSessionOpener _sessionOpener;
    private readonly IConnectionChangeNotifier _changeNotifier;
    private readonly IGuidProvider _guidProvider;
    private readonly IClock _clock;
    private readonly ConnectionEditorViewModelFactory? _editorFactory;
    private readonly IConfirmationDialogService? _confirmation;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private CancellationTokenSource? _filterDebounce;
    private bool _disposed;
    private bool _suppressExpansionEvent;
    private bool _suppressFilterChanges;
    private bool _tagsLoaded;
    private Guid? _feedbackConnectionId;
    private ConnectionOpenMode? _feedbackRecoveryMode;

    public ConnectionsPageViewModel(
        IConnectionQueryService queries,
        IFolderRepository folders,
        IConnectionService connections,
        IFolderService folderService,
        IRecentConnectionStore recent,
        ISettingsStore settings,
        IConnectionSessionOpener sessionOpener,
        IConnectionChangeNotifier changeNotifier,
        IGuidProvider guidProvider,
        IClock clock)
        : this(
            queries,
            folders,
            EmptyTagRepository.Instance,
            connections,
            folderService,
            recent,
            settings,
            sessionOpener,
            changeNotifier,
            guidProvider,
            clock)
    {
    }

    public ConnectionsPageViewModel(
        IConnectionQueryService queries,
        IFolderRepository folders,
        ITagRepository tags,
        IConnectionService connections,
        IFolderService folderService,
        IRecentConnectionStore recent,
        ISettingsStore settings,
        IConnectionSessionOpener sessionOpener,
        IConnectionChangeNotifier changeNotifier,
        IGuidProvider guidProvider,
        IClock clock)
        : this(
            queries,
            folders,
            tags,
            connections,
            folderService,
            recent,
            settings,
            sessionOpener,
            changeNotifier,
            guidProvider,
            clock,
            null,
            null)
    {
    }

    public ConnectionsPageViewModel(
        IConnectionQueryService queries,
        IFolderRepository folders,
        ITagRepository tags,
        IConnectionService connections,
        IFolderService folderService,
        IRecentConnectionStore recent,
        ISettingsStore settings,
        IConnectionSessionOpener sessionOpener,
        IConnectionChangeNotifier changeNotifier,
        IGuidProvider guidProvider,
        IClock clock,
        ConnectionEditorViewModelFactory? editorFactory,
        IConfirmationDialogService? confirmation)
        : base("Connections")
    {
        _queries = queries;
        _folders = folders;
        _tags = tags;
        _connections = connections;
        _folderService = folderService;
        _recent = recent;
        _settings = settings;
        _sessionOpener = sessionOpener;
        _changeNotifier = changeNotifier;
        _guidProvider = guidProvider;
        _clock = clock;
        _editorFactory = editorFactory;
        _confirmation = confirmation;
        changeNotifier.ConnectionChanged += OnConnectionChanged;
        ProtocolFilters =
        [
            ConnectionFilterChipViewModel.ForProtocol(ProtocolType.Ssh),
            ConnectionFilterChipViewModel.ForProtocol(ProtocolType.Sftp),
            ConnectionFilterChipViewModel.ForProtocol(ProtocolType.Rdp),
            ConnectionFilterChipViewModel.ForProtocol(ProtocolType.S3),
            ConnectionFilterChipViewModel.ForProtocol(ProtocolType.AzureBlob),
        ];
        EnvironmentFilters =
        [
            ConnectionFilterChipViewModel.ForEnvironment(EnvironmentKind.Development),
            ConnectionFilterChipViewModel.ForEnvironment(EnvironmentKind.Staging),
            ConnectionFilterChipViewModel.ForEnvironment(EnvironmentKind.Production),
        ];
        foreach (var chip in ProtocolFilters.Concat(EnvironmentFilters))
        {
            chip.SelectionChanged += OnFilterChipSelectionChanged;
        }
    }

    public event EventHandler<ExplorerActionRequestedEventArgs>? ActionRequested;

    /// <summary>Raised when a node enters inline rename on the view model's initiative, so the view can
    /// move focus into the editor the same way it does for F2.</summary>
    public event EventHandler<ExplorerNodeViewModel>? RenameStarted;

    public ObservableCollection<ExplorerNodeViewModel> RootNodes { get; } = [];

    public ObservableCollection<ExplorerNodeViewModel> SelectedNodes { get; } = [];

    public ObservableCollection<ConnectionFilterChipViewModel> ProtocolFilters { get; }

    public ObservableCollection<ConnectionFilterChipViewModel> EnvironmentFilters { get; }

    public ObservableCollection<ConnectionFilterChipViewModel> TagFilters { get; } = [];

    public Task ConnectionChangesSettled { get; private set; } = Task.CompletedTask;

    public Task SearchChangesSettled { get; private set; } = Task.CompletedTask;

    public Task WorkspaceChangesSettled { get; private set; } = Task.CompletedTask;

    [ObservableProperty]
    public partial ConnectionEditorViewModel? Editor { get; private set; }

    [ObservableProperty]
    public partial ConnectionDetailsViewModel? Details { get; private set; }

    public bool IsEditorOpen => Editor is not null;

    [ObservableProperty]
    public partial string? SearchText { get; set; }

    [ObservableProperty]
    public partial bool FavoritesOnly { get; set; }

    [ObservableProperty]
    public partial bool HasActiveFilters { get; private set; }

    [ObservableProperty]
    public partial string ActiveFilterSummary { get; private set; } = string.Empty;

    public string EmptyStateTitle => HasActiveFilters ? "No matching connections" : "No connections yet";

    public string EmptyStateMessage => HasActiveFilters
        ? "Try a different search or clear the active filters."
        : "Create your first connection to get started.";

    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial string? FeedbackMessage { get; private set; }

    [ObservableProperty]
    public partial string? FeedbackActionLabel { get; private set; }

    partial void OnFeedbackMessageChanged(string? value)
    {
        ClearFeedbackAction();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await EnsureTagFiltersAsync(cancellationToken).ConfigureAwait(true);
        if (RootNodes.Count == 0)
        {
            await RefreshAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await EnsureTagFiltersAsync(cancellationToken).ConfigureAwait(true);
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            IsLoading = true;
            var items = await _queries.QueryAsync(BuildFilter(), cancellationToken).ConfigureAwait(true);
            var folders = await _folders.ListAsync(cancellationToken).ConfigureAwait(true);
            var recentLimit = await _settings.Get(SettingKeys.RecentLimit, cancellationToken).ConfigureAwait(true);
            var recent = await _recent.ListAsync(recentLimit, cancellationToken).ConfigureAwait(true);
            RebuildTree(items, folders, recent);
            FeedbackMessage = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            FeedbackMessage = $"The connection explorer could not be refreshed: {exception.Message}";
        }
        finally
        {
            IsLoading = false;
            _ = _refreshLock.Release();
        }
    }

    public void SelectNode(ExplorerNodeViewModel node, bool additive)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!additive)
        {
            foreach (var selected in SelectedNodes)
            {
                selected.IsSelected = false;
            }

            SelectedNodes.Clear();
        }

        if (!SelectedNodes.Contains(node))
        {
            node.IsSelected = true;
            SelectedNodes.Add(node);
        }

        if (node.Kind != ExplorerNodeKind.Connection || node.Id is not { } connectionId)
        {
            return;
        }

        // The right-hand pane follows the selection whichever pane is showing: with the editor open,
        // picking another connection moves the editor to it rather than leaving it on the old one.
        WorkspaceChangesSettled = Editor is null
            ? ShowDetailsAsync(connectionId)
            : SwitchEditorAsync(connectionId);
    }

    public async Task<bool> DropAsync(
        IEnumerable<ExplorerNodeViewModel> draggedNodes,
        ExplorerNodeViewModel? target,
        int? insertionIndex = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draggedNodes);
        if (target is not null && target.Kind != ExplorerNodeKind.Folder)
        {
            FeedbackMessage = "Drop connections and folders onto a folder or the explorer root.";
            return false;
        }

        var targetFolderId = target?.Id;
        var order = insertionIndex;
        foreach (var node in draggedNodes.Where(node => !node.IsVirtual).Distinct())
        {
            RemoteFlowError? failure = null;
            if (node.Kind == ExplorerNodeKind.Folder && node.Id is { } folderId)
            {
                var moved = await _folderService.MoveAsync(folderId, targetFolderId, cancellationToken).ConfigureAwait(true);
                if (moved.IsFailure)
                {
                    failure = moved.Error;
                }
            }
            else if (node.Kind == ExplorerNodeKind.Connection && node.Id is { } connectionId)
            {
                var moved = await _connections.MoveToFolderAsync(connectionId, targetFolderId, cancellationToken).ConfigureAwait(true);
                if (moved.IsFailure)
                {
                    failure = moved.Error;
                }
                else if (order is not null)
                {
                    var reordered = await _connections.SetSortOrderAsync(connectionId, order, cancellationToken).ConfigureAwait(true);
                    if (reordered.IsFailure)
                    {
                        failure = reordered.Error;
                    }

                    order++;
                }
            }

            if (failure is not null)
            {
                FeedbackMessage = failure.Message;
                return false;
            }
        }

        await RefreshAsync(cancellationToken).ConfigureAwait(true);
        return true;
    }

    public async Task SetExpandedAsync(
        ExplorerNodeViewModel node,
        bool isExpanded,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.Folder is null)
        {
            node.IsExpanded = isExpanded;
            return;
        }

        _suppressExpansionEvent = true;
        node.IsExpanded = isExpanded;
        _suppressExpansionEvent = false;
        _ = node.Folder.SetPresentation(
            node.Folder.SortOrder,
            isExpanded,
            _guidProvider,
            _clock.UtcNow);
        await _folders.UpdateAsync(node.Folder, cancellationToken).ConfigureAwait(true);
    }

    public async Task ClearRecentAsync(CancellationToken cancellationToken = default)
    {
        const int batchSize = 100;
        while (true)
        {
            var recent = await _recent.ListAsync(batchSize, cancellationToken).ConfigureAwait(true);
            if (recent.Count == 0)
            {
                break;
            }

            foreach (var item in recent)
            {
                await _recent.RemoveAsync(item.ConnectionId, cancellationToken).ConfigureAwait(true);
            }
        }

        await RefreshAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Creates a folder under <paramref name="parentId"/> (top level when null) and drops it
    /// straight into inline rename, so naming it is part of creating it.</summary>
    public async Task<Guid?> CreateFolderAsync(
        Guid? parentId = null,
        CancellationToken cancellationToken = default)
    {
        var existing = await _folders.ListAsync(cancellationToken).ConfigureAwait(true);
        var created = await _folderService
            .CreateAsync(NextFolderName(existing, parentId), parentId, cancellationToken)
            .ConfigureAwait(true);
        if (created.IsFailure)
        {
            FeedbackMessage = created.Error.Message;
            return null;
        }

        await RefreshAsync(cancellationToken).ConfigureAwait(true);
        var node = FindFolderNode(RootNodes, created.Value.Id);
        if (node is null)
        {
            return created.Value.Id;
        }

        _ = ExpandAncestors(RootNodes, node);
        SelectNode(node, additive: false);
        node.BeginRenameCommand.Execute(null);
        RenameStarted?.Invoke(this, node);
        return created.Value.Id;
    }

    public void RequestCreateFolder(Guid? parentId = null)
    {
        WorkspaceChangesSettled = CreateFolderAsync(parentId);
    }

    public void RequestCreateConnection()
    {
        if (_editorFactory is null)
        {
            ActionRequested?.Invoke(this, new ExplorerActionRequestedEventArgs(null, null, ExplorerAction.Edit));
            return;
        }

        WorkspaceChangesSettled = OpenEditorAsync(null);
    }

    public async Task<bool> SaveEditorAsync(
        ReadOnlyMemory<char> capturedSecret,
        CancellationToken cancellationToken = default)
    {
        if (Editor is null || !await Editor.SaveAsync(capturedSecret, cancellationToken).ConfigureAwait(true))
        {
            return false;
        }

        var connectionId = Editor.ConnectionId!.Value;
        Editor = null;
        OnPropertyChanged(nameof(IsEditorOpen));
        await RefreshAsync(cancellationToken).ConfigureAwait(true);
        await ShowDetailsAsync(connectionId, cancellationToken).ConfigureAwait(true);
        return true;
    }

    public async Task<bool> CloseEditorAsync(CancellationToken cancellationToken = default)
    {
        if (Editor is null)
        {
            return true;
        }

        if (Editor.IsDirty && _confirmation is not null &&
            !await _confirmation.ConfirmAsync(
                "Discard unsaved changes?",
                $"Discard the unsaved changes to '{Editor.Name}'?",
                "Discard",
                cancellationToken).ConfigureAwait(true))
        {
            return false;
        }

        var connectionId = Editor.ConnectionId;
        Editor = null;
        OnPropertyChanged(nameof(IsEditorOpen));
        if (connectionId is { } id)
        {
            await ShowDetailsAsync(id, cancellationToken).ConfigureAwait(true);
        }

        return true;
    }

    public Task<bool> CanNavigateAwayAsync(CancellationToken cancellationToken = default)
    {
        return CloseEditorAsync(cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _changeNotifier.ConnectionChanged -= OnConnectionChanged;
        _filterDebounce?.Cancel();
        _filterDebounce?.Dispose();
        foreach (var chip in ProtocolFilters.Concat(EnvironmentFilters).Concat(TagFilters))
        {
            chip.SelectionChanged -= OnFilterChipSelectionChanged;
        }

        _refreshLock.Dispose();
        _disposed = true;
    }

    public void ClearAllFilters()
    {
        _suppressFilterChanges = true;
        SearchText = string.Empty;
        FavoritesOnly = false;
        foreach (var chip in ProtocolFilters.Concat(EnvironmentFilters).Concat(TagFilters))
        {
            chip.IsSelected = false;
        }

        _suppressFilterChanges = false;
        ScheduleFilterRefresh();
    }

    private ConnectionFilter BuildFilter()
    {
        return new ConnectionFilter
        {
            Text = SearchText,
            Protocols = [.. ProtocolFilters.Where(chip => chip.IsSelected).Select(chip => chip.Protocol!.Value)],
            Environments = [.. EnvironmentFilters.Where(chip => chip.IsSelected).Select(chip => chip.Environment!.Value)],
            Tags = [.. TagFilters.Where(chip => chip.IsSelected).Select(chip => chip.TagId!.Value)],
            FavoritesOnly = FavoritesOnly,
            SortBy = ConnectionSortBy.SortOrder,
        };
    }

    private async Task EnsureTagFiltersAsync(CancellationToken cancellationToken)
    {
        if (_tagsLoaded)
        {
            return;
        }

        var tags = await _tags.ListAsync(cancellationToken).ConfigureAwait(true);
        foreach (var tag in tags.OrderBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase))
        {
            var chip = ConnectionFilterChipViewModel.ForTag(tag.Id, tag.Name);
            chip.SelectionChanged += OnFilterChipSelectionChanged;
            TagFilters.Add(chip);
        }

        _tagsLoaded = true;
    }

    private void ScheduleFilterRefresh()
    {
        UpdateFilterSummary();
        if (_suppressFilterChanges || _disposed)
        {
            return;
        }

        _filterDebounce?.Cancel();
        _filterDebounce?.Dispose();
        _filterDebounce = new CancellationTokenSource();
        SearchChangesSettled = DebounceAndRefreshAsync(_filterDebounce.Token);
    }

    private async Task DebounceAndRefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken).ConfigureAwait(true);
            await RefreshAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void UpdateFilterSummary()
    {
        var active = new List<string>();
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            active.Add($"Text: {SearchText.Trim()}");
        }

        active.AddRange(ProtocolFilters.Where(chip => chip.IsSelected).Select(chip => chip.Label));
        active.AddRange(EnvironmentFilters.Where(chip => chip.IsSelected).Select(chip => chip.Label));
        active.AddRange(TagFilters.Where(chip => chip.IsSelected).Select(chip => $"Tag: {chip.Label}"));
        if (FavoritesOnly)
        {
            active.Add("Favorites only");
        }

        HasActiveFilters = active.Count > 0;
        ActiveFilterSummary = string.Join(" • ", active);
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateMessage));
    }

    private void OnFilterChipSelectionChanged(object? sender, EventArgs e)
    {
        ScheduleFilterRefresh();
    }

    partial void OnSearchTextChanged(string? value)
    {
        ScheduleFilterRefresh();
    }

    partial void OnFavoritesOnlyChanged(bool value)
    {
        ScheduleFilterRefresh();
    }

    private void RebuildTree(
        IReadOnlyList<ConnectionListItem> items,
        IReadOnlyList<Folder> folders,
        IReadOnlyList<RecentConnection> recent)
    {
        RootNodes.Clear();
        SelectedNodes.Clear();
        var favoriteRoot = CreateNode(ExplorerNodeKind.Favorites, "Favorites", iconKey: "Icon.Favorite");
        foreach (var item in items.Where(item => item.IsFavorite))
        {
            favoriteRoot.Children.Add(CreateConnectionNode(item));
        }

        var itemById = items.ToDictionary(item => item.Id);
        var recentRoot = CreateNode(ExplorerNodeKind.Recent, "Recent", iconKey: "Icon.Recent");
        foreach (var recentItem in recent)
        {
            if (itemById.TryGetValue(recentItem.ConnectionId, out var item))
            {
                recentRoot.Children.Add(CreateConnectionNode(item));
            }
        }

        RootNodes.Add(favoriteRoot);
        RootNodes.Add(recentRoot);
        var folderNodes = folders.ToDictionary(
            folder => folder.Id,
            CreateFolderNode);
        foreach (var folder in folders.OrderBy(folder => folder.SortOrder).ThenBy(folder => folder.Name, StringComparer.OrdinalIgnoreCase))
        {
            var node = folderNodes[folder.Id];
            if (folder.ParentId is { } parentId && folderNodes.TryGetValue(parentId, out var parent))
            {
                parent.Children.Add(node);
            }
            else
            {
                RootNodes.Add(node);
            }
        }

        foreach (var item in items)
        {
            var node = CreateConnectionNode(item);
            if (item.FolderId is { } folderId && folderNodes.TryGetValue(folderId, out var folder))
            {
                folder.Children.Add(node);
            }
            else
            {
                RootNodes.Add(node);
            }
        }

        // Folders on their own keep the tree on screen: the empty state covers it, and a user who has
        // built a folder layout before adding connections still needs to reach it.
        IsEmpty = items.Count == 0 && (HasActiveFilters || folders.Count == 0);
    }

    private static string NextFolderName(IEnumerable<Folder> folders, Guid? parentId)
    {
        const string baseName = "New folder";
        var siblings = folders
            .Where(folder => folder.ParentId == parentId)
            .Select(folder => folder.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!siblings.Contains(baseName))
        {
            return baseName;
        }

        var suffix = 2;
        while (siblings.Contains($"{baseName} {suffix}"))
        {
            suffix++;
        }

        return $"{baseName} {suffix}";
    }

    private static ExplorerNodeViewModel? FindFolderNode(
        IEnumerable<ExplorerNodeViewModel> nodes,
        Guid id)
    {
        foreach (var node in nodes)
        {
            if (node.Kind == ExplorerNodeKind.Folder && node.Id == id)
            {
                return node;
            }

            if (FindFolderNode(node.Children, id) is { } match)
            {
                return match;
            }
        }

        return null;
    }

    private static bool ExpandAncestors(
        IEnumerable<ExplorerNodeViewModel> nodes,
        ExplorerNodeViewModel target)
    {
        foreach (var node in nodes)
        {
            if (ReferenceEquals(node, target))
            {
                return true;
            }

            if (ExpandAncestors(node.Children, target))
            {
                node.IsExpanded = true;
                return true;
            }
        }

        return false;
    }

    private ExplorerNodeViewModel CreateFolderNode(Folder folder)
    {
        var node = CreateNode(
            ExplorerNodeKind.Folder,
            folder.Name,
            folder.Id,
            folder,
            iconKey: "Icon.Folder");
        node.ExpansionChanged += OnFolderExpansionChanged;
        return node;
    }

    private ExplorerNodeViewModel CreateConnectionNode(ConnectionListItem item)
    {
        return CreateNode(
            ExplorerNodeKind.Connection,
            item.Name,
            item.Id,
            connection: item,
            // A connection is drawn as the thing it opens: a shell, a file browser, or a screen.
            iconKey: item.Protocol switch
            {
                ProtocolType.Ssh => "Icon.Terminals",
                ProtocolType.Sftp => "Icon.Sftp",
                ProtocolType.Rdp => "Icon.RemoteDesktop",
                ProtocolType.S3 => "Icon.Storage",
                ProtocolType.AzureBlob => "Icon.Storage",
                _ => throw new ArgumentOutOfRangeException(nameof(item)),
            });
    }

    private ExplorerNodeViewModel CreateNode(
        ExplorerNodeKind kind,
        string name,
        Guid? id = null,
        Folder? folder = null,
        ConnectionListItem? connection = null,
        string iconKey = "")
    {
        return new ExplorerNodeViewModel(kind, name, ExecuteActionAsync, RenameAsync, id, folder, connection, iconKey);
    }

    private async Task ExecuteActionAsync(ExplorerNodeViewModel node, ExplorerAction action)
    {
        if (node.Id is null && action != ExplorerAction.NewFolder)
        {
            return;
        }

        switch (action)
        {
            case ExplorerAction.Connect:
            case ExplorerAction.OpenSftp:
            case ExplorerAction.OpenRdp:
                var mode = action switch
                {
                    ExplorerAction.Connect => ConnectionOpenMode.Default,
                    ExplorerAction.OpenSftp => ConnectionOpenMode.Sftp,
                    ExplorerAction.OpenRdp => ConnectionOpenMode.Rdp,
                    ExplorerAction.Edit => throw new ArgumentOutOfRangeException(nameof(action)),
                    ExplorerAction.Duplicate => throw new ArgumentOutOfRangeException(nameof(action)),
                    ExplorerAction.Delete => throw new ArgumentOutOfRangeException(nameof(action)),
                    ExplorerAction.NewFolder => throw new ArgumentOutOfRangeException(nameof(action)),
                    _ => throw new ArgumentOutOfRangeException(nameof(action)),
                };
                var opened = await _sessionOpener.OpenAsync(node.Id!.Value, mode).ConfigureAwait(true);
                if (opened.Opened)
                {
                    await RefreshAsync().ConfigureAwait(true);
                }
                else
                {
                    ShowOpenFailure(
                        node.Id.Value,
                        opened,
                        "The connection did not open, so it was not added to Recent.");
                }

                break;
            case ExplorerAction.Duplicate:
                _ = await _connections.DuplicateAsync(node.Id!.Value).ConfigureAwait(true);
                await RefreshAsync().ConfigureAwait(true);
                break;
            case ExplorerAction.Delete:
                var isFolder = node.Kind == ExplorerNodeKind.Folder;
                if (_confirmation is not null && !await _confirmation.ConfirmAsync(
                        isFolder ? "Delete folder?" : "Delete connection?",
                        isFolder
                            ? $"Delete '{node.Name}'? Its connections and subfolders move up to the parent folder."
                            : $"Delete '{node.Name}'? This action cannot be undone.",
                        "Delete").ConfigureAwait(true))
                {
                    break;
                }

                if (isFolder)
                {
                    var deletedFolder = await _folderService.DeleteAsync(node.Id!.Value).ConfigureAwait(true);
                    if (deletedFolder.IsFailure)
                    {
                        FeedbackMessage = deletedFolder.Error.Message;
                    }
                }
                else
                {
                    _ = await _connections.DeleteAsync(node.Id!.Value).ConfigureAwait(true);
                }

                await RefreshAsync().ConfigureAwait(true);
                break;
            case ExplorerAction.NewFolder:
                _ = await CreateFolderAsync(node.Kind == ExplorerNodeKind.Folder ? node.Id : null)
                    .ConfigureAwait(true);
                break;
            case ExplorerAction.Edit:
                if (_editorFactory is not null)
                {
                    await OpenEditorAsync(node.Id).ConfigureAwait(true);
                }
                else
                {
                    ActionRequested?.Invoke(this, new ExplorerActionRequestedEventArgs(
                        node.Kind == ExplorerNodeKind.Connection ? node.Id : null,
                        node.Kind == ExplorerNodeKind.Folder ? node.Id : null,
                        action));
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action));
        }
    }

    private async Task<bool> RenameAsync(ExplorerNodeViewModel node, string name)
    {
        if (node.Id is not { } id)
        {
            return false;
        }

        var result = node.Kind == ExplorerNodeKind.Folder
            ? await RenameFolderAsync(id, name).ConfigureAwait(true)
            : await RenameConnectionAsync(id, name).ConfigureAwait(true);
        if (result is not null)
        {
            FeedbackMessage = result.Message;
            return false;
        }

        return true;
    }

    private async Task<RemoteFlowError?> RenameFolderAsync(Guid id, string name)
    {
        var result = await _folderService.RenameAsync(id, name).ConfigureAwait(true);
        return result.IsFailure ? result.Error : null;
    }

    private async Task<RemoteFlowError?> RenameConnectionAsync(Guid id, string name)
    {
        var result = await _connections.RenameAsync(id, name).ConfigureAwait(true);
        return result.IsFailure ? result.Error : null;
    }

    private async void OnFolderExpansionChanged(ExplorerNodeViewModel node, bool isExpanded)
    {
        if (_suppressExpansionEvent || node.Folder?.IsExpanded == isExpanded)
        {
            return;
        }

        try
        {
            await SetExpandedAsync(node, isExpanded).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            FeedbackMessage = $"Folder expansion could not be saved: {exception.Message}";
        }
    }

    private async void OnConnectionChanged(object? sender, ConnectionChangedEventArgs e)
    {
        ConnectionChangesSettled = HandleConnectionChangeAsync(e.Kind);
        await ConnectionChangesSettled.ConfigureAwait(true);
    }

    private Task HandleConnectionChangeAsync(ConnectionChangeKind kind)
    {
        if (kind == ConnectionChangeKind.Reloaded)
        {
            // A backup import rewrote the store underneath the page: the tag chips, the open editor, and
            // the details pane all describe rows that may be gone, so none of them is carried over.
            ResetTagFilters();
            Editor = null;
            Details = null;
            OnPropertyChanged(nameof(IsEditorOpen));
        }

        return RefreshAsync();
    }

    /// <summary>Drops the cached tag chips so the next refresh reloads them from the imported data.</summary>
    private void ResetTagFilters()
    {
        foreach (var chip in TagFilters)
        {
            chip.SelectionChanged -= OnFilterChipSelectionChanged;
        }

        TagFilters.Clear();
        _tagsLoaded = false;
        UpdateFilterSummary();
    }

    private async Task OpenEditorAsync(Guid? connectionId, CancellationToken cancellationToken = default)
    {
        if (_editorFactory is null || !await CloseEditorAsync(cancellationToken).ConfigureAwait(true))
        {
            return;
        }

        Editor = await _editorFactory.CreateEditorAsync(connectionId, cancellationToken).ConfigureAwait(true);
        Details = null;
        OnPropertyChanged(nameof(IsEditorOpen));
    }

    /// <summary>Retargets the open editor at <paramref name="connectionId"/>. Unsaved work still has to be
    /// discarded explicitly, so a declined prompt leaves the editor where it was.</summary>
    private Task SwitchEditorAsync(Guid connectionId, CancellationToken cancellationToken = default)
    {
        return Editor is { } editor && editor.ConnectionId == connectionId
            ? Task.CompletedTask
            : OpenEditorAsync(connectionId, cancellationToken);
    }

    private async Task ShowDetailsAsync(Guid connectionId, CancellationToken cancellationToken = default)
    {
        if (_editorFactory is null)
        {
            return;
        }

        Details = await _editorFactory.CreateDetailsAsync(
            connectionId,
            mode => OpenFromDetailsAsync(connectionId, mode),
            () => OpenEditorAsync(connectionId),
            () => DuplicateFromDetailsAsync(connectionId),
            () => DeleteFromDetailsAsync(connectionId),
            cancellationToken).ConfigureAwait(true);
    }

    private async Task OpenFromDetailsAsync(Guid connectionId, ConnectionOpenMode mode)
    {
        var opened = await _sessionOpener.OpenAsync(connectionId, mode).ConfigureAwait(true);
        if (opened.Opened)
        {
            await ShowDetailsAsync(connectionId).ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
        }
        else if (opened.Message is { } message)
        {
            ShowOpenFailure(connectionId, opened, message);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunFeedbackAction))]
    private async Task RunFeedbackActionAsync()
    {
        if (_feedbackConnectionId is not { } connectionId ||
            _feedbackRecoveryMode is not { } recoveryMode)
        {
            return;
        }

        var opened = await _sessionOpener.OpenAsync(connectionId, recoveryMode).ConfigureAwait(true);
        if (opened.Opened)
        {
            FeedbackMessage = null;
            ClearFeedbackAction();
            await RefreshAsync().ConfigureAwait(true);
        }
        else
        {
            ShowOpenFailure(connectionId, opened, "The connection did not open.");
        }
    }

    private bool CanRunFeedbackAction()
    {
        return _feedbackConnectionId is not null && _feedbackRecoveryMode is not null;
    }

    private void ShowOpenFailure(Guid connectionId, ConnectionOpenResult result, string fallbackMessage)
    {
        FeedbackMessage = result.Message ?? fallbackMessage;
        _feedbackConnectionId = result.RecoveryMode is null ? null : connectionId;
        _feedbackRecoveryMode = result.RecoveryMode;
        FeedbackActionLabel = result.RecoveryActionLabel;
        RunFeedbackActionCommand.NotifyCanExecuteChanged();
    }

    private void ClearFeedbackAction()
    {
        _feedbackConnectionId = null;
        _feedbackRecoveryMode = null;
        FeedbackActionLabel = null;
        RunFeedbackActionCommand.NotifyCanExecuteChanged();
    }

    private async Task DuplicateFromDetailsAsync(Guid connectionId)
    {
        var result = await _connections.DuplicateAsync(connectionId).ConfigureAwait(true);
        if (result.IsFailure)
        {
            FeedbackMessage = result.Error.Message;
            return;
        }

        await RefreshAsync().ConfigureAwait(true);
        await ShowDetailsAsync(result.Value.Id).ConfigureAwait(true);
    }

    private async Task DeleteFromDetailsAsync(Guid connectionId)
    {
        var name = Details?.Name ?? "this connection";
        if (_confirmation is not null && !await _confirmation.ConfirmAsync(
                "Delete connection?",
                $"Delete '{name}'? This action cannot be undone.",
                "Delete").ConfigureAwait(true))
        {
            return;
        }

        var deleted = await _connections.DeleteAsync(connectionId).ConfigureAwait(true);
        if (deleted.IsFailure)
        {
            FeedbackMessage = deleted.Error.Message;
            return;
        }

        Details = null;
        await RefreshAsync().ConfigureAwait(true);
    }

    private sealed class EmptyTagRepository : ITagRepository
    {
        public static EmptyTagRepository Instance { get; } = new();

        public Task<Tag?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<Tag?>(null);
        }

        public Task<Tag?> GetByNameAsync(string name, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<Tag?>(null);
        }

        public Task<IReadOnlyList<Tag>> ListAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<Tag>>([]);
        }

        public Task AddAsync(Tag tag, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task UpdateAsync(Tag tag, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<int> GetUsageCountAsync(Guid id, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(0);
        }
    }
}
