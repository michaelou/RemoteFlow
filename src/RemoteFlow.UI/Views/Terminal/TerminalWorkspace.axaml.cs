using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
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
    private readonly Dictionary<Control, IDisposable> _containerTiling = [];
    private IWorkspaceSessionViewModel? _pressedTab;
    private Point _pressPosition;
    private bool _isDraggingTab;

    public TerminalWorkspace()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(
            WorkspaceSessionContentHost.FocusEscapeRequestedEvent,
            OnFocusEscapeRequested,
            RoutingStrategies.Bubble);
        AddHandler(
            WorkspaceSessionContentHost.SelectionRequestedEvent,
            OnSelectionRequested,
            RoutingStrategies.Bubble);
        // Selection follows the keyboard. Every application command — close, copy, paste, find — acts on the
        // selected session, and in a grid the session being typed into is no longer the only one on screen.
        AddHandler(GotFocusEvent, OnSessionContentGotFocus, RoutingStrategies.Bubble);
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TerminalsPageViewModel viewModel)
        {
            await viewModel.InitializeAsync().ConfigureAwait(true);
            FocusTerminal();
        }
    }

    private async void Shortcuts_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TerminalsPageViewModel viewModel ||
            TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var dialog = new TerminalShortcutsDialog(
            new TerminalShortcutsViewModel(viewModel.Keymap, viewModel.CtrlCPolicy));
        await dialog.ShowDialog(owner).ConfigureAwait(true);
        FocusTerminal();
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
        if (sender is Control { DataContext: IWorkspaceSessionViewModel session } &&
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
        if (sender is not Control { DataContext: IWorkspaceSessionViewModel session } control ||
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
        if (session is IWorkspaceSessionFocusTarget)
        {
            _ = control.Focus(NavigationMethod.Pointer);
        }
        else
        {
            FocusTerminal();
        }
    }

    /// <summary>Enter or Space selects the focused tab; Delete closes it. The tab keeps focus after a
    /// selection so a keyboard user can keep moving along the strip, and hands focus to the terminal
    /// only when they ask for the session itself.</summary>
    private async void Tab_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not Control { DataContext: IWorkspaceSessionViewModel session } ||
            DataContext is not TerminalsPageViewModel viewModel)
        {
            return;
        }

        if (e.Key is Key.Enter or Key.Space)
        {
            viewModel.SelectSession(session);
            e.Handled = true;
            FocusTerminal();
        }
        else if (e.Key == Key.Delete)
        {
            _ = await viewModel.CloseSessionAsync(session).ConfigureAwait(true);
            e.Handled = true;
        }
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
            sender is Control { DataContext: IWorkspaceSessionViewModel target } &&
            DataContext is TerminalsPageViewModel viewModel)
        {
            viewModel.MoveSession(_pressedTab, target);
        }

        _pressedTab = null;
        _isDraggingTab = false;
    }

    private void TerminalBorder_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Clicking into a tile selects that tile, not whichever one was selected before.
        _ = SelectSessionFrom(e.Source);
        FocusTerminal();
    }

    private void OnFocusEscapeRequested(object? sender, RoutedEventArgs e)
    {
        // The surface asking to be left is not necessarily the selected one when every session is on screen,
        // and the tab focus below follows the selection.
        _ = SelectSessionFrom(e.Source);
        FocusTabStrip();
        e.Handled = true;
    }

    private void OnSelectionRequested(object? sender, RoutedEventArgs e)
    {
        e.Handled = SelectSessionFrom(e.Source);
    }

    private void OnSessionContentGotFocus(object? sender, FocusChangedEventArgs e)
    {
        _ = SelectSessionFrom(e.Source);
    }

    /// <summary>Selects the session whose content the event came from, if it came from one at all. Focus
    /// moves nothing here, so this cannot loop back on itself.</summary>
    private bool SelectSessionFrom(object? source)
    {
        if (DataContext is not TerminalWorkspaceViewModel viewModel || source is not Visual visual)
        {
            return false;
        }

        var session = visual.GetSelfAndVisualAncestors()
            .OfType<WorkspaceSessionContentHost>()
            .FirstOrDefault()?.Session;
        if (session is null || !viewModel.Sessions.Contains(session))
        {
            return false;
        }

        viewModel.SelectSession(session);
        return true;
    }

    /// <summary>
    /// Tells each item container whether its session holds a cell of the grid.
    /// </summary>
    /// <remarks>
    /// The panel lays out the containers the <c>ItemsControl</c> generates, and a container is always visible
    /// however hidden its contents are — so the panel is told which ones to tile rather than reading it off
    /// their visibility. Collapsing the container instead would stop its content from ever being realized,
    /// and every session's content has to stay attached for a remote desktop to keep its native window.
    /// </remarks>
    private void BindContainerTiling(Control container, int index)
    {
        ReleaseContainerTiling(container);
        if (DataContext is not TerminalWorkspaceViewModel viewModel ||
            index < 0 || index >= viewModel.Sessions.Count)
        {
            return;
        }

        _containerTiling[container] = container.Bind(
            WorkspaceSessionTilePanel.IsTileShownProperty,
            new Binding(nameof(IWorkspaceSessionViewModel.IsContentVisible))
            {
                Source = viewModel.Sessions[index],
            });
    }

    private void ReleaseContainerTiling(Control container)
    {
        if (_containerTiling.Remove(container, out var binding))
        {
            binding.Dispose();
        }
    }

    private void SessionContent_OnContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        BindContainerTiling(e.Container, e.Index);
    }

    /// <summary>Reordering hands a container a different session without clearing it.</summary>
    private void SessionContent_OnContainerIndexChanged(object? sender, ContainerIndexChangedEventArgs e)
    {
        BindContainerTiling(e.Container, e.NewIndex);
    }

    private void SessionContent_OnContainerClearing(object? sender, ContainerClearingEventArgs e)
    {
        ReleaseContainerTiling(e.Container);
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not TerminalsPageViewModel viewModel)
        {
            return;
        }

        if (e.Source is TextBox && viewModel.SelectedTerminalSession is { } searchSession)
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

        if (IsModifierOnly(e.Key) && viewModel.SelectedTerminalSession?.Model.HasSelection == true)
        {
            // A chord reaches the application as two events: the modifier going down, then the key. The
            // terminal clears its selection for any key it is given, so letting the bare modifier through
            // wiped the selection that Ctrl+Insert, Ctrl+Shift+C or copy-on-select was about to read — every
            // keyboard copy silently did nothing after selecting with the mouse. A modifier on its own sends
            // no bytes to the PTY, so nothing is lost by keeping it out of the terminal; an ordinary key
            // still reaches it and still clears the selection, which is what a user expects from typing.
            e.Handled = true;
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

    private static bool IsModifierOnly(Key key)
    {
        return key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or
            Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;
    }

    private async Task RunCommandAsync(KeymapCommand command, TerminalsPageViewModel viewModel)
    {
        // The terminal swallows Tab as a byte, so this is the only way back out to the rest of the
        // application. The tab strip is the nearest stop, and Tab continues from there.
        if (command == KeymapCommand.LeaveTerminal)
        {
            FocusTabStrip();
            return;
        }

        await TerminalInputRouter.ExecuteAsync(command, viewModel, ToggleFullscreen).ConfigureAwait(true);
        if (viewModel.SelectedTerminalSession?.IsFindOpen == true)
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
        if (DataContext is TerminalsPageViewModel { SelectedSession: IWorkspaceSessionFocusTarget focusTarget })
        {
            _ = focusTarget.FocusSessionContent();
            return;
        }

        var selected = (DataContext as TerminalsPageViewModel)?.SelectedTerminalSession;
        var terminal = this.GetVisualDescendants()
            .OfType<TerminalControl>()
            .FirstOrDefault(control => ReferenceEquals(control.DataContext, selected));
        _ = terminal?.Focus();
    }

    /// <summary>Focus the tab of the session on screen, falling back to the workspace itself when there
    /// are no sessions at all.</summary>
    private void FocusTabStrip()
    {
        var selected = (DataContext as TerminalsPageViewModel)?.SelectedSession;
        var tab = TabScroller.GetVisualDescendants()
            .OfType<Border>()
            .FirstOrDefault(border => border.Focusable && ReferenceEquals(border.DataContext, selected));
        _ = tab?.Focus(NavigationMethod.Tab) ?? Focus(NavigationMethod.Tab);
    }

    private void FocusFindBox()
    {
        // Every tile carries its own find bar, so the visible one is not necessarily the right one.
        var selected = (DataContext as TerminalWorkspaceViewModel)?.SelectedTerminalSession;
        var find = this.GetVisualDescendants().OfType<TextBox>()
            .FirstOrDefault(textBox => textBox.Name == "FindTextBox" && textBox.IsVisible &&
                ReferenceEquals(textBox.DataContext, selected));
        _ = find?.Focus();
        find?.SelectAll();
    }
}
