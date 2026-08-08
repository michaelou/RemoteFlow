using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

public sealed class SftpItemViewModel(RemoteFileInfo file)
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

public sealed partial class SftpWorkspaceViewModel(
    ISftpWorkspaceSessionFactory sessions,
    IFilePickerService filePicker) : PageViewModel("SFTP"), IAsyncDisposable
{
    private readonly List<string> _backHistory = [];
    private readonly List<string> _forwardHistory = [];
    private SftpWorkspaceSession? _session;
    private TransferEngine? _transfers;

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
        _transfers?.Dispose();
        _transfers = null;
        if (_session is not null)
        {
            await _session.DisposeAsync().ConfigureAwait(false);
            _session = null;
        }
    }

    private static string DescribeTransferFailure(TransferResult result, string operation)
    {
        var failure = result.Items.FirstOrDefault(item => item.Failure is not null)?.Failure;
        return failure?.Error == SftpError.PermissionDenied
            ? $"The {operation} was denied. The destination is read-only; no partial item was published."
            : failure?.Message ?? $"The {operation} did not complete.";
    }
}
