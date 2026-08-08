using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RemoteFlow.UI.ViewModels.Connections;

namespace RemoteFlow.UI.Views.Connections;

public sealed partial class ConnectionsView : UserControl
{
    private IReadOnlyList<ExplorerNodeViewModel> _draggedNodes = [];

    public ConnectionsView()
    {
        InitializeComponent();
    }

    private async void ConnectionsView_OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ConnectionsPageViewModel viewModel)
        {
            viewModel.RenameStarted -= OnRenameStarted;
            viewModel.RenameStarted += OnRenameStarted;
            await viewModel.InitializeAsync().ConfigureAwait(true);
        }
    }

    private void ConnectionsView_OnUnloaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ConnectionsPageViewModel viewModel)
        {
            viewModel.RenameStarted -= OnRenameStarted;
        }
    }

    private void OnRenameStarted(object? sender, ExplorerNodeViewModel node)
    {
        FocusRenameEditor(node);
    }

    private void ConnectionTree_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not ConnectionsPageViewModel viewModel)
        {
            return;
        }

        var first = true;
        foreach (var item in e.RemovedItems.OfType<ExplorerNodeViewModel>())
        {
            item.IsSelected = false;
            _ = viewModel.SelectedNodes.Remove(item);
        }

        foreach (var item in e.AddedItems.OfType<ExplorerNodeViewModel>())
        {
            viewModel.SelectNode(item, additive: !first || viewModel.SelectedNodes.Count > 0);
            first = false;
        }
    }

    private async void Node_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: ExplorerNodeViewModel node } ||
            !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed ||
            node.IsVirtual ||
            DataContext is not ConnectionsPageViewModel viewModel)
        {
            return;
        }

        _draggedNodes = viewModel.SelectedNodes.Contains(node)
            ? [.. viewModel.SelectedNodes]
            : [node];
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateText(string.Join(',', _draggedNodes.Select(item => item.Id))));
        _ = await DragDrop.DoDragDropAsync(e, transfer, DragDropEffects.Move).ConfigureAwait(true);
    }

    private void ConnectionTree_OnDragOver(object? sender, DragEventArgs e)
    {
        var target = FindNode(e.Source);
        e.DragEffects = target is null or { Kind: ExplorerNodeKind.Folder }
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void ConnectionTree_OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is ConnectionsPageViewModel viewModel && _draggedNodes.Count > 0)
        {
            _ = await viewModel.DropAsync(_draggedNodes, FindNode(e.Source)).ConfigureAwait(true);
        }

        _draggedNodes = [];
        e.Handled = true;
    }

    private static ExplorerNodeViewModel? FindNode(object? source)
    {
        return source is Control { DataContext: ExplorerNodeViewModel node } ? node : null;
    }

    private void ConnectionTree_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TreeView { SelectedItem: ExplorerNodeViewModel node })
        {
            return;
        }

        if (e.Key == Key.Enter && node.ConnectCommand.CanExecute(null))
        {
            node.ConnectCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.F2 && node.BeginRenameCommand.CanExecute(null))
        {
            node.BeginRenameCommand.Execute(null);
            FocusRenameEditor(node);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && node.DeleteCommand.CanExecute(null))
        {
            node.DeleteCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void ConnectionTree_OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is TreeView { SelectedItem: ExplorerNodeViewModel node } && node.ConnectCommand.CanExecute(null))
        {
            node.ConnectCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void InlineRename_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: ExplorerNodeViewModel node })
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            node.CommitRenameCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            node.CancelRenameCommand.Execute(null);
            e.Handled = true;
        }
    }

    private async void ClearRecent_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ConnectionsPageViewModel viewModel)
        {
            await viewModel.ClearRecentAsync().ConfigureAwait(true);
        }
    }

    private void CreateFirstConnection_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ConnectionsPageViewModel viewModel)
        {
            viewModel.RequestCreateConnection();
        }
    }

    private void NewFolder_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ConnectionsPageViewModel viewModel)
        {
            viewModel.RequestCreateFolder();
        }
    }

    /// <summary>The rename box only exists once the node flips into rename mode, so the focus has to
    /// wait for the template to swap it in.</summary>
    private void FocusRenameEditor(ExplorerNodeViewModel node)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var editor = ConnectionTree.GetVisualDescendants()
                .OfType<TextBox>()
                .FirstOrDefault(control => ReferenceEquals(control.DataContext, node));
            _ = editor?.Focus();
            editor?.SelectAll();
        });
    }

    private void ClearFilters_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ConnectionsPageViewModel viewModel)
        {
            viewModel.ClearAllFilters();
        }
    }
}
