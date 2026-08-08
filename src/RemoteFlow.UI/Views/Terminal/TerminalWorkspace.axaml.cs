using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using RemoteFlow.Application.Services;
using RemoteFlow.UI.Input;
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

    private async void OpenSystemTerminal_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: TerminalSessionViewModel session } &&
            DataContext is TerminalsPageViewModel viewModel)
        {
            await viewModel.OpenInSystemTerminalAsync(session).ConfigureAwait(true);
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

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not TerminalsPageViewModel viewModel)
        {
            return;
        }

        if (e.Source is TextBox && viewModel.SelectedSession is { } searchSession)
        {
            if (e.Key == Key.Escape)
            {
                searchSession.CloseFind();
                e.Handled = true;
                FocusTerminal();
            }
            else if (e.Key == Key.Enter)
            {
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                {
                    searchSession.FindPrevious();
                }
                else
                {
                    searchSession.FindNext();
                }

                e.Handled = true;
            }

            return;
        }

        // Everything the keymap does not claim as an application command belongs to the terminal:
        // TerminalControl encodes it and raises UserInput itself. Marking the event handled must
        // happen synchronously, before the tunnelled event reaches the control.
        var router = new TerminalInputRouter(viewModel.Keymap);
        if (router.Resolve(e, viewModel) is not { } command)
        {
            return;
        }

        e.Handled = true;
        _ = RunCommandAsync(command, viewModel);
    }

    private async Task RunCommandAsync(KeymapCommand command, TerminalsPageViewModel viewModel)
    {
        await TerminalInputRouter.ExecuteAsync(command, viewModel, ToggleFullscreen).ConfigureAwait(true);
        if (viewModel.SelectedSession?.IsFindOpen == true)
        {
            FocusFindBox();
        }
        else
        {
            FocusTerminal();
        }
    }

    private void ToggleFullscreen()
    {
        if (TopLevel.GetTopLevel(this) is Window window)
        {
            window.WindowState = window.WindowState == WindowState.FullScreen
                ? WindowState.Normal
                : WindowState.FullScreen;
        }
    }

    private void FocusTerminal()
    {
        var terminal = this.GetVisualDescendants().OfType<TerminalControl>().FirstOrDefault();
        _ = terminal?.Focus();
    }

    private void FocusFindBox()
    {
        var find = this.GetVisualDescendants().OfType<TextBox>()
            .FirstOrDefault(textBox => textBox.Name == "FindTextBox" && textBox.IsVisible);
        _ = find?.Focus();
        find?.SelectAll();
    }
}
