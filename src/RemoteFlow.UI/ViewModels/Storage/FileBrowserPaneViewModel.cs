using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteFlow.Application.Abstractions.Storage;
using RemoteFlow.UI.Services;

namespace RemoteFlow.UI.ViewModels.Storage;

public enum FileBrowserSortColumn
{
    Name = 0,
    Size = 1,
    Modified = 2,
}

public sealed partial class FileBrowserItemViewModel(FileBrowserEntry entry, string transferLabel = "Transfer")
    : ObservableObject
{
    public FileBrowserEntry Entry { get; } = entry;

    /// <summary>"Upload" or "Download", carried on the row rather than reached for through the visual tree.
    /// A context menu lives in a popup, so <c>$parent[FileBrowserPane]</c> does not find the pane from
    /// inside it and the header would silently bind to nothing.</summary>
    public string TransferLabel { get; } = transferLabel;

    public string Name => Entry.Name;

    public string Path => Entry.Path;

    public long Size => Entry.Size;

    public string SizeText => Entry.IsDirectory ? "—" : FormatSize(Entry.Size);

    public DateTimeOffset? Modified => Entry.Modified;

    public string ModifiedText => Entry.Modified is { } modified
        ? modified.LocalDateTime.ToString("g", System.Globalization.CultureInfo.CurrentCulture)
        : "—";

    public bool IsDirectory => Entry.IsDirectory;

    [ObservableProperty]
    public partial bool IsRenaming { get; set; }

    [ObservableProperty]
    public partial string RenameText { get; set; } = entry.Name;

    private static string FormatSize(long bytes)
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

/// <summary>One browser pane over one <see cref="IFileBrowserSource"/>. Instantiated twice by
/// <see cref="StoragePageViewModel"/> — once over the local filesystem and once over the bucket — which
/// is why nothing here knows which of the two it is beyond <see cref="PaneName"/>.
///
/// <b>The accessible-name trap:</b> one control used twice makes both Refresh buttons announce the same
/// name, the audit passes, and a screen-reader user cannot tell the panes apart. Every actionable control
/// binds its name to one of the pane-scoped labels below, so the two read "Refresh the local folder" and
/// "Refresh the remote prefix".</summary>
public sealed partial class FileBrowserPaneViewModel : ObservableObject
{
    /// <summary>Rows per request. Both providers cap a listing at a thousand keys.</summary>
    public const int PageSize = 1_000;

    /// <summary>The hard cap, in pages and in rows. Handing a <c>ListBox</c> a materialised list over a
    /// 500,000-key prefix is the one thing this design must not do, and a "Load more" a user can hold down
    /// would get there.</summary>
    public const int MaxPages = 10;

    public const int MaxRows = 10_000;

    private readonly IConfirmationDialogService _confirmation;
    private readonly ILocalFolderMemory? _folderMemory;
    private readonly List<string> _backHistory = [];
    private readonly List<string> _forwardHistory = [];
    private string? _continuationToken;
    private int _pagesLoaded;
    private bool _syncingRoot;
    private string? _rememberedPath;
    private CancellationTokenSource? _busyIndicatorDelay;

    public FileBrowserPaneViewModel(
        string paneName,
        string transferLabel,
        IConfirmationDialogService confirmation,
        IFileBrowserSource? source = null,
        ILocalFolderMemory? folderMemory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paneName);
        ArgumentException.ThrowIfNullOrWhiteSpace(transferLabel);
        PaneName = paneName;
        TransferLabel = transferLabel;
        _confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation));
        Source = source;
        // Only a pane the user roams freely gets a memory. The remote pane's root is pinned by the
        // connection, and restoring a prefix from a different bucket would open on an error banner.
        _folderMemory = folderMemory;
    }

    /// <summary>"local folder" or "remote prefix". Every accessible name on the pane is derived from it.
    /// </summary>
    public string PaneName { get; }

    public string TransferLabel { get; }

    public string RefreshLabel => $"Refresh the {PaneName}";

    public string BackLabel => $"Back in the {PaneName}";

    public string ForwardLabel => $"Forward in the {PaneName}";

    public string UpLabel => $"Up one level in the {PaneName}";

    public string PathLabel => $"Path of the {PaneName}";

    public string ListLabel => $"Contents of the {PaneName}";

    public string NewFolderLabel => $"New folder in the {PaneName}";

    public string NewFolderNameLabel => $"Name of the new folder in the {PaneName}";

    public string RenameLabel => $"Rename in the {PaneName}";

    public string TransferAccessibleLabel => $"{TransferLabel} the selection in the {PaneName}";

    public string FilterLabel => $"Narrow the {PaneName} to names starting with";

    public string LoadMoreLabel => $"Load more rows into the {PaneName}";

    public string HiddenLabel => $"Show hidden entries in the {PaneName}";

    public string SortNameLabel => $"Sort the {PaneName} by name";

    public string SortSizeLabel => $"Sort the {PaneName} by size";

    public string SortModifiedLabel => $"Sort the {PaneName} by modified date";

    public string RootsLabel => $"Drive shown in the {PaneName}";

    public ObservableCollection<FileBrowserItemViewModel> Items { get; } = [];

    public ObservableCollection<FileBrowserItemViewModel> SelectedItems { get; } = [];

    public ObservableCollection<FileBrowserCrumb> Breadcrumbs { get; } = [];

    /// <summary>Where this pane can be rooted: the ready drives on Windows, and nothing at all on the
    /// object-storage side, where the connection already pins the root.</summary>
    public ObservableCollection<FileBrowserCrumb> Roots { get; } = [];

    /// <summary>Shown only when there is a choice to make. One drive, or a bucket, is not a picker.
    /// </summary>
    public bool HasRoots => Roots.Count > 1;

    /// <summary>Set by the page when a connection attaches. Null means "nothing to browse yet", which is
    /// the remote pane's state before Connect and never the local pane's.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    [NotifyPropertyChangedFor(nameof(SupportsRename))]
    [NotifyPropertyChangedFor(nameof(SupportsHiddenEntries))]
    [NotifyPropertyChangedFor(nameof(SourceTitle))]
    public partial IFileBrowserSource? Source { get; set; }

    /// <summary>Supplied by the page, which is the only thing that can see both panes at once.</summary>
    public Func<CancellationToken, Task>? TransferHandler { get; set; }

    public bool IsReady => Source is not null;

    public bool SupportsRename => Source?.SupportsRename ?? false;

    public bool SupportsHiddenEntries => Source?.SupportsHiddenEntries ?? false;

    public string SourceTitle => Source?.DisplayName ?? "Not connected";

    /// <summary>The root the current path is under. Setting it navigates; navigation sets it back, which
    /// is why the write is guarded — otherwise walking into <c>D:\work</c> would re-navigate to
    /// <c>D:\</c>.</summary>
    [ObservableProperty]
    public partial FileBrowserCrumb? SelectedRoot { get; set; }

    [ObservableProperty]
    public partial string CurrentPath { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string PathText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FilterText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ShowHiddenEntries { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    /// <summary>How long a load must run before the busy indicator appears. Loads that finish sooner never
    /// show it, so quick browsing does not flicker.</summary>
    public TimeSpan BusyIndicatorDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    [ObservableProperty]
    public partial bool IsBusyIndicatorVisible { get; private set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial string? FeedbackMessage { get; set; }

    [ObservableProperty]
    public partial string? DropTargetMessage { get; private set; }

    [ObservableProperty]
    public partial bool IsCreatingFolder { get; private set; }

    [ObservableProperty]
    public partial string NewFolderName { get; set; } = "New folder";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMore))]
    [NotifyPropertyChangedFor(nameof(TruncationMessage))]
    [NotifyPropertyChangedFor(nameof(SortScopeTooltip))]
    public partial bool IsTruncated { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameSortGlyph))]
    [NotifyPropertyChangedFor(nameof(SizeSortGlyph))]
    [NotifyPropertyChangedFor(nameof(ModifiedSortGlyph))]
    public partial FileBrowserSortColumn SortColumn { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameSortGlyph))]
    [NotifyPropertyChangedFor(nameof(SizeSortGlyph))]
    [NotifyPropertyChangedFor(nameof(ModifiedSortGlyph))]
    public partial bool SortDescending { get; private set; }

    public string NameSortGlyph => SortGlyph(FileBrowserSortColumn.Name);

    public string SizeSortGlyph => SortGlyph(FileBrowserSortColumn.Size);

    public string ModifiedSortGlyph => SortGlyph(FileBrowserSortColumn.Modified);

    /// <summary>Says out loud what most dual-pane cloud browsers get silently wrong: once a listing is
    /// truncated, the sort covers what is loaded and nothing else.</summary>
    public string SortScopeTooltip => IsTruncated
        ? $"Sorts only the {Items.Count:N0} rows loaded so far, not the whole prefix."
        : "Sorts the rows in this folder.";

    /// <summary>Never a total. S3 cannot cheaply count a prefix, so this says "of many" rather than
    /// inventing a number the provider did not give.</summary>
    public string TruncationMessage => IsTruncated
        ? $"{Items.Count:N0} of many shown. Narrow the prefix, or use the path box to go deeper — " +
            "this view does not load an entire bucket."
        : string.Empty;

    public bool HasMore => !IsTruncated && _continuationToken is not null;

    public bool CanGoBack => _backHistory.Count > 0;

    public bool CanGoForward => _forwardHistory.Count > 0;

    public async Task<bool> NavigateAsync(string path, CancellationToken cancellationToken = default)
    {
        if (Source is not { } source)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(path) || !source.IsValidPath(path))
        {
            ErrorMessage = $"'{path}' is not a path this pane can open.";
            PathText = CurrentPath;
            return false;
        }

        return await NavigateCoreAsync(path, addHistory: true, cancellationToken).ConfigureAwait(true);
    }

    [RelayCommand]
    public Task NavigatePathAsync(CancellationToken cancellationToken = default)
    {
        return NavigateAsync(PathText, cancellationToken);
    }

    public Task<bool> OpenAsync(FileBrowserItemViewModel item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.IsDirectory
            ? NavigateCoreAsync(item.Path, addHistory: true, cancellationToken)
            : Task.FromResult(false);
    }

    [RelayCommand]
    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        // Re-read on the user's own refresh rather than on every internal navigate: asking an
        // unresponsive optical or network drive whether it is ready is not free, and a drive appearing is
        // exactly the thing someone presses refresh for.
        ReloadRoots();
        return CurrentPath.Length == 0
            ? Task.CompletedTask
            : NavigateCoreAsync(CurrentPath, addHistory: false, cancellationToken);
    }

    [RelayCommand]
    public Task UpAsync(CancellationToken cancellationToken = default)
    {
        var parent = Source?.GetParent(CurrentPath);
        return parent is null
            ? Task.CompletedTask
            : NavigateCoreAsync(parent, addHistory: true, cancellationToken);
    }

    [RelayCommand]
    public async Task BackAsync(CancellationToken cancellationToken = default)
    {
        if (_backHistory.Count == 0)
        {
            return;
        }

        var target = _backHistory[^1];
        _backHistory.RemoveAt(_backHistory.Count - 1);
        _forwardHistory.Add(CurrentPath);
        if (!await NavigateCoreAsync(target, addHistory: false, cancellationToken).ConfigureAwait(true))
        {
            _forwardHistory.RemoveAt(_forwardHistory.Count - 1);
            _backHistory.Add(target);
        }

        NotifyHistoryChanged();
    }

    [RelayCommand]
    public async Task ForwardAsync(CancellationToken cancellationToken = default)
    {
        if (_forwardHistory.Count == 0)
        {
            return;
        }

        var target = _forwardHistory[^1];
        _forwardHistory.RemoveAt(_forwardHistory.Count - 1);
        _backHistory.Add(CurrentPath);
        if (!await NavigateCoreAsync(target, addHistory: false, cancellationToken).ConfigureAwait(true))
        {
            _backHistory.RemoveAt(_backHistory.Count - 1);
            _forwardHistory.Add(target);
        }

        NotifyHistoryChanged();
    }

    /// <summary>Appends the next page. Stops at the cap rather than at the end of the prefix.</summary>
    [RelayCommand]
    public async Task LoadMoreAsync(CancellationToken cancellationToken = default)
    {
        if (Source is not { } source || _continuationToken is null || IsTruncated)
        {
            return;
        }

        IsLoading = true;
        try
        {
            var page = await source.ListAsync(CurrentPath, ListOptions(_continuationToken), cancellationToken)
                .ConfigureAwait(true);
            if (page.IsFailure)
            {
                ErrorMessage = page.Failure.Message;
                return;
            }

            Append(page.Value);
            ApplySort();
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "Loading more rows was cancelled.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    public Task TransferAsync(CancellationToken cancellationToken = default)
    {
        return TransferHandler is null ? Task.CompletedTask : TransferHandler(cancellationToken);
    }

    public void SortBy(FileBrowserSortColumn column)
    {
        if (SortColumn == column)
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortColumn = column;
            SortDescending = false;
        }

        ApplySort();
    }

    public void SetSelection(IEnumerable<FileBrowserItemViewModel> selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        SelectedItems.Clear();
        foreach (var item in selection)
        {
            SelectedItems.Add(item);
        }
    }

    public FileBrowserItemViewModel? FindByPrefix(string prefix, int startAfter = -1)
    {
        if (string.IsNullOrEmpty(prefix) || Items.Count == 0)
        {
            return null;
        }

        for (var offset = 1; offset <= Items.Count; offset++)
        {
            var item = Items[(startAfter + offset + Items.Count) % Items.Count];
            if (item.Name.StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase))
            {
                return item;
            }
        }

        return null;
    }

    /// <summary>Where a drop would land: the folder under the pointer, or the folder being shown when the
    /// pointer is over a file or over empty space.</summary>
    public string DropTargetPath(FileBrowserItemViewModel? hoveredDirectory)
    {
        return hoveredDirectory is { IsDirectory: true } ? hoveredDirectory.Path : CurrentPath;
    }

    /// <summary>The verb comes from the drag payload rather than being fixed here, so a drop that will run
    /// a download says so instead of the pane-to-pane "Drop into".</summary>
    public void SetDropTarget(FileBrowserItemViewModel? hoveredDirectory, string verb = "Drop into")
    {
        DropTargetMessage = $"{verb} {DropTargetPath(hoveredDirectory)}";
    }

    public void ClearDropTarget()
    {
        DropTargetMessage = null;
    }

    public void BeginCreateFolder()
    {
        NewFolderName = "New folder";
        IsCreatingFolder = true;
        ErrorMessage = null;
    }

    public void CancelCreateFolder()
    {
        IsCreatingFolder = false;
        NewFolderName = "New folder";
    }

    public async Task<bool> CommitCreateFolderAsync(CancellationToken cancellationToken = default)
    {
        if (Source is not { } source)
        {
            return false;
        }

        var name = NewFolderName.Trim();
        if (!IsValidName(name))
        {
            ErrorMessage = "Enter a valid folder name without path separators.";
            return false;
        }

        var created = await source.CreateFolderAsync(source.Combine(CurrentPath, name), cancellationToken)
            .ConfigureAwait(true);
        if (created.IsFailure)
        {
            ErrorMessage = created.Failure.Message;
            return false;
        }

        IsCreatingFolder = false;
        FeedbackMessage = $"Created folder '{name}'.";
        _ = await NavigateCoreAsync(CurrentPath, addHistory: false, CancellationToken.None).ConfigureAwait(true);
        return true;
    }

    public void BeginRename(FileBrowserItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!SupportsRename)
        {
            ErrorMessage = "Object storage has no rename. Copy the object to the new key and delete the old one.";
            return;
        }

        foreach (var other in Items)
        {
            other.IsRenaming = false;
        }

        item.RenameText = item.Name;
        item.IsRenaming = true;
        ErrorMessage = null;
    }

    public static void CancelRename(FileBrowserItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        item.RenameText = item.Name;
        item.IsRenaming = false;
    }

    public async Task<bool> CommitRenameAsync(
        FileBrowserItemViewModel item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (Source is not { } source || !SupportsRename)
        {
            return false;
        }

        var name = item.RenameText.Trim();
        if (!IsValidName(name))
        {
            ErrorMessage = "Enter a valid name without path separators.";
            return false;
        }

        if (string.Equals(item.Name, name, StringComparison.Ordinal))
        {
            item.IsRenaming = false;
            return true;
        }

        var renamed = await source.RenameAsync(item.Path, name, cancellationToken).ConfigureAwait(true);
        if (renamed.IsFailure)
        {
            ErrorMessage = renamed.Failure.Message;
            return false;
        }

        item.IsRenaming = false;
        FeedbackMessage = $"Renamed '{item.Name}' to '{name}'.";
        _ = await NavigateCoreAsync(CurrentPath, addHistory: false, CancellationToken.None).ConfigureAwait(true);
        return true;
    }

    /// <summary>Counts what would go, confirms, then deletes. The count is what makes the confirmation
    /// honest: "delete 1 item" and "delete 41,000 items" are the same gesture.</summary>
    public async Task<bool> DeleteAsync(
        IEnumerable<FileBrowserItemViewModel> selection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (Source is not { } source)
        {
            return false;
        }

        var roots = selection.Distinct().ToArray();
        if (roots.Length == 0)
        {
            return false;
        }

        int count;
        try
        {
            count = await CountAsync(roots, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            FeedbackMessage = "Delete cancelled.";
            return false;
        }

        var confirmed = await _confirmation.ConfirmAsync(
            count == 1 ? "Delete item?" : "Delete items?",
            $"Permanently delete {count:N0} item(s)? This includes everything below the selection.",
            "Delete",
            cancellationToken).ConfigureAwait(true);
        if (!confirmed)
        {
            FeedbackMessage = "Delete cancelled.";
            return false;
        }

        var failures = new List<string>();
        foreach (var root in roots)
        {
            var deleted = await source.DeleteAsync(root.Entry, cancellationToken).ConfigureAwait(true);
            if (deleted.IsFailure)
            {
                failures.Add($"{root.Name}: {deleted.Failure.Message}");
            }
        }

        _ = await NavigateCoreAsync(CurrentPath, addHistory: false, CancellationToken.None).ConfigureAwait(true);
        if (failures.Count > 0)
        {
            ErrorMessage = $"Deleted {roots.Length - failures.Count} of {roots.Length}. " +
                string.Join(" ", failures);
            return false;
        }

        FeedbackMessage = $"Deleted {count:N0} item(s).";
        return true;
    }

    /// <summary>Every entry the selection expands to, so a folder transfer can be counted and confirmed
    /// before a single byte moves.</summary>
    public async Task<int> CountAsync(
        IEnumerable<FileBrowserItemViewModel> selection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (Source is not { } source)
        {
            return 0;
        }

        var count = 0;
        foreach (var item in selection)
        {
            await foreach (var _ in source.EnumerateRecursiveAsync(item.Entry, cancellationToken)
                .ConfigureAwait(true))
            {
                count++;
            }
        }

        return count;
    }

    public async Task<bool> AttachAsync(IFileBrowserSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        Source = source;
        _backHistory.Clear();
        _forwardHistory.Clear();
        ReloadRoots();
        NotifyHistoryChanged();

        var remembered = _folderMemory is null
            ? null
            : await _folderMemory.RecallAsync(cancellationToken).ConfigureAwait(true);

        // Where the pane was last pointed wins over the source's own root, and the short-circuit is the
        // fallback: a remembered path that no longer lists — a deleted folder, an ejected drive — opens the
        // root instead of greeting the user with an error banner, whose message the second navigate clears.
        return (remembered is not null &&
                source.IsValidPath(remembered) &&
                await NavigateCoreAsync(remembered, addHistory: false, cancellationToken).ConfigureAwait(true)) ||
            await NavigateCoreAsync(source.RootPath, addHistory: false, cancellationToken).ConfigureAwait(true);
    }

    public void Detach()
    {
        Source = null;
        Items.Clear();
        Roots.Clear();
        OnPropertyChanged(nameof(HasRoots));
        SelectedItems.Clear();
        Breadcrumbs.Clear();
        _backHistory.Clear();
        _forwardHistory.Clear();
        _continuationToken = null;
        IsTruncated = false;
        CurrentPath = string.Empty;
        PathText = string.Empty;
        NotifyHistoryChanged();
    }

    partial void OnSelectedRootChanged(FileBrowserCrumb? value)
    {
        if (_syncingRoot || value is null || Source is null)
        {
            return;
        }

        if (!string.Equals(value.Path, CurrentPath, StringComparison.OrdinalIgnoreCase))
        {
            _ = NavigateAsync(value.Path);
        }
    }

    partial void OnFilterTextChanged(string value)
    {
        _ = value;
        if (Source is not null && CurrentPath.Length > 0)
        {
            // Server-side narrowing: the listing is re-requested with a longer prefix rather than the
            // loaded rows being filtered, which is the only kind of narrowing either provider can do.
            _ = NavigateCoreAsync(CurrentPath, addHistory: false, CancellationToken.None);
        }
    }

    partial void OnShowHiddenEntriesChanged(bool value)
    {
        _ = value;
        if (Source is not null && CurrentPath.Length > 0)
        {
            _ = RefreshAsync();
        }
    }

    partial void OnIsLoadingChanged(bool value)
    {
        CancelBusyIndicatorDelay();
        if (!value)
        {
            IsBusyIndicatorVisible = false;
            return;
        }

        if (BusyIndicatorDelay <= TimeSpan.Zero)
        {
            IsBusyIndicatorVisible = true;
            return;
        }

        var pending = new CancellationTokenSource();
        _busyIndicatorDelay = pending;
        _ = ShowBusyIndicatorWhenSlowAsync(pending.Token);
    }

    private static bool IsValidName(string name)
    {
        return name.Length > 0 && name is not ("." or "..") && !name.Contains('/') && !name.Contains('\\');
    }

    private string SortGlyph(FileBrowserSortColumn column)
    {
        return SortColumn != column ? string.Empty : SortDescending ? "▼" : "▲";
    }

    private FileBrowserListOptions ListOptions(string? continuationToken)
    {
        return new FileBrowserListOptions
        {
            ContinuationToken = continuationToken,
            PageSize = PageSize,
            ShowHidden = ShowHiddenEntries,
            NamePrefix = string.IsNullOrWhiteSpace(FilterText) ? null : FilterText.Trim(),
        };
    }

    private async Task<bool> NavigateCoreAsync(
        string path,
        bool addHistory,
        CancellationToken cancellationToken)
    {
        if (Source is not { } source)
        {
            return false;
        }

        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var page = await source.ListAsync(path, ListOptions(null), cancellationToken).ConfigureAwait(true);
            if (page.IsFailure)
            {
                ErrorMessage = page.Failure.Message;
                PathText = CurrentPath;
                return false;
            }

            if (addHistory && !string.Equals(CurrentPath, path, StringComparison.Ordinal))
            {
                _backHistory.Add(CurrentPath);
                _forwardHistory.Clear();
            }

            CurrentPath = path;
            PathText = path;
            Items.Clear();
            SelectedItems.Clear();
            _continuationToken = null;
            _pagesLoaded = 0;
            IsTruncated = false;
            Append(page.Value);
            ApplySort();
            RebuildBreadcrumbs(source, path);
            SyncSelectedRoot(path);
            NotifyHistoryChanged();
            Remember(path);
            return true;
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "The folder load was cancelled.";
            return false;
        }
        catch (Exception exception)
        {
            ErrorMessage = $"The folder could not be loaded: {exception.Message}";
            return false;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Append(FileBrowserPage page)
    {
        foreach (var entry in page.Entries)
        {
            Items.Add(new FileBrowserItemViewModel(entry, TransferLabel));
        }

        _pagesLoaded++;
        _continuationToken = page.ContinuationToken;
        IsTruncated = _continuationToken is not null &&
            (_pagesLoaded >= MaxPages || Items.Count >= MaxRows);
        if (page.Warning is { Length: > 0 } warning)
        {
            // A partial page is not a failure: the rows that were readable stay on screen and the reason
            // the rest are missing is said rather than left as an unexplained short listing.
            ErrorMessage = warning;
        }

        OnPropertyChanged(nameof(HasMore));
        OnPropertyChanged(nameof(TruncationMessage));
        OnPropertyChanged(nameof(SortScopeTooltip));
    }

    /// <summary>Sorted as a plain list before anything reaches the observable collection. Clearing and
    /// re-adding 10,000 rows is 20,000 change notifications on the UI thread, which virtualization does
    /// not help with.</summary>
    private void ApplySort()
    {
        var sorted = Items
            .Select((item, index) => (item, index))
            .OrderBy(pair => pair.item.IsDirectory ? 0 : 1)
            .ThenBy(pair => pair, Comparer(SortColumn, SortDescending))
            .ThenBy(pair => pair.item.Name, StringComparer.Ordinal)
            .ThenBy(pair => pair.index)
            .Select(pair => pair.item)
            .ToArray();
        Items.Clear();
        foreach (var item in sorted)
        {
            Items.Add(item);
        }
    }

    private static Comparer<(FileBrowserItemViewModel Item, int Index)> Comparer(
        FileBrowserSortColumn column,
        bool descending)
    {
        Comparison<(FileBrowserItemViewModel Item, int Index)> comparison = column switch
        {
            FileBrowserSortColumn.Size => (left, right) => left.Item.Size.CompareTo(right.Item.Size),
            FileBrowserSortColumn.Modified => (left, right) => Nullable.Compare(
                left.Item.Modified,
                right.Item.Modified),
            FileBrowserSortColumn.Name => (left, right) => string.Compare(
                left.Item.Name,
                right.Item.Name,
                StringComparison.OrdinalIgnoreCase),
            _ => throw new ArgumentOutOfRangeException(nameof(column)),
        };
        return System.Collections.Generic.Comparer<(FileBrowserItemViewModel Item, int Index)>.Create(
            descending ? (left, right) => comparison(right, left) : comparison);
    }

    private void RebuildBreadcrumbs(IFileBrowserSource source, string path)
    {
        Breadcrumbs.Clear();
        foreach (var crumb in source.GetBreadcrumbs(path))
        {
            Breadcrumbs.Add(crumb);
        }
    }

    private void ReloadRoots()
    {
        if (Source is not { } source)
        {
            return;
        }

        var roots = source.GetRoots();
        Roots.Clear();
        foreach (var root in roots)
        {
            Roots.Add(root);
        }

        OnPropertyChanged(nameof(HasRoots));
        SyncSelectedRoot(CurrentPath);
    }

    /// <summary>Points the picker at the root the current path is under, without that write turning round
    /// and navigating back to the root itself.</summary>
    private void SyncSelectedRoot(string path)
    {
        if (Roots.Count == 0 || path.Length == 0)
        {
            return;
        }

        var match = Roots
            .Where(root => path.StartsWith(root.Path, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(root => root.Path.Length)
            .FirstOrDefault();
        if (match is null || ReferenceEquals(match, SelectedRoot))
        {
            return;
        }

        _syncingRoot = true;
        try
        {
            SelectedRoot = match;
        }
        finally
        {
            _syncingRoot = false;
        }
    }

    /// <summary>Written on arrival rather than on the way out, so a crash or a kill still leaves the folder
    /// remembered. Fire and forget, and deliberately silent: where the pane is pointed is a convenience,
    /// and a failed settings write must not turn a folder that loaded fine into an error banner.</summary>
    private void Remember(string path)
    {
        if (_folderMemory is null || string.Equals(_rememberedPath, path, StringComparison.Ordinal))
        {
            return;
        }

        _rememberedPath = path;
        _ = RememberAsync(_folderMemory, path);
    }

    private static async Task RememberAsync(ILocalFolderMemory memory, string path)
    {
        try
        {
            await memory.RememberAsync(path, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Nothing the user can act on, and nothing worth taking the pane's error banner for.
        }
    }

    private void NotifyHistoryChanged()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }

    private void CancelBusyIndicatorDelay()
    {
        var pending = _busyIndicatorDelay;
        _busyIndicatorDelay = null;
        pending?.Cancel();
        pending?.Dispose();
    }

    private async Task ShowBusyIndicatorWhenSlowAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(BusyIndicatorDelay, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (IsLoading)
        {
            IsBusyIndicatorVisible = true;
        }
    }
}
