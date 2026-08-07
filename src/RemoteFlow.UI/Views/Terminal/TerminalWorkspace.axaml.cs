using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using RemoteFlow.UI.ViewModels.Terminal;
using SvcSystems.UI.Terminal;

namespace RemoteFlow.UI.Views.Terminal;

public sealed partial class TerminalWorkspace : UserControl
{
    private TerminalSessionViewModel? _pressedTab;
    private Point _pressPosition;
    private bool _isDraggingTab;

    public TerminalWorkspace()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TerminalsPageViewModel viewModel)
        {
            await viewModel.InitializeAsync().ConfigureAwait(true);
            FocusTerminal();
        }
    }

    private async void AddTab_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TerminalsPageViewModel viewModel)
        {
            _ = await viewModel.AddLocalSessionAsync().ConfigureAwait(true);
            FocusTerminal();
        }
    }

    private async void CloseTab_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: TerminalSessionViewModel session } &&
            DataContext is TerminalsPageViewModel viewModel)
        {
            _ = await viewModel.CloseSessionAsync(session).ConfigureAwait(true);
            FocusTerminal();
            e.Handled = true;
        }
    }

    private async void Tab_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: TerminalSessionViewModel session } control ||
            DataContext is not TerminalsPageViewModel viewModel)
        {
            return;
        }

        var properties = e.GetCurrentPoint(control).Properties;
        if (properties.IsMiddleButtonPressed)
        {
            _ = await viewModel.CloseSessionAsync(session).ConfigureAwait(true);
            e.Handled = true;
            return;
        }

        if (!properties.IsLeftButtonPressed)
        {
            return;
        }

        viewModel.SelectSession(session);
        _pressedTab = session;
        _pressPosition = e.GetPosition(this);
        _isDraggingTab = false;
        FocusTerminal();
    }

    private void Tab_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressedTab is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var position = e.GetPosition(this);
        _isDraggingTab = Math.Abs(position.X - _pressPosition.X) > 6 ||
            Math.Abs(position.Y - _pressPosition.Y) > 6;
    }

    private void Tab_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isDraggingTab && _pressedTab is not null &&
            sender is Control { DataContext: TerminalSessionViewModel target } &&
            DataContext is TerminalsPageViewModel viewModel)
        {
            viewModel.MoveSession(_pressedTab, target);
        }

        _pressedTab = null;
        _isDraggingTab = false;
    }

    private void TerminalBorder_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        FocusTerminal();
    }

    private async void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not TerminalsPageViewModel viewModel)
        {
            return;
        }

        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        if (control && shift && e.Key == Key.T)
        {
            e.Handled = true;
            _ = await viewModel.AddLocalSessionAsync().ConfigureAwait(true);
            FocusTerminal();
        }
        else if (control && shift && e.Key == Key.W && viewModel.SelectedSession is { } selected)
        {
            e.Handled = true;
            _ = await viewModel.CloseSessionAsync(selected).ConfigureAwait(true);
            FocusTerminal();
        }
        else if (control && e.Key == Key.Tab)
        {
            viewModel.CycleSession(shift);
            e.Handled = true;
            FocusTerminal();
        }
        else if (alt && !control && !shift && e.Key is >= Key.D1 and <= Key.D9)
        {
            viewModel.SelectSession((int)e.Key - (int)Key.D1 + 1);
            e.Handled = true;
            FocusTerminal();
        }
    }

    private void FocusTerminal()
    {
        var terminal = this.GetVisualDescendants().OfType<TerminalControl>().FirstOrDefault();
        _ = terminal?.Focus();
    }
}
