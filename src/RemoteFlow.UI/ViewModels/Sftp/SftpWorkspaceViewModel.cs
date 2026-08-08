using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Sftp;
using RemoteFlow.Application.Services;
using RemoteFlow.UI.Services;

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

public sealed partial class SftpWorkspaceViewModel(
    ISftpWorkspaceSessionFactory sessions,
    IFilePickerService filePicker,
    IConfirmationDialogService confirmation,
    IClipboardService clipboard) : PageViewModel("SFTP"), IAsyncDisposable
{
    private readonly List<string> _backHistory = [];
    private readonly List<string> _forwardHistory = [];
    private SftpWorkspaceSession? _session;
    private TransferEngine? _transfers;
    private CancellationTokenSource? _operationCancellation;

    public ObservableCollection<SftpItemViewModel> Items { get; } = [];

    public ObservableCollection<SftpItemViewModel> SelectedItems { get; } = [];

    public ObservableCollection<SftpBreadcrumb> Breadcrumbs { get; } = [];

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

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    [ObservableProperty]
    public partial string? FeedbackMessage { get; private set; }

    [ObservableProperty]
    public partial string? DropTargetMessage { get; private set; }

    [ObservableProperty]
    public partial SftpSortColumn SortColumn { get; private set; }

    [ObservableProperty]
    public partial bool SortDescending { get; private set; }

    public bool CanGoBack => _backHistory.Count > 0;

    public bool CanGoForward => _forwardHistory.Count > 0;

    public bool IsConnected => _session is not null;

    public bool CanCancelOperation => IsMutating;

    [ObservableProperty]
    public partial bool IsMutating { get; private set; }

    [ObservableProperty]
    public partial bool IsCreatingFolder { get; private set; }

    [ObservableProperty]
    public partial string NewFolderName { get; set; } = "New folder";

    public async Task AttachAsync(Guid connectionId, CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var next = await sessions.OpenAsync(connectionId, cancellationToken).ConfigureAwait(true);
            await DisposeSessionAsync().ConfigureAwait(true);
            _session = next;
            _transfers = new TransferEngine(next.Sftp);
            ConnectionTitle = next.Definition.Name;
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
            OnPropertyChanged(nameof(IsConnected));
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
        var paths = await filePicker.PickUploadPathsAsync(cancellationToken).ConfigureAwait(true);
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
        foreach (var localPath in localPaths)
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
        var folder = await filePicker.PickDownloadFolderAsync(
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
        var completed = new List<string>();
        foreach (var item in items)
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
        if (completed.Count > 0 && ErrorMessage is null)
        {
            FeedbackMessage = $"Downloaded {completed.Count} item(s).";
        }
        return completed;
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

            var confirmed = await confirmation.ConfirmAsync(
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
            confirmation,
            token => NavigateCoreAsync(CurrentPath, addHistory: false, token));
    }

    public async Task CopyPathAsync(SftpItemViewModel item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var literal = SftpPath.ToShellLiteral(item.FullPath);
        var result = await clipboard.WriteTextAsync(literal, cancellationToken).ConfigureAwait(true);
        if (result.Succeeded)
        {
            FeedbackMessage = $"Copied shell-safe path: {literal}";
        }
        else
        {
            ErrorMessage = result.ErrorMessage ?? "The path could not be copied.";
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

    public async ValueTask DisposeAsync()
    {
        await DisposeSessionAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
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

    private async Task DisposeSessionAsync()
    {
        _operationCancellation?.Cancel();
        _transfers?.Dispose();
        _transfers = null;
        if (_session is not null)
        {
            await _session.DisposeAsync().ConfigureAwait(false);
            _session = null;
        }
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
