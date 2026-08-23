using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RemoteFlow.Application.Abstractions.Storage;
using RemoteFlow.UI.ViewModels.Storage;

namespace RemoteFlow.UI.Views.Storage;

/// <summary>One pane, used twice. Every handler here reads its state from <c>DataContext</c> rather than
/// from a field, which is what lets the same control serve the local filesystem and the bucket.</summary>
public sealed partial class FileBrowserPane : UserControl
{
    /// <summary>The drag payload between panes: the pane the rows came from, passed in process. The
    /// receiving pane asks it to run its own transfer, which already knows where the other side points.
    /// Nothing is staged to disk, so the Storage page needs no staging directory and cannot leak one.
    ///
    /// Public because the SFTP page's remote list is not a pane and has to recognise this format to accept
    /// an upload dragged out of the local pane beside it.</summary>
    public static readonly DataFormat<FileBrowserPaneViewModel> PaneFormat =
        DataFormat.CreateInProcessFormat<FileBrowserPaneViewModel>("remoteflow/file-browser-pane");

    /// <summary>The payload for a drop from something that is not a pane — the SFTP page's remote list. The
    /// pane accepts it without knowing what it is: the payload carries the action, the pane the
    /// destination.</summary>
    public static readonly DataFormat<FileBrowserExternalDrop> ExternalDropFormat =
        DataFormat.CreateInProcessFormat<FileBrowserExternalDrop>("remoteflow/file-browser-external-drop");

    private string _typePrefix = string.Empty;
    private DateTimeOffset _lastTyped;

    public FileBrowserPane()
    {
        InitializeComponent();
    }

    /// <summary>Focused by the page's pane-jump shortcuts, which is why it is exposed rather than
    /// private.
    ///
    /// The row, not the list: the Fluent <c>ListBox</c> is not itself focusable — focus belongs to the
    /// item containers — so calling <c>Focus</c> on the list quietly does nothing.</summary>
    public bool FocusList()
    {
        if (EntryList.ItemCount > 0)
        {
            if (EntryList.SelectedIndex < 0)
            {
                EntryList.SelectedIndex = 0;
            }

            if (EntryList.ContainerFromIndex(EntryList.SelectedIndex) is InputElement container &&
                container.Focus())
            {
                return true;
            }
        }

        return EntryList.Focus();
    }

    public bool FocusPathBox()
    {
        var focused = PathEditor.Focus();
        PathEditor.SelectAll();
        return focused;
    }

    private static FileBrowserItemViewModel? FindItem(object? source)
    {
        return source is Control { DataContext: FileBrowserItemViewModel item } ? item : null;
    }

    private async void PathEditor_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is FileBrowserPaneViewModel viewModel)
        {
            await viewModel.NavigatePathAsync().ConfigureAwait(true);
            e.Handled = true;
        }
    }

    private async void Breadcrumb_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: FileBrowserCrumb crumb } &&
            DataContext is FileBrowserPaneViewModel viewModel)
        {
            _ = await viewModel.NavigateAsync(crumb.Path).ConfigureAwait(true);
        }
    }

    private void EntryList_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is FileBrowserPaneViewModel viewModel)
        {
            viewModel.SetSelection(EntryList.SelectedItems?.Cast<FileBrowserItemViewModel>() ?? []);
        }
    }

    private async void EntryList_OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (EntryList.SelectedItem is FileBrowserItemViewModel item &&
            DataContext is FileBrowserPaneViewModel viewModel)
        {
            _ = await viewModel.OpenAsync(item).ConfigureAwait(true);
            e.Handled = true;
        }
    }

    private async void EntryList_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not FileBrowserPaneViewModel viewModel)
        {
            return;
        }

        if (e.Key == Key.Enter && EntryList.SelectedItem is FileBrowserItemViewModel { IsDirectory: true } folder)
        {
            // Enter descends into a folder and transfers a file to the other pane, which is the gesture a
            // dual-pane user already has in their fingers.
            _ = await viewModel.OpenAsync(folder).ConfigureAwait(true);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && viewModel.SelectedItems.Count > 0)
        {
            await viewModel.TransferAsync().ConfigureAwait(true);
            e.Handled = true;
        }
        else if (e.Key == Key.Back || (e.Key == Key.Left && e.KeyModifiers.HasFlag(KeyModifiers.Alt)))
        {
            await viewModel.UpAsync().ConfigureAwait(true);
            e.Handled = true;
        }
        else if (e.Key == Key.Right && e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            await viewModel.ForwardAsync().ConfigureAwait(true);
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            await viewModel.RefreshAsync().ConfigureAwait(true);
            e.Handled = true;
        }
        else if (e.Key == Key.F7)
        {
            BeginCreateFolder(viewModel);
            e.Handled = true;
        }
        else if (e.Key == Key.F2 &&
            viewModel.SupportsRename &&
            EntryList.SelectedItem is FileBrowserItemViewModel renaming)
        {
            BeginRenameCore(viewModel, renaming);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            _ = await viewModel.DeleteAsync(viewModel.SelectedItems).ConfigureAwait(true);
            e.Handled = true;
        }
    }

    private void EntryList_OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || DataContext is not FileBrowserPaneViewModel viewModel)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        _typePrefix = now - _lastTyped > TimeSpan.FromSeconds(1) ? e.Text : _typePrefix + e.Text;
        _lastTyped = now;
        var match = viewModel.FindByPrefix(_typePrefix, EntryList.SelectedIndex);
        if (match is not null)
        {
            EntryList.SelectedItem = match;
            EntryList.ScrollIntoView(match);
            e.Handled = true;
        }
    }

    private async void EntryList_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not FileBrowserPaneViewModel viewModel)
        {
            return;
        }

        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsRightButtonPressed)
        {
            // Right-clicking outside the current selection targets just that row, so the menu always acts
            // on what was clicked; right-clicking inside it keeps the multi-selection intact.
            if (FindItem(e.Source) is { } clicked && !viewModel.SelectedItems.Contains(clicked))
            {
                EntryList.SelectedItem = clicked;
            }

            return;
        }

        if (!properties.IsLeftButtonPressed ||
            FindItem(e.Source) is not { } dragged ||
            !viewModel.SelectedItems.Contains(dragged))
        {
            return;
        }

        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(PaneFormat, viewModel));
        _ = await DragDrop.DoDragDropAsync(e, transfer, DragDropEffects.Copy).ConfigureAwait(true);
    }

    private void EntryList_OnDragOver(object? sender, DragEventArgs e)
    {
        if (DataContext is not FileBrowserPaneViewModel viewModel || !viewModel.IsReady)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        if (e.DataTransfer.TryGetValue(ExternalDropFormat) is { } external)
        {
            viewModel.SetDropTarget(FindItem(e.Source), external.Verb);
        }
        else if (e.DataTransfer.TryGetValue(PaneFormat) is { } origin && !ReferenceEquals(origin, viewModel))
        {
            viewModel.SetDropTarget(FindItem(e.Source));
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void EntryList_OnDragLeave(object? sender, RoutedEventArgs e)
    {
        if (DataContext is FileBrowserPaneViewModel viewModel)
        {
            viewModel.ClearDropTarget();
        }
    }

    private async void EntryList_OnDrop(object? sender, DragEventArgs e)
    {
        // Between two panes there is no operating-system staging directory at all — see ADR-0021, and the
        // SFTP leak it deliberately does not replicate.
        if (DataContext is FileBrowserPaneViewModel viewModel)
        {
            var hovered = FindItem(e.Source);
            viewModel.ClearDropTarget();
            if (e.DataTransfer.TryGetValue(ExternalDropFormat) is { } external)
            {
                await external.DropAsync(viewModel.DropTargetPath(hovered), CancellationToken.None)
                    .ConfigureAwait(true);
                await viewModel.RefreshAsync().ConfigureAwait(true);
            }
            else if (e.DataTransfer.TryGetValue(PaneFormat) is { } origin && !ReferenceEquals(origin, viewModel))
            {
                await origin.TransferAsync().ConfigureAwait(true);
            }
        }

        e.Handled = true;
    }

    private void NewFolder_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is FileBrowserPaneViewModel viewModel)
        {
            BeginCreateFolder(viewModel);
        }
    }

    private async void CreateFolder_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is FileBrowserPaneViewModel viewModel)
        {
            _ = await viewModel.CommitCreateFolderAsync().ConfigureAwait(true);
        }
    }

    private void CancelCreateFolder_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is FileBrowserPaneViewModel viewModel)
        {
            viewModel.CancelCreateFolder();
        }
    }

    private async void NewFolderEditor_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not FileBrowserPaneViewModel viewModel)
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

    private async void Open_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: FileBrowserItemViewModel item } &&
            DataContext is FileBrowserPaneViewModel viewModel)
        {
            _ = await viewModel.OpenAsync(item).ConfigureAwait(true);
        }
    }

    /// <summary>Upload or download from the row menu. Right-clicking outside the selection has already
    /// narrowed it to the clicked row, so this transfers exactly what the menu was opened over.</summary>
    private async void Transfer_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is FileBrowserPaneViewModel viewModel)
        {
            await viewModel.TransferAsync().ConfigureAwait(true);
        }
    }

    private void Rename_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: FileBrowserItemViewModel item } &&
            DataContext is FileBrowserPaneViewModel viewModel)
        {
            BeginRenameCore(viewModel, item);
        }
    }

    private async void RenameEditor_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: FileBrowserItemViewModel item } ||
            DataContext is not FileBrowserPaneViewModel viewModel)
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
            FileBrowserPaneViewModel.CancelRename(item);
            e.Handled = true;
        }
    }

    private async void Delete_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: FileBrowserItemViewModel item } &&
            DataContext is FileBrowserPaneViewModel viewModel)
        {
            var selected = viewModel.SelectedItems.Contains(item)
                ? viewModel.SelectedItems.ToArray()
                : [item];
            _ = await viewModel.DeleteAsync(selected).ConfigureAwait(true);
        }
    }

    private async void CopyPath_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: FileBrowserItemViewModel item } ||
            TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        // The plain path, not a shell literal: ToShellLiteral is meaningless for an object key.
        await ClipboardExtensions.SetTextAsync(clipboard, item.Path).ConfigureAwait(true);
        if (DataContext is FileBrowserPaneViewModel viewModel)
        {
            viewModel.FeedbackMessage = $"Copied {item.Path}.";
        }
    }

    private void SortName_OnClick(object? sender, RoutedEventArgs e)
    {
        Sort(FileBrowserSortColumn.Name);
    }

    private void SortSize_OnClick(object? sender, RoutedEventArgs e)
    {
        Sort(FileBrowserSortColumn.Size);
    }

    private void SortModified_OnClick(object? sender, RoutedEventArgs e)
    {
        Sort(FileBrowserSortColumn.Modified);
    }

    private void Sort(FileBrowserSortColumn column)
    {
        if (DataContext is FileBrowserPaneViewModel viewModel)
        {
            viewModel.SortBy(column);
        }
    }

    private void BeginCreateFolder(FileBrowserPaneViewModel viewModel)
    {
        viewModel.BeginCreateFolder();
        Dispatcher.UIThread.Post(() =>
        {
            _ = NewFolderEditor.Focus();
            NewFolderEditor.SelectAll();
        });
    }

    private void BeginRenameCore(FileBrowserPaneViewModel viewModel, FileBrowserItemViewModel item)
    {
        viewModel.BeginRename(item);
        if (!item.IsRenaming)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            var editor = EntryList.GetVisualDescendants()
                .OfType<TextBox>()
                .FirstOrDefault(control => ReferenceEquals(control.DataContext, item));
            _ = editor?.Focus();
            editor?.SelectAll();
        });
    }
}
