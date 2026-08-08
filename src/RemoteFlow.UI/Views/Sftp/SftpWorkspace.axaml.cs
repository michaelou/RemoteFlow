using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using RemoteFlow.UI.ViewModels.Sftp;

namespace RemoteFlow.UI.Views.Sftp;

public sealed partial class SftpWorkspace : UserControl
{
    private string _typePrefix = string.Empty;
    private DateTimeOffset _lastTyped;

    public SftpWorkspace()
    {
        InitializeComponent();
    }

    private void Workspace_OnLoaded(object? sender, RoutedEventArgs e)
    {
        _ = FileList.Focus();
    }

    private async void PathEditor_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is SftpWorkspaceViewModel viewModel)
        {
            await viewModel.NavigatePathAsync().ConfigureAwait(true);
            e.Handled = true;
        }
    }

    private async void Breadcrumb_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SftpBreadcrumb breadcrumb } &&
            DataContext is SftpWorkspaceViewModel viewModel)
        {
            await viewModel.NavigateAsync(breadcrumb.Path).ConfigureAwait(true);
        }
    }

    private void FileList_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is SftpWorkspaceViewModel viewModel)
        {
            viewModel.SetSelection(FileList.SelectedItems?.Cast<SftpItemViewModel>() ?? []);
        }
    }

    private async void FileList_OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (FileList.SelectedItem is SftpItemViewModel item && DataContext is SftpWorkspaceViewModel viewModel)
        {
            await viewModel.OpenAsync(item).ConfigureAwait(true);
            e.Handled = true;
        }
    }

    private async void FileList_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not SftpWorkspaceViewModel viewModel)
        {
            return;
        }
        if (e.Key == Key.Enter && FileList.SelectedItem is SftpItemViewModel item)
        {
            await viewModel.OpenAsync(item).ConfigureAwait(true);
            e.Handled = true;
        }
        else if (e.Key == Key.Back)
        {
            await viewModel.UpAsync().ConfigureAwait(true);
            e.Handled = true;
        }
        else if (e.Key == Key.R && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            await viewModel.RefreshAsync().ConfigureAwait(true);
            e.Handled = true;
        }
    }

    private void FileList_OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || DataContext is not SftpWorkspaceViewModel viewModel)
        {
            return;
        }
        var now = DateTimeOffset.UtcNow;
        _typePrefix = now - _lastTyped > TimeSpan.FromSeconds(1) ? e.Text : _typePrefix + e.Text;
        _lastTyped = now;
        var match = viewModel.FindByPrefix(_typePrefix, FileList.SelectedIndex);
        if (match is not null)
        {
            FileList.SelectedItem = match;
            FileList.ScrollIntoView(match);
            e.Handled = true;
        }
    }

    private async void FileList_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed ||
            DataContext is not SftpWorkspaceViewModel viewModel)
        {
            return;
        }
        var item = FindItem(e.Source);
        if (item is null || !viewModel.SelectedItems.Contains(item))
        {
            return;
        }

        var staging = Path.Combine(Path.GetTempPath(), "RemoteFlow", "drag", Guid.NewGuid().ToString("N"));
        var paths = await viewModel.PrepareDragOutAsync(viewModel.SelectedItems, staging).ConfigureAwait(true);
        if (paths.Count == 0 || TopLevel.GetTopLevel(this)?.StorageProvider is not { } provider)
        {
            return;
        }
        var transfer = new DataTransfer();
        foreach (var path in paths)
        {
            IStorageItem? storageItem = Directory.Exists(path)
                ? await provider.TryGetFolderFromPathAsync(path)
                : await provider.TryGetFileFromPathAsync(path);
            if (storageItem is not null)
            {
                transfer.Add(DataTransferItem.CreateFile(storageItem));
            }
        }
        if (transfer.Items.Count > 0)
        {
            _ = await DragDrop.DoDragDropAsync(e, transfer, DragDropEffects.Copy).ConfigureAwait(true);
        }
    }

    private void FileList_OnDragOver(object? sender, DragEventArgs e)
    {
        if (DataContext is not SftpWorkspaceViewModel viewModel || e.DataTransfer.TryGetFiles() is not { Length: > 0 })
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }
        viewModel.SetDropTarget(FindItem(e.Source));
        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void FileList_OnDragLeave(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SftpWorkspaceViewModel viewModel)
        {
            viewModel.ClearDropTarget();
        }
    }

    private async void FileList_OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is SftpWorkspaceViewModel viewModel && e.DataTransfer.TryGetFiles() is { Length: > 0 } files)
        {
            var hovered = FindItem(e.Source);
            var target = hovered is { IsDirectory: true } ? hovered.FullPath : viewModel.CurrentPath;
            var paths = files.Select(file => file.TryGetLocalPath()).Where(path => path is not null).Cast<string>();
            await viewModel.UploadAsync(paths, target).ConfigureAwait(true);
            viewModel.ClearDropTarget();
        }
        e.Handled = true;
    }

    private static SftpItemViewModel? FindItem(object? source)
    {
        return source is Control { DataContext: SftpItemViewModel item } ? item : null;
    }

    private void SortName_OnClick(object? sender, RoutedEventArgs e)
    {
        Sort(SftpSortColumn.Name);
    }

    private void SortSize_OnClick(object? sender, RoutedEventArgs e)
    {
        Sort(SftpSortColumn.Size);
    }

    private void SortModified_OnClick(object? sender, RoutedEventArgs e)
    {
        Sort(SftpSortColumn.Modified);
    }

    private void SortPermissions_OnClick(object? sender, RoutedEventArgs e)
    {
        Sort(SftpSortColumn.Permissions);
    }

    private void SortOwner_OnClick(object? sender, RoutedEventArgs e)
    {
        Sort(SftpSortColumn.Owner);
    }

    private void Sort(SftpSortColumn column)
    {
        if (DataContext is SftpWorkspaceViewModel viewModel)
        {
            viewModel.SortBy(column);
        }
    }
}
