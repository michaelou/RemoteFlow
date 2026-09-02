using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Sftp;
using RemoteFlow.Application.Abstractions.Storage;
using RemoteFlow.Application.Queries;
using RemoteFlow.Application.Services;
using RemoteFlow.Domain.Enums;
using RemoteFlow.UI.Services;
using RemoteFlow.UI.ViewModels.Storage;
using RemoteFlow.UI.ViewModels.Transfers;

namespace RemoteFlow.UI.ViewModels.Sftp;

public enum SftpSortColumn
{
    Name = 0,
    Size = 1,
    Modified = 2,
    Permissions = 3,
    Owner = 4,
}

public sealed record SftpBreadcrumb(string Label, string Path);

/// <summary>One entry in the workspace's connection picker. Only connections that can speak SFTP get one.</summary>
public sealed record SftpConnectionChoice(Guid Id, string Name, string Endpoint);

public sealed partial class SftpItemViewModel(RemoteFileInfo file) : ObservableObject
{
    public RemoteFileInfo File { get; } = file;

    public string Name => File.Name;

    public string FullPath => File.FullPath;

    public long Size => File.Size;

    public string SizeText => File.IsDirectory ? "—" : FormatSize(File.Size);

    public DateTimeOffset Modified => File.ModifiedTime;

    public string Permissions => SftpPath.FormatMode(File.Mode);

    public string Owner => string.IsNullOrEmpty(File.Group) ? File.Owner : $"{File.Owner}:{File.Group}";

    public bool IsDirectory => File.IsDirectory;

    public bool IsSymlink => File.IsSymlink;

    [ObservableProperty]
    public partial bool IsRenaming { get; set; }

    [ObservableProperty]
    public partial string RenameText { get; set; } = file.Name;

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }
}

public sealed record SftpPropertiesViewModel(
    string Name,
    string FullPath,
    string Size,
    DateTimeOffset Modified,
    string OctalMode,
    string SymbolicMode,
    string Owner,
    string Group,
    string? SymlinkTarget)
{
    public bool HasSymlinkTarget => !string.IsNullOrWhiteSpace(SymlinkTarget);

    public string OwnerAndGroup => $"{Owner} / {Group}";
}

public sealed partial class SftpWorkspaceViewModel : PageViewModel, IAsyncDisposable
{
    private readonly ISftpWorkspaceSessionFactory _sessions;
    private readonly IFilePickerService _filePicker;
    private readonly IConfirmationDialogService _confirmation;
    private readonly IClipboardService _clipboard;
    private readonly IRemoteEditServiceFactory? _remoteEditFactory;
    private readonly IUiDispatcher? _dispatcher;
    private readonly IConnectionQueryService? _connectionQueries;
    private readonly LocalFileBrowserSource _localFiles = new();
    private readonly List<string> _backHistory = [];
    private readonly List<string> _forwardHistory = [];
    private Guid? _attachedConnectionId;
    private SftpWorkspaceSession? _session;
    private TransferEngine? _transfers;
    private IRemoteEditService? _remoteEdits;
    private CancellationTokenSource? _operationCancellation;
    private CancellationTokenSource? _busyIndicatorDelay;

    /// <summary>Spelled out rather than a primary constructor because the local pane's Upload button is an
    /// instance method, and a property initializer cannot reach one.</summary>
    public SftpWorkspaceViewModel(
        ISftpWorkspaceSessionFactory sessions,
        IFilePickerService filePicker,
        IConfirmationDialogService confirmation,
        IClipboardService clipboard,
        IRemoteEditServiceFactory? remoteEditFactory = null,
        TransfersPageViewModel? transferManager = null,
        IUiDispatcher? dispatcher = null,
        IConnectionQueryService? connectionQueries = null,
        ILocalFolderMemory? folderMemory = null) : base("SFTP")
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _filePicker = filePicker ?? throw new ArgumentNullException(nameof(filePicker));
        _confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation));
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _remoteEditFactory = remoteEditFactory;
        Transfers = transferManager;
        _dispatcher = dispatcher;
        _connectionQueries = connectionQueries;
        Local = new FileBrowserPaneViewModel("local folder", "Upload", confirmation, _localFiles, folderMemory)
        {
            TransferHandler = UploadSelectionToAsync,
        };
    }

    public ObservableCollection<SftpItemViewModel> Items { get; } = [];

    public ObservableCollection<SftpItemViewModel> SelectedItems { get; } = [];

    public ObservableCollection<SftpBreadcrumb> Breadcrumbs { get; } = [];

    /// <summary>The local half of the page: the same <c>FileBrowserPane</c> the Storage page puts on its
    /// left, over the same <see cref="LocalFileBrowserSource"/> and sharing the same memory of where it was
    /// last pointed, so the two pages open in the same folder.
    ///
    /// Its Upload button and row menu send the local selection to whatever folder the remote list is
    /// showing, which is why the pane is constructed in the body rather than in an initializer.</summary>
    public FileBrowserPaneViewModel Local { get; }

    /// <summary>The application-wide transfer queue shown at the foot of the page — the injected singleton,
    /// the same one the Transfers page and the Storage page bind, not a second queue. Null only in a test
    /// that did not ask for one, which is why every call site checks.</summary>
    public TransfersPageViewModel? Transfers { get; }

    public bool HasTransferQueue => Transfers is not null;

    /// <summary>The SFTP-capable connections offered by the picker, so the workspace can be opened from
    /// here instead of only by connecting from the explorer.</summary>
    public ObservableCollection<SftpConnectionChoice> AvailableConnections { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectSelectedCommand))]
    public partial SftpConnectionChoice? SelectedConnection { get; set; }

    public bool HasConnectionChoices => AvailableConnections.Count > 0;

    public string NoConnectionMessage => HasConnectionChoices
        ? "Choose a connection above and select Connect to browse its files."
        : "No SSH or SFTP connections are saved yet. Create one on the Connections page first.";

    [ObservableProperty]
    public partial string ConnectionTitle { get; private set; } = "SFTP";

    [ObservableProperty]
    public partial string CurrentPath { get; private set; } = "/";

    [ObservableProperty]
    public partial string PathText { get; set; } = "/";

    [ObservableProperty]
    public partial bool ShowHiddenFiles { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    /// <summary>
    /// Gets or sets how long a load must run before the status bar shows its progress indicator.
    /// Loads that finish sooner never show it, so quick browsing does not flicker.
    /// </summary>
    public TimeSpan BusyIndicatorDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    [ObservableProperty]
    public partial bool IsBusyIndicatorVisible { get; private set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    [ObservableProperty]
    public partial string? FeedbackMessage { get; private set; }

    [ObservableProperty]
    public partial string? DropTargetMessage { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameSortGlyph))]
    [NotifyPropertyChangedFor(nameof(SizeSortGlyph))]
    [NotifyPropertyChangedFor(nameof(ModifiedSortGlyph))]
    [NotifyPropertyChangedFor(nameof(PermissionsSortGlyph))]
    [NotifyPropertyChangedFor(nameof(OwnerSortGlyph))]
    public partial SftpSortColumn SortColumn { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameSortGlyph))]
    [NotifyPropertyChangedFor(nameof(SizeSortGlyph))]
    [NotifyPropertyChangedFor(nameof(ModifiedSortGlyph))]
    [NotifyPropertyChangedFor(nameof(PermissionsSortGlyph))]
    [NotifyPropertyChangedFor(nameof(OwnerSortGlyph))]
    public partial bool SortDescending { get; private set; }

    public string NameSortGlyph => SortGlyph(SftpSortColumn.Name);

    public string SizeSortGlyph => SortGlyph(SftpSortColumn.Size);

    public string ModifiedSortGlyph => SortGlyph(SftpSortColumn.Modified);

    public string PermissionsSortGlyph => SortGlyph(SftpSortColumn.Permissions);

    public string OwnerSortGlyph => SortGlyph(SftpSortColumn.Owner);

    public bool CanGoBack => _backHistory.Count > 0;

    public bool CanGoForward => _forwardHistory.Count > 0;

    public bool IsConnected => _session is not null;

    public bool CanCancelOperation => IsMutating;

    public int ActiveRemoteEditCount => _remoteEdits?.ActiveCount ?? 0;

    public bool HasActiveRemoteEdits => ActiveRemoteEditCount > 0;

    public string RemoteEditIndicator => ActiveRemoteEditCount == 1
        ? "Editing 1 remote file"
        : $"Editing {ActiveRemoteEditCount} remote files";

    [ObservableProperty]
    public partial bool IsMutating { get; private set; }

    [ObservableProperty]
    public partial bool IsCreatingFolder { get; private set; }

    [ObservableProperty]
    public partial string NewFolderName { get; set; } = "New folder";

    /// <summary>Reloads the picker from the saved connections, keeping whatever was selected if it survived.</summary>
    [RelayCommand]
    public async Task LoadConnectionsAsync(CancellationToken cancellationToken = default)
    {
        if (_connectionQueries is null)
        {
            return;
        }
        try
        {
            var items = await _connectionQueries.QueryAsync(
                new ConnectionFilter
                {
                    Protocols = [ProtocolType.Ssh, ProtocolType.Sftp],
                    SortBy = ConnectionSortBy.Name,
                },
                cancellationToken).ConfigureAwait(true);
            var previous = SelectedConnection?.Id ?? _attachedConnectionId;
            AvailableConnections.Clear();
            foreach (var item in items)
            {
                AvailableConnections.Add(new SftpConnectionChoice(item.Id, item.Name, $"{item.Host}:{item.Port}"));
            }
            SelectedConnection = AvailableConnections.FirstOrDefault(choice => choice.Id == previous);
            NotifyConnectionChoicesChanged();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ErrorMessage = $"The connection list could not be loaded: {exception.Message}";
        }
    }

    /// <summary>Opens the connection chosen in the picker, replacing whatever session is already attached.</summary>
    [RelayCommand(CanExecute = nameof(CanConnectSelected))]
    public Task ConnectSelectedAsync(CancellationToken cancellationToken = default)
    {
        return SelectedConnection is { } choice
            ? AttachAsync(choice.Id, cancellationToken)
            : Task.CompletedTask;
    }

    public async Task AttachAsync(Guid connectionId, CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var next = await _sessions.OpenAsync(connectionId, cancellationToken).ConfigureAwait(true);
            if (!await DisposeSessionAsync().ConfigureAwait(true))
            {
                await next.DisposeAsync().ConfigureAwait(true);
                ErrorMessage = "The existing SFTP session has an unsaved remote edit.";
                return;
            }
            _session = next;
            _transfers = new TransferEngine(next.Sftp);
            _remoteEdits = _remoteEditFactory?.Create(next.Sftp, Guid.NewGuid());
            if (_remoteEdits is { } activeRemoteEdits)
            {
                activeRemoteEdits.ActiveEditsChanged += OnActiveEditsChanged;
                activeRemoteEdits.UploadCompleted += OnRemoteEditUploadCompleted;
            }
            _attachedConnectionId = connectionId;
            ConnectionTitle = next.Definition.Name;
            SyncPickerWithSession(next.Definition.Id, next.Definition.Name, $"{next.Definition.Host}:{next.Definition.Port}");
            ShowHiddenFiles = next.Definition.Sftp.ShowHiddenFiles;
            _backHistory.Clear();
            _forwardHistory.Clear();

            var requested = next.Definition.Sftp.RemoteRootPath ?? "~";
            var realPath = await next.Sftp.GetRealPathAsync(requested, cancellationToken).ConfigureAwait(true);
            if (realPath.IsFailure)
            {
                ErrorMessage = realPath.Failure.Message;
                return;
            }
            _ = await NavigateCoreAsync(realPath.Value, addHistory: false, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ErrorMessage = "The SFTP connection was cancelled.";
        }
        catch (Exception exception)
        {
            ErrorMessage = $"The SFTP workspace could not be opened: {exception.Message}";
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsConnected));
        }
    }

    [RelayCommand]
    public Task NavigatePathAsync(CancellationToken cancellationToken = default)
    {
        return NavigateAsync(PathText, cancellationToken);
    }

    public async Task NavigateAsync(string path, CancellationToken cancellationToken = default)
    {
        if (_session is null)
        {
            return;
        }
        var trimmed = path?.TrimStart();
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed[0] != '/')
        {
            ErrorMessage = "Enter an absolute remote path beginning with '/'.";
            return;
        }
        _ = await NavigateCoreAsync(trimmed, addHistory: true, cancellationToken).ConfigureAwait(true);
    }

    public Task OpenAsync(SftpItemViewModel item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.IsDirectory
            ? NavigateCoreAsync(item.FullPath, addHistory: true, cancellationToken)
            : Task.CompletedTask;
    }

    [RelayCommand]
    public Task UpAsync(CancellationToken cancellationToken = default)
    {
        if (CurrentPath == "/")
        {
            return Task.CompletedTask;
        }
        var index = CurrentPath.LastIndexOf('/');
        var parent = index <= 0 ? "/" : CurrentPath[..index];
        return NavigateCoreAsync(parent, addHistory: true, cancellationToken);
    }

    [RelayCommand]
    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        return NavigateCoreAsync(CurrentPath, addHistory: false, cancellationToken);
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

    /// <summary>
    /// The arrow shown beside a column heading. Only the sorted column has one, and the direction is a
    /// shape rather than a shade, so which column is sorted survives being read in greyscale.
    /// </summary>
    private string SortGlyph(SftpSortColumn column)
    {
        return SortColumn != column ? string.Empty : SortDescending ? "▼" : "▲";
    }

    public void SortBy(SftpSortColumn column)
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

    public void SetSelection(IEnumerable<SftpItemViewModel> selection)
    {
        SelectedItems.Clear();
        foreach (var item in selection)
        {
            SelectedItems.Add(item);
        }
    }

    public SftpItemViewModel? FindByPrefix(string prefix, int startAfter = -1)
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

    public void SetDropTarget(SftpItemViewModel? hoveredDirectory)
    {
        var target = hoveredDirectory is { IsDirectory: true } ? hoveredDirectory.FullPath : CurrentPath;
        DropTargetMessage = $"Upload to {target}";
    }

    public void ClearDropTarget()
    {
        DropTargetMessage = null;
    }

    [RelayCommand]
    public async Task ChooseAndUploadAsync(CancellationToken cancellationToken = default)
    {
        var paths = await _filePicker.PickUploadPathsAsync(cancellationToken).ConfigureAwait(true);
        await UploadAsync(paths, CurrentPath, cancellationToken).ConfigureAwait(true);
    }

    public async Task UploadAsync(
        IEnumerable<string> localPaths,
        string targetDirectory,
        CancellationToken cancellationToken = default)
    {
        if (_transfers is null)
        {
            return;
        }
        ErrorMessage = null;
        var paths = localPaths.ToArray();
        if (Transfers is null)
        {
            foreach (var localPath in paths)
            {
                var name = Path.GetFileName(localPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                var result = await _transfers.UploadAsync(
                    localPath,
                    SftpPath.Combine(targetDirectory, name),
                    cancellationToken: cancellationToken).ConfigureAwait(true);
                if (!result.IsSuccess)
                {
                    ErrorMessage = DescribeTransferFailure(result, "upload");
                    break;
                }
            }
        }
        else
        {
            var engine = _transfers;
            var queued = new List<TransferItemViewModel>();
            foreach (var localPath in paths)
            {
                var name = Path.GetFileName(localPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                var destination = SftpPath.Combine(targetDirectory, name);
                queued.Add(await Transfers.QueueAsync(new TransferQueueRequest(
                    TransferDirection.Upload,
                    localPath,
                    destination,
                    (progress, token) => engine.UploadAsync(localPath, destination, progress, token)),
                    cancellationToken).ConfigureAwait(true));
            }
            var results = await Task.WhenAll(queued.Select(item => item.Completion)).ConfigureAwait(true);
            var failed = results.FirstOrDefault(result => !result.IsSuccess);
            if (failed is not null)
            {
                ErrorMessage = DescribeTransferFailure(failed, "upload");
            }
        }
        if (ErrorMessage is null)
        {
            FeedbackMessage = $"Upload to {SftpPath.Normalize(targetDirectory)} completed.";
            await RefreshAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    public async Task ChooseAndDownloadAsync(CancellationToken cancellationToken = default)
    {
        if (_session is null)
        {
            return;
        }
        var folder = await _filePicker.PickDownloadFolderAsync(
            _session.Definition.Sftp.LocalDownloadPath,
            cancellationToken).ConfigureAwait(true);
        if (folder is not null)
        {
            _ = await DownloadAsync(SelectedItems, folder, cancellationToken).ConfigureAwait(true);
        }
    }

    public async Task<IReadOnlyList<string>> DownloadAsync(
        IEnumerable<SftpItemViewModel> items,
        string localDirectory,
        CancellationToken cancellationToken = default)
    {
        if (_transfers is null)
        {
            return [];
        }
        var selected = items.ToArray();
        var completed = new List<string>();
        if (Transfers is null)
        {
            foreach (var item in selected)
            {
                var localPath = Path.Combine(localDirectory, item.Name);
                var result = await _transfers.DownloadAsync(
                    item.FullPath,
                    localPath,
                    cancellationToken: cancellationToken).ConfigureAwait(true);
                if (!result.IsSuccess)
                {
                    ErrorMessage = DescribeTransferFailure(result, "download");
                    break;
                }
                completed.Add(localPath);
            }
        }
        else
        {
            var engine = _transfers;
            var queued = new List<(string Path, TransferItemViewModel Item)>();
            foreach (var item in selected)
            {
                var localPath = Path.Combine(localDirectory, item.Name);
                var managed = await Transfers.QueueAsync(new TransferQueueRequest(
                    TransferDirection.Download,
                    item.FullPath,
                    localPath,
                    (progress, token) => engine.DownloadAsync(item.FullPath, localPath, progress, token)),
                    cancellationToken).ConfigureAwait(true);
                queued.Add((localPath, managed));
            }
            foreach (var queuedItem in queued)
            {
                var result = await queuedItem.Item.Completion.ConfigureAwait(true);
                if (result.IsSuccess)
                {
                    completed.Add(queuedItem.Path);
                }
                else
                {
                    ErrorMessage ??= DescribeTransferFailure(result, "download");
                }
            }
        }
        if (completed.Count > 0 && ErrorMessage is null)
        {
            FeedbackMessage = $"Downloaded {completed.Count} item(s).";
        }
        return completed;
    }

    /// <summary>Opens the local pane where it was last left, or at the home directory the first time. Called
    /// when the page is shown, so the left half is usable before a connection is attached.</summary>
    public Task InitializeLocalAsync(CancellationToken cancellationToken = default)
    {
        return Local.CurrentPath.Length == 0
            ? Local.AttachAsync(_localFiles, cancellationToken)
            : Task.CompletedTask;
    }

    /// <summary>The local pane's Upload button and its row menu: the local selection into the folder the
    /// remote list is showing.</summary>
    public Task UploadSelectionAsync(CancellationToken cancellationToken = default)
    {
        return UploadSelectionToAsync(null, cancellationToken);
    }

    /// <summary>The same upload, into a named remote directory. <paramref name="targetDirectory"/> is null
    /// for the button and the row menu, which have no pointer position and mean "the folder the remote list
    /// is showing".</summary>
    public async Task UploadSelectionToAsync(
        string? targetDirectory,
        CancellationToken cancellationToken = default)
    {
        if (_session is null)
        {
            ErrorMessage = "Connect to a server first.";
            return;
        }

        var paths = Local.SelectedItems.Select(item => item.Path).ToArray();
        if (paths.Length == 0)
        {
            Local.FeedbackMessage = "Select something to upload first.";
            return;
        }

        await UploadAsync(paths, targetDirectory ?? CurrentPath, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>The remote list's Download button and its row menu: the remote selection into the folder the
    /// local pane is showing, which is then refreshed so the arrivals appear.</summary>
    [RelayCommand]
    public Task DownloadSelectionAsync(CancellationToken cancellationToken = default)
    {
        return DownloadToLocalPaneAsync(SelectedItems, cancellationToken);
    }

    public async Task DownloadToLocalPaneAsync(
        IEnumerable<SftpItemViewModel> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        var selected = items.ToArray();
        if (selected.Length == 0)
        {
            FeedbackMessage = "Select something to download first.";
            return;
        }

        if (Local.CurrentPath.Length == 0)
        {
            ErrorMessage = "The local pane has no folder open.";
            return;
        }

        _ = await DownloadAsync(selected, Local.CurrentPath, cancellationToken).ConfigureAwait(true);
        await Local.RefreshAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Finishes a drag that started in the remote list and landed on the local pane.
    ///
    /// The rows were already downloaded once, into a staging directory, to build the drag's
    /// operating-system file payload — see <see cref="PrepareDragOutAsync"/> and ADR-0013, which requires
    /// every advertised path to exist for the whole drop. Finishing the drop is therefore a move, not a
    /// second download: one transfer of a 4 GB file rather than two. The staging directory goes with them,
    /// which makes this the one path on which it is ever cleaned up.</summary>
    public async Task<int> CompleteDragToLocalAsync(
        string stagingDirectory,
        IReadOnlyList<string> stagedPaths,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        ArgumentNullException.ThrowIfNull(stagedPaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        var moved = 0;
        var failures = new List<string>();
        foreach (var staged in stagedPaths)
        {
            var result = await _localFiles.MoveIntoAsync(staged, destinationDirectory, cancellationToken)
                .ConfigureAwait(true);
            if (result.IsSuccess)
            {
                moved++;
            }
            else
            {
                failures.Add($"{Path.GetFileName(staged)}: {result.Failure.Message}");
            }
        }

        DiscardStaging(stagingDirectory);
        if (failures.Count > 0)
        {
            ErrorMessage = $"Moved {moved} of {stagedPaths.Count} into {destinationDirectory}. " +
                string.Join(" ", failures);
        }
        else
        {
            FeedbackMessage = $"Downloaded {moved} item(s) to {destinationDirectory}.";
        }

        return moved;
    }

    public Task<IReadOnlyList<string>> PrepareDragOutAsync(
        IEnumerable<SftpItemViewModel> items,
        string stagingDirectory,
        CancellationToken cancellationToken = default)
    {
        _ = Directory.CreateDirectory(stagingDirectory);
        return DownloadAsync(items, stagingDirectory, cancellationToken);
    }

    public void BeginRename(SftpItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        foreach (var other in Items)
        {
            other.IsRenaming = false;
        }
        item.RenameText = item.Name;
        item.IsRenaming = true;
        ErrorMessage = null;
    }

    public static void CancelRename(SftpItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        item.RenameText = item.Name;
        item.IsRenaming = false;
    }

    public async Task<bool> CommitRenameAsync(
        SftpItemViewModel item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (_session is null)
        {
            return false;
        }
        var name = item.RenameText.Trim();
        if (name.Length == 0 || name is "." or ".." || name.Contains('/') || name.Contains('\\'))
        {
            ErrorMessage = "Enter a valid name without path separators.";
            return false;
        }
        if (Items.Any(other => !ReferenceEquals(other, item) &&
            string.Equals(other.Name, name, StringComparison.Ordinal)))
        {
            ErrorMessage = $"An item named '{name}' already exists in this folder.";
            return false;
        }
        if (string.Equals(item.Name, name, StringComparison.Ordinal))
        {
            item.IsRenaming = false;
            return true;
        }

        using var operation = BeginOperation(cancellationToken);
        try
        {
            var result = await _session.Sftp.RenameAsync(
                item.FullPath,
                SftpPath.Combine(CurrentPath, name),
                operation.Token).ConfigureAwait(true);
            if (result.IsFailure)
            {
                ErrorMessage = result.Failure.Message;
                return false;
            }
            item.IsRenaming = false;
            FeedbackMessage = $"Renamed '{item.Name}' to '{name}'.";
            _ = await NavigateCoreAsync(CurrentPath, addHistory: false, CancellationToken.None).ConfigureAwait(true);
            return true;
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "The rename was cancelled.";
            return false;
        }
        finally
        {
            EndOperation();
        }
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
        if (_session is null)
        {
            return false;
        }
        var name = NewFolderName.Trim();
        if (name.Length == 0 || name is "." or ".." || name.Contains('/') || name.Contains('\\'))
        {
            ErrorMessage = "Enter a valid folder name without path separators.";
            return false;
        }
        if (Items.Any(item => string.Equals(item.Name, name, StringComparison.Ordinal)))
        {
            ErrorMessage = $"A file or folder named '{name}' already exists.";
            return false;
        }

        using var operation = BeginOperation(cancellationToken);
        try
        {
            var result = await _session.Sftp.CreateDirectoryAsync(
                SftpPath.Combine(CurrentPath, name),
                operation.Token).ConfigureAwait(true);
            if (result.IsFailure)
            {
                ErrorMessage = result.Failure.Error == SftpError.AlreadyExists
                    ? $"A folder named '{name}' already exists."
                    : result.Failure.Message;
                return false;
            }
            IsCreatingFolder = false;
            FeedbackMessage = $"Created folder '{name}'.";
            _ = await NavigateCoreAsync(CurrentPath, addHistory: false, CancellationToken.None).ConfigureAwait(true);
            return true;
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "Folder creation was cancelled.";
            return false;
        }
        finally
        {
            EndOperation();
        }
    }

    public async Task<bool> DeleteAsync(
        IEnumerable<SftpItemViewModel> selected,
        CancellationToken cancellationToken = default)
    {
        if (_session is null)
        {
            return false;
        }
        var roots = selected.Distinct().ToArray();
        if (roots.Length == 0)
        {
            return false;
        }

        using var operation = BeginOperation(cancellationToken);
        var plan = new List<RemoteFileInfo>();
        var succeeded = new List<string>();
        var failures = new List<(string Path, SftpFailure Failure)>();
        try
        {
            foreach (var root in roots)
            {
                if (!await BuildDeletePlanAsync(root.File, plan, operation.Token).ConfigureAwait(true))
                {
                    return false;
                }
            }

            var confirmed = await _confirmation.ConfirmAsync(
                plan.Count == 1 ? "Delete item?" : "Delete items recursively?",
                $"Permanently delete {plan.Count} item(s)? This includes every file and folder below the selection.",
                "Delete",
                operation.Token).ConfigureAwait(true);
            if (!confirmed)
            {
                FeedbackMessage = "Delete cancelled.";
                return false;
            }

            foreach (var entry in plan)
            {
                operation.Token.ThrowIfCancellationRequested();
                var result = await _session.Sftp.DeleteAsync(
                    entry.FullPath,
                    recursive: false,
                    operation.Token).ConfigureAwait(true);
                if (result.IsSuccess)
                {
                    succeeded.Add(entry.FullPath);
                }
                else
                {
                    failures.Add((entry.FullPath, result.Failure));
                }
            }

            if (succeeded.Count > 0)
            {
                _ = await NavigateCoreAsync(CurrentPath, addHistory: false, CancellationToken.None).ConfigureAwait(true);
            }
            if (failures.Count > 0)
            {
                ErrorMessage = $"Deleted {succeeded.Count} of {plan.Count} item(s). " +
                    $"Failed: {string.Join(", ", failures.Select(failure => failure.Path))}. " +
                    failures[0].Failure.Message;
                return false;
            }

            FeedbackMessage = $"Deleted {succeeded.Count} item(s).";
            return true;
        }
        catch (OperationCanceledException)
        {
            var cancellationMessage =
                $"Delete cancelled after deleting {succeeded.Count} of {plan.Count} planned item(s).";
            if (succeeded.Count > 0)
            {
                _ = await NavigateCoreAsync(CurrentPath, addHistory: false, CancellationToken.None).ConfigureAwait(true);
            }
            ErrorMessage = cancellationMessage;
            return false;
        }
        finally
        {
            EndOperation();
        }
    }

    public static SftpPropertiesViewModel GetProperties(SftpItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new SftpPropertiesViewModel(
            item.Name,
            item.FullPath,
            item.IsDirectory ? "Folder" : item.SizeText,
            item.Modified,
            item.Permissions,
            FormatSymbolicMode(item.File.Mode),
            item.File.Owner,
            item.File.Group,
            item.File.SymlinkTarget);
    }

    public async Task<SftpPermissionsEditorViewModel?> CreatePermissionsEditorAsync(
        SftpItemViewModel? item,
        CancellationToken cancellationToken = default)
    {
        if (_session is null)
        {
            return null;
        }
        var target = item?.File;
        var isCurrentDirectory = item is null;
        if (target is null)
        {
            var stat = await _session.Sftp.StatAsync(CurrentPath, cancellationToken).ConfigureAwait(true);
            if (stat.IsFailure || stat.Value is null)
            {
                ErrorMessage = stat.IsFailure
                    ? stat.Failure.Message
                    : "The current directory metadata is unavailable.";
                return null;
            }
            target = stat.Value;
        }
        return new SftpPermissionsEditorViewModel(
            target,
            isCurrentDirectory,
            _session.Sftp,
            _confirmation,
            token => NavigateCoreAsync(CurrentPath, addHistory: false, token));
    }

    public async Task CopyPathAsync(SftpItemViewModel item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var literal = SftpPath.ToShellLiteral(item.FullPath);
        var result = await _clipboard.WriteTextAsync(literal, cancellationToken).ConfigureAwait(true);
        if (result.Succeeded)
        {
            FeedbackMessage = $"Copied shell-safe path: {literal}";
        }
        else
        {
            ErrorMessage = result.ErrorMessage ?? "The path could not be copied.";
        }
    }

    public async Task EditRemoteAsync(
        SftpItemViewModel item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.IsDirectory || _remoteEdits is null)
        {
            ErrorMessage = item.IsDirectory
                ? "Choose a file to edit."
                : "Remote editing is not available for this session.";
            return;
        }
        try
        {
            var edit = await _remoteEdits.OpenAsync(item.FullPath, cancellationToken).ConfigureAwait(true);
            FeedbackMessage = $"Editing '{item.Name}' at {edit.LocalPath}. Changes upload automatically.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ErrorMessage = $"The remote file could not be opened for editing: {exception.Message}";
        }
    }

    public async Task CloseRemoteEditAsync(
        SftpItemViewModel item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var edit = _remoteEdits?.ActiveEdits.FirstOrDefault(active =>
            string.Equals(active.OriginalRemotePath, item.FullPath, StringComparison.Ordinal));
        if (edit is null)
        {
            FeedbackMessage = $"'{item.Name}' is not open for remote editing.";
            return;
        }
        if (await _remoteEdits!.CloseAsync(edit, cancellationToken).ConfigureAwait(true))
        {
            FeedbackMessage = $"Stopped editing '{item.Name}'.";
        }
    }

    [RelayCommand]
    public void CancelOperation()
    {
        _operationCancellation?.Cancel();
    }

    partial void OnShowHiddenFilesChanged(bool value)
    {
        if (_session is not null)
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

    public async ValueTask DisposeAsync()
    {
        CancelBusyIndicatorDelay();
        _ = await DisposeSessionAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
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

    /// <summary>Best effort, and silent. A staging directory that survives is the pre-existing leak
    /// ADR-0021 records; failing to remove it must not turn a drop that worked into an error banner.
    /// </summary>
    private static void DiscardStaging(string stagingDirectory)
    {
        try
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private bool CanConnectSelected()
    {
        return SelectedConnection is not null;
    }

    private void NotifyConnectionChoicesChanged()
    {
        OnPropertyChanged(nameof(HasConnectionChoices));
        OnPropertyChanged(nameof(NoConnectionMessage));
    }

    /// <summary>Points the picker at the session that just attached, adding an entry for it when the
    /// workspace was opened from the explorer before the list was ever loaded.</summary>
    private void SyncPickerWithSession(Guid connectionId, string name, string endpoint)
    {
        var match = AvailableConnections.FirstOrDefault(choice => choice.Id == connectionId);
        if (match is null)
        {
            match = new SftpConnectionChoice(connectionId, name, endpoint);
            AvailableConnections.Add(match);
            NotifyConnectionChoicesChanged();
        }

        SelectedConnection = match;
    }

    private void CancelBusyIndicatorDelay()
    {
        var pending = _busyIndicatorDelay;
        _busyIndicatorDelay = null;
        pending?.Cancel();
        pending?.Dispose();
    }

    private async Task<bool> NavigateCoreAsync(
        string path,
        bool addHistory,
        CancellationToken cancellationToken)
    {
        if (_session is null)
        {
            return false;
        }
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var normalized = SftpPath.Normalize(path);
            var result = await _session.Sftp.ListAsync(normalized, cancellationToken).ConfigureAwait(true);
            if (result.IsFailure)
            {
                ErrorMessage = result.Failure.Message;
                PathText = CurrentPath;
                return false;
            }

            if (addHistory && !string.Equals(CurrentPath, normalized, StringComparison.Ordinal))
            {
                _backHistory.Add(CurrentPath);
                _forwardHistory.Clear();
            }
            CurrentPath = normalized;
            PathText = normalized;
            Items.Clear();
            foreach (var entry in result.Value.Where(entry =>
                ShowHiddenFiles || entry.Name.Length == 0 || entry.Name[0] != '.'))
            {
                Items.Add(new SftpItemViewModel(entry));
            }
            ApplySort();
            RebuildBreadcrumbs();
            SelectedItems.Clear();
            NotifyHistoryChanged();
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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

    private void ApplySort()
    {
        var indexed = Items.Select((item, index) => (item, index));
        var ordered = indexed
            .OrderBy(pair => pair.item.IsDirectory ? 0 : 1);
        ordered = SortColumn switch
        {
            SftpSortColumn.Size => SortDescending
                ? ordered.ThenByDescending(pair => pair.item.Size)
                : ordered.ThenBy(pair => pair.item.Size),
            SftpSortColumn.Modified => SortDescending
                ? ordered.ThenByDescending(pair => pair.item.Modified)
                : ordered.ThenBy(pair => pair.item.Modified),
            SftpSortColumn.Permissions => SortDescending
                ? ordered.ThenByDescending(pair => pair.item.Permissions, StringComparer.Ordinal)
                : ordered.ThenBy(pair => pair.item.Permissions, StringComparer.Ordinal),
            SftpSortColumn.Owner => SortDescending
                ? ordered.ThenByDescending(pair => pair.item.Owner, StringComparer.OrdinalIgnoreCase)
                : ordered.ThenBy(pair => pair.item.Owner, StringComparer.OrdinalIgnoreCase),
            SftpSortColumn.Name => SortDescending
                ? ordered.ThenByDescending(pair => pair.item.Name, StringComparer.OrdinalIgnoreCase)
                : ordered.ThenBy(pair => pair.item.Name, StringComparer.OrdinalIgnoreCase),
            _ => throw new ArgumentOutOfRangeException(nameof(SortColumn)),
        };
        var sorted = ordered
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

    private void RebuildBreadcrumbs()
    {
        Breadcrumbs.Clear();
        Breadcrumbs.Add(new SftpBreadcrumb("/", "/"));
        var current = string.Empty;
        foreach (var segment in CurrentPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + segment;
            Breadcrumbs.Add(new SftpBreadcrumb(segment, current));
        }
    }

    private void NotifyHistoryChanged()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }

    private async Task<bool> DisposeSessionAsync()
    {
        _operationCancellation?.Cancel();
        if (_remoteEdits is not null)
        {
            if (!await _remoteEdits.CloseAllAsync().ConfigureAwait(false))
            {
                return false;
            }
            _remoteEdits.ActiveEditsChanged -= OnActiveEditsChanged;
            _remoteEdits.UploadCompleted -= OnRemoteEditUploadCompleted;
            await _remoteEdits.DisposeAsync().ConfigureAwait(false);
            _remoteEdits = null;
        }
        _transfers?.Dispose();
        _transfers = null;
        if (_session is not null)
        {
            await _session.DisposeAsync().ConfigureAwait(false);
            _session = null;
        }
        OnPropertyChanged(nameof(ActiveRemoteEditCount));
        OnPropertyChanged(nameof(HasActiveRemoteEdits));
        OnPropertyChanged(nameof(RemoteEditIndicator));
        return true;
    }

    private void OnActiveEditsChanged(object? sender, EventArgs args)
    {
        OnUiThread(() =>
        {
            OnPropertyChanged(nameof(ActiveRemoteEditCount));
            OnPropertyChanged(nameof(HasActiveRemoteEdits));
            OnPropertyChanged(nameof(RemoteEditIndicator));
        });
    }

    private void OnRemoteEditUploadCompleted(object? sender, RemoteEditUploadResult result)
    {
        var name = SftpPath.GetName(result.RemotePath);
        OnUiThread(() =>
        {
            if (result.Succeeded)
            {
                FeedbackMessage = $"Saved '{name}' to {result.RemotePath}.";
            }
            else
            {
                ErrorMessage = $"'{name}' could not be saved to {result.RemotePath}: {result.Message}";
            }
        });
    }

    private void OnUiThread(Action action)
    {
        // Watcher callbacks arrive on a background thread; bound state has to change on the UI thread.
        if (_dispatcher is null)
        {
            action();
            return;
        }
        _ = _dispatcher.InvokeAsync(action).AsTask();
    }

    private async Task<bool> BuildDeletePlanAsync(
        RemoteFileInfo entry,
        List<RemoteFileInfo> plan,
        CancellationToken cancellationToken)
    {
        if (_session is null || plan.Any(item => string.Equals(item.FullPath, entry.FullPath, StringComparison.Ordinal)))
        {
            return true;
        }
        if (entry.IsDirectory && !entry.IsSymlink)
        {
            var children = await _session.Sftp.ListAsync(entry.FullPath, cancellationToken).ConfigureAwait(true);
            if (children.IsFailure)
            {
                ErrorMessage = $"Cannot count items below '{entry.FullPath}': {children.Failure.Message}";
                return false;
            }
            foreach (var child in children.Value)
            {
                if (!await BuildDeletePlanAsync(child, plan, cancellationToken).ConfigureAwait(true))
                {
                    return false;
                }
            }
        }
        plan.Add(entry);
        return true;
    }

    private CancellationTokenSource BeginOperation(CancellationToken cancellationToken)
    {
        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();
        _operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        IsMutating = true;
        return _operationCancellation;
    }

    private void EndOperation()
    {
        _operationCancellation = null;
        IsMutating = false;
    }

    partial void OnIsMutatingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCancelOperation));
    }

    private static string FormatSymbolicMode(UnixFileMode mode)
    {
        Span<char> symbols =
        [
            mode.HasFlag(UnixFileMode.UserRead) ? 'r' : '-',
            mode.HasFlag(UnixFileMode.UserWrite) ? 'w' : '-',
            mode.HasFlag(UnixFileMode.UserExecute) ? 'x' : '-',
            mode.HasFlag(UnixFileMode.GroupRead) ? 'r' : '-',
            mode.HasFlag(UnixFileMode.GroupWrite) ? 'w' : '-',
            mode.HasFlag(UnixFileMode.GroupExecute) ? 'x' : '-',
            mode.HasFlag(UnixFileMode.OtherRead) ? 'r' : '-',
            mode.HasFlag(UnixFileMode.OtherWrite) ? 'w' : '-',
            mode.HasFlag(UnixFileMode.OtherExecute) ? 'x' : '-',
        ];
        if (mode.HasFlag(UnixFileMode.SetUser))
        {
            symbols[2] = symbols[2] == 'x' ? 's' : 'S';
        }
        if (mode.HasFlag(UnixFileMode.SetGroup))
        {
            symbols[5] = symbols[5] == 'x' ? 's' : 'S';
        }
        if (mode.HasFlag(UnixFileMode.StickyBit))
        {
            symbols[8] = symbols[8] == 'x' ? 't' : 'T';
        }
        return new string(symbols);
    }

    private static string DescribeTransferFailure(TransferResult result, string operation)
    {
        var failure = result.Items.FirstOrDefault(item => item.Failure is not null)?.Failure;
        return failure?.Error == SftpError.PermissionDenied
            ? $"The {operation} was denied. The destination is read-only; no partial item was published."
            : failure?.Message ?? $"The {operation} did not complete.";
    }
}
