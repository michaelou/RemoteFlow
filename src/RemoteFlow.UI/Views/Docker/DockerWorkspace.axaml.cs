using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using RemoteFlow.UI.ViewModels.Docker;

namespace RemoteFlow.UI.Views.Docker;

public sealed partial class DockerWorkspace : UserControl
{
    private const int _logsRow = 5;
    private const double _logsHeight = 260;
    private DockerWorkspaceViewModel? _viewModel;

    public DockerWorkspace()
    {
        InitializeComponent();
    }

    private async void Workspace_OnLoaded(object? sender, RoutedEventArgs e)
    {
        _ = ConnectionPicker.Focus();
        if (DataContext is not DockerWorkspaceViewModel viewModel)
        {
            return;
        }

        _viewModel = viewModel;
        viewModel.Logs.PropertyChanged += Logs_OnPropertyChanged;
        viewModel.Logs.LinesAppended += Logs_OnLinesAppended;
        SizeLogsRow(viewModel.Logs.IsOpen);
        viewModel.Activate();
        await viewModel.LoadConnectionsAsync().ConfigureAwait(true);
    }

    /// <summary>The page is rebuilt every time it is navigated to, so leaving it is the moment to stop
    /// polling the server and to let go of the view model's events.</summary>
    private void Workspace_OnUnloaded(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.Deactivate();
        _viewModel.Logs.PropertyChanged -= Logs_OnPropertyChanged;
        _viewModel.Logs.LinesAppended -= Logs_OnLinesAppended;
        _viewModel = null;
    }

    private void Workspace_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5 && _viewModel is { IsConnected: true } viewModel)
        {
            _ = viewModel.RefreshCommand.ExecuteAsync(null);
            e.Handled = true;
        }
        else if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control) && _viewModel is { IsConnected: true })
        {
            _ = FilterBox.Focus();
            FilterBox.SelectAll();
            e.Handled = true;
        }
    }

    private void ContainerList_OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_viewModel?.SelectedContainer is { } item)
        {
            _ = _viewModel.ShowLogsCommand.ExecuteAsync(item);
        }
    }

    /// <summary>Enter shows the logs, the same as a double-click; Delete removes, after asking.</summary>
    private void ContainerList_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel?.SelectedContainer is not { } item)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            _ = _viewModel.ShowLogsCommand.ExecuteAsync(item);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && item.CanRemove)
        {
            _ = _viewModel.RemoveContainerCommand.ExecuteAsync(item);
            e.Handled = true;
        }
    }

    private void Logs_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DockerLogsViewModel.IsOpen) && sender is DockerLogsViewModel logs)
        {
            SizeLogsRow(logs.IsOpen);
        }
    }

    /// <summary>Keeps the newest line in sight while following. Posted, so the list has laid the new rows
    /// out before it is asked to scroll to one.</summary>
    private void Logs_OnLinesAppended(object? sender, EventArgs e)
    {
        if (sender is not DockerLogsViewModel { Follow: true } logs || logs.Lines.Count == 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (logs.Lines.Count > 0)
            {
                LogList.ScrollIntoView(logs.Lines.Count - 1);
            }
        }, DispatcherPriority.Background);
    }

    private void SizeLogsRow(bool open)
    {
        Body.RowDefinitions[_logsRow].Height = open ? new GridLength(_logsHeight) : new GridLength(0);
    }
}
