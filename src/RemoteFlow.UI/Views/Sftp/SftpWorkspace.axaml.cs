using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RemoteFlow.UI.ViewModels.Sftp;
using RemoteFlow.UI.ViewModels.Storage;
using RemoteFlow.UI.Views.Storage;

namespace RemoteFlow.UI.Views.Sftp;

public sealed partial class SftpWorkspace : UserControl
{
    private readonly DragGesture _drag = new();
    private string _typePrefix = string.Empty;
    private DateTimeOffset _lastTyped;

    public SftpWorkspace()
    {
        InitializeComponent();

        // handledEventsToo, and attached here rather than in the markup: the row container marks a press
        // handled the moment it triggers selection, and it sees the event first, being a child of the
        // list. A handler declared on the ListBox therefore never ran on a row — see FileBrowserPane,
        // which had the identical defect.
        FileList.AddHandler(PointerPressedEvent, FileList_OnPointerPressed, handledEventsToo: true);
        FileList.AddHandler(PointerMovedEvent, FileList_OnPointerMoved, handledEventsToo: true);
        FileList.AddHandler(PointerReleasedEvent, FileList_OnPointerReleased, handledEventsToo: true);
    }

    private async void Workspace_OnLoaded(object? sender, RoutedEventArgs e)
    {
        // The connection picker is the first tab stop, so the keyboard lands on the decision the page
        // exists to make rather than in the middle of a file list.
        _ = ConnectionPicker.Focus();
        if (DataContext is SftpWorkspaceViewModel viewModel)
        {
            await viewModel.InitializeLocalAsync().ConfigureAwait(true);
            await viewModel.LoadConnectionsAsync().ConfigureAwait(true);
        }
    }

    /// <summary>The explicit pane jump, the same three chords the Storage page binds — see ADR-0021 for why
    /// <c>Tab</c>, <c>F6</c>, <c>Ctrl+Tab</c> and <c>Alt+1</c>/<c>Alt+2</c> are all left alone.</summary>
    private void Workspace_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key == Key.Left)
        {
            _ = LocalPane.FocusList();
            e.Handled = true;
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key == Key.Right)
        {
            _ = FocusRemoteList();
            e.Handled = true;
        }
        else if (e.Key == Key.L)
        {
            if (LocalPane.IsKeyboardFocusWithin)
            {
                _ = LocalPane.FocusPathBox();
            }
            else
            {
                _ = PathEditor.Focus();
                PathEditor.SelectAll();
            }

            e.Handled = true;
        }
    }

    /// <summary>The row, not the list: the Fluent <c>ListBox</c> is not itself focusable — focus belongs to
    /// the item containers — so calling <c>Focus</c> on the list quietly does nothing.</summary>
    private bool FocusRemoteList()
    {
        if (FileList.ItemCount > 0)
        {
            if (FileList.SelectedIndex < 0)
            {
                FileList.SelectedIndex = 0;
            }

            if (FileList.ContainerFromIndex(FileList.SelectedIndex) is InputElement container &&
                container.Focus())
            {
                return true;
            }
        }

        return FileList.Focus();
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
        else if (e.Key == Key.F2 && FileList.SelectedItem is SftpItemViewModel renameItem)
        {
            BeginRename(viewModel, renameItem);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            _ = await viewModel.DeleteAsync(viewModel.SelectedItems).ConfigureAwait(true);
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

    private void FileList_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _drag.Disarm();
        if (DataContext is not SftpWorkspaceViewModel viewModel)
        {
            return;
        }

        var point = e.GetCurrentPoint(this).Properties;
        if (point.IsRightButtonPressed)
        {
            // Right-clicking outside the current selection targets just that row, so the menu always
            // acts on what was clicked; right-clicking inside it keeps the multi-selection intact.
            if (FindItem(e.Source) is { } clicked && !viewModel.SelectedItems.Contains(clicked))
            {
                FileList.SelectedItem = clicked;
            }

            return;
        }

        if (!point.IsLeftButtonPressed)
        {
            return;
        }

        // A click into the inline rename editor is not a drag out of the row being renamed.
        var item = FindItem(e.Source);
        if (item is null or { IsRenaming: true } || !viewModel.SelectedItems.Contains(item))
        {
            return;
        }

        // Armed only. Starting here would download the whole selection on a plain click, because that is
        // what building the file payload below costs.
        _drag.Arm(e, e.GetPosition(this));
    }

    private async void FileList_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (DataContext is not SftpWorkspaceViewModel viewModel ||
            _drag.TryStart(e, e.GetPosition(this)) is not { } press)
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

        // Two payloads for one drag. The operating system gets real files, because ADR-0013 requires every
        // advertised path to exist for the whole drop and there is no lazy file payload to hand it. The
        // local pane beside this list gets an in-process action instead, which moves those same staged
        // files into wherever it was dropped — so an in-app drop is one transfer, not two, and the staging
        // directory is cleaned up rather than left behind.
        transfer.Add(DataTransferItem.Create(
            FileBrowserPane.ExternalDropFormat,
            new FileBrowserExternalDrop(
                "Download to",
                (destination, token) => viewModel.CompleteDragToLocalAsync(staging, paths, destination, token))));
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

        _ = await DragDrop.DoDragDropAsync(press, transfer, DragDropEffects.Copy).ConfigureAwait(true);
    }

    private void FileList_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _drag.Disarm();
    }

    private void FileList_OnDragOver(object? sender, DragEventArgs e)
    {
        if (DataContext is not SftpWorkspaceViewModel viewModel ||
            !viewModel.IsConnected ||
            (!ReferenceEquals(e.DataTransfer.TryGetValue(FileBrowserPane.PaneFormat), viewModel.Local) &&
                e.DataTransfer.TryGetFiles() is not { Length: > 0 }))
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
        if (DataContext is SftpWorkspaceViewModel viewModel)
        {
            var hovered = FindItem(e.Source);
            var target = hovered is { IsDirectory: true } ? hovered.FullPath : viewModel.CurrentPath;
            if (ReferenceEquals(e.DataTransfer.TryGetValue(FileBrowserPane.PaneFormat), viewModel.Local))
            {
                // Out of the local pane, so the paths are already on this machine and nothing is staged:
                // the rows the pane has selected go straight to the folder the pointer was over.
                string[] paths = [.. viewModel.Local.SelectedItems.Select(item => item.Path)];
                await viewModel.UploadAsync(paths, target).ConfigureAwait(true);
                viewModel.ClearDropTarget();
            }
            else if (e.DataTransfer.TryGetFiles() is { Length: > 0 } files)
            {
                var paths = files.Select(file => file.TryGetLocalPath()).Where(path => path is not null).Cast<string>();
                await viewModel.UploadAsync(paths, target).ConfigureAwait(true);
                viewModel.ClearDropTarget();
            }
        }
        e.Handled = true;
    }

    private static SftpItemViewModel? FindItem(object? source)
    {
        return source is Control { DataContext: SftpItemViewModel item } ? item : null;
    }

    private void NewFolder_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SftpWorkspaceViewModel viewModel)
        {
            viewModel.BeginCreateFolder();
            Dispatcher.UIThread.Post(() =>
            {
                _ = NewFolderEditor.Focus();
                NewFolderEditor.SelectAll();
            });
        }
    }

    private async void CreateFolder_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SftpWorkspaceViewModel viewModel)
        {
            _ = await viewModel.CommitCreateFolderAsync().ConfigureAwait(true);
        }
    }

    private void CancelCreateFolder_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SftpWorkspaceViewModel viewModel)
        {
            viewModel.CancelCreateFolder();
        }
    }

    private async void NewFolderEditor_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not SftpWorkspaceViewModel viewModel)
        {
            return;
        }
        if (e.Key == Key.Enter)
        {
            _ = await viewModel.CommitCreateFolderAsync().ConfigureAwait(true);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            viewModel.CancelCreateFolder();
            e.Handled = true;
        }
    }

    private void Rename_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SftpItemViewModel item } &&
            DataContext is SftpWorkspaceViewModel viewModel)
        {
            BeginRename(viewModel, item);
        }
    }

    /// <summary>Download from the row menu, into the folder the local pane is showing. Right-clicking
    /// outside the selection has already narrowed it to the clicked row.</summary>
    private async void Download_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SftpItemViewModel item } &&
            DataContext is SftpWorkspaceViewModel viewModel)
        {
            SftpItemViewModel[] selected = viewModel.SelectedItems.Contains(item)
                ? [.. viewModel.SelectedItems]
                : [item];
            await viewModel.DownloadToLocalPaneAsync(selected).ConfigureAwait(true);
        }
    }

    private async void EditRemote_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SftpItemViewModel item } &&
            DataContext is SftpWorkspaceViewModel viewModel)
        {
            await viewModel.EditRemoteAsync(item).ConfigureAwait(true);
        }
    }

    private async void StopRemoteEdit_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SftpItemViewModel item } &&
            DataContext is SftpWorkspaceViewModel viewModel)
        {
            await viewModel.CloseRemoteEditAsync(item).ConfigureAwait(true);
        }
    }

    private async void RenameEditor_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: SftpItemViewModel item } ||
            DataContext is not SftpWorkspaceViewModel viewModel)
        {
            return;
        }
        if (e.Key == Key.Enter)
        {
            _ = await viewModel.CommitRenameAsync(item).ConfigureAwait(true);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            SftpWorkspaceViewModel.CancelRename(item);
            e.Handled = true;
        }
    }

    private async void Delete_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SftpItemViewModel item } &&
            DataContext is SftpWorkspaceViewModel viewModel)
        {
            var selected = viewModel.SelectedItems.Contains(item) ? viewModel.SelectedItems : [item];
            _ = await viewModel.DeleteAsync(selected).ConfigureAwait(true);
        }
    }

    private async void Properties_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SftpItemViewModel item } &&
            TopLevel.GetTopLevel(this) is Window owner)
        {
            var dialog = new SftpPropertiesDialog(SftpWorkspaceViewModel.GetProperties(item));
            await dialog.ShowDialog(owner).ConfigureAwait(true);
        }
    }

    private async void CurrentPermissions_OnClick(object? sender, RoutedEventArgs e)
    {
        await ShowPermissionsAsync(null).ConfigureAwait(true);
    }

    private async void Permissions_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SftpItemViewModel item })
        {
            await ShowPermissionsAsync(item).ConfigureAwait(true);
        }
    }

    private async Task ShowPermissionsAsync(SftpItemViewModel? item)
    {
        if (DataContext is not SftpWorkspaceViewModel viewModel ||
            TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }
        var editor = await viewModel.CreatePermissionsEditorAsync(item).ConfigureAwait(true);
        if (editor is not null)
        {
            var dialog = new PermissionsDialog(editor);
            await dialog.ShowDialog(owner).ConfigureAwait(true);
        }
    }

    private async void CopyPath_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SftpItemViewModel item } &&
            DataContext is SftpWorkspaceViewModel viewModel)
        {
            await viewModel.CopyPathAsync(item).ConfigureAwait(true);
        }
    }

    private void BeginRename(SftpWorkspaceViewModel viewModel, SftpItemViewModel item)
    {
        viewModel.BeginRename(item);
        Dispatcher.UIThread.Post(() =>
        {
            var editor = FileList.GetVisualDescendants()
                .OfType<TextBox>()
                .FirstOrDefault(control => ReferenceEquals(control.DataContext, item));
            _ = editor?.Focus();
            editor?.SelectAll();
        });
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
