using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RemoteFlow.Application.Services;
using RemoteFlow.UI.Input;
using RemoteFlow.UI.ViewModels.Terminal;
using SvcSystems.UI.Terminal;

namespace RemoteFlow.UI.Views.Terminal;

public sealed partial class TerminalWorkspace : UserControl
{
    /// <summary>How far the pointer travels before a press on a tab or a tile header becomes a drag. Below
    /// it, the press was a click.</summary>
    private const double _dragThreshold = 6;

    private const string _dragSourceClass = "tile-drag-source";
    private const string _dropTargetClass = "tile-drop-target";
    private const string _draggingTabClass = "dragging";

    private readonly Dictionary<Control, IDisposable> _containerTiling = [];
    private TerminalWorkspaceViewModel? _observedWorkspace;
    private IWorkspaceSessionViewModel? _dragSession;
    private DragSurface _dragSurface;
    private Point _pressPosition;
    private bool _isDragging;
    private int _dragOriginIndex;
    private Control? _dropTile;

    private enum DragSurface
    {
        Tab,
        Tile,
    }

    public TerminalWorkspace()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        // A drag captures the pointer to the workspace itself: live reordering rebuilds the tab under the
        // pointer, and a tile drag crosses other tiles — neither the pressed element nor what lies beneath
        // the pointer can be trusted to keep receiving the gesture. Handled events too, because a terminal
        // marks the moves it sees as its own.
        AddHandler(PointerMovedEvent, OnDragPointerMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnDragPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, OnDragCaptureLost, RoutingStrategies.Direct);
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
            DataContext is not TerminalWorkspaceViewModel viewModel)
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
        BeginPress(session, DragSurface.Tab, e);
        if (session is IWorkspaceSessionFocusTarget)
        {
            _ = control.Focus(NavigationMethod.Pointer);
        }
        else
        {
            FocusTerminal();
        }
    }

    /// <summary>Enter or Space selects the focused tab; Delete closes it; Ctrl+Shift+Left and Right move it.
    /// The tab keeps focus after a selection so a keyboard user can keep moving along the strip, and hands
    /// focus to the terminal only when they ask for the session itself.</summary>
    private async void Tab_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not Control { DataContext: IWorkspaceSessionViewModel session } ||
            DataContext is not TerminalWorkspaceViewModel viewModel)
        {
            return;
        }

        if (e.Key is Key.Left or Key.Right &&
            e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            // Moving the session rebuilds its tab, and the new one does not have the focus the old one did.
            if (viewModel.MoveSessionBy(session, e.Key == Key.Left ? -1 : 1))
            {
                Dispatcher.UIThread.Post(() => FocusTab(session), DispatcherPriority.Loaded);
            }

            e.Handled = true;
        }
        else if (e.Key is Key.Enter or Key.Space)
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

    /// <summary>The header is the only part of a tile that is the workspace's own to handle: everything
    /// below it belongs to the session. Pressing it selects the session; dragging it moves the tile.</summary>
    private void TileHeader_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: IWorkspaceSessionViewModel session } control ||
            DataContext is not TerminalWorkspaceViewModel viewModel ||
            !e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
        {
            return;
        }

        viewModel.SelectSession(session);
        BeginPress(session, DragSurface.Tile, e);
        FocusTerminal();
        e.Handled = true;
    }

    private void BeginPress(IWorkspaceSessionViewModel session, DragSurface surface, PointerEventArgs e)
    {
        _dragSession = session;
        _dragSurface = surface;
        _pressPosition = e.GetPosition(this);
        _isDragging = false;
    }

    private void OnDragPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragSession is null || DataContext is not TerminalWorkspaceViewModel viewModel)
        {
            return;
        }

        // The button can come up outside the window, where the release is never seen.
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            EndDrag(commit: false);
            return;
        }

        if (!_isDragging)
        {
            var position = e.GetPosition(this);
            if (Math.Abs(position.X - _pressPosition.X) <= _dragThreshold &&
                Math.Abs(position.Y - _pressPosition.Y) <= _dragThreshold)
            {
                return;
            }

            _isDragging = true;
            _dragOriginIndex = viewModel.Sessions.IndexOf(_dragSession);
            e.Pointer.Capture(this);
            Cursor = new Cursor(StandardCursorType.DragMove);
            MarkDragSource();
        }

        if (_dragSurface == DragSurface.Tab)
        {
            ReorderTabUnderPointer(e, viewModel);
        }
        else
        {
            MarkDropTile(TileUnderPointer(e));
        }

        e.Handled = true;
    }

    private void OnDragPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragSession is null)
        {
            return;
        }

        if (_isDragging)
        {
            e.Handled = true;
        }

        EndDrag(commit: true);
    }

    private void OnDragCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_isDragging && ReferenceEquals(e.Source, this))
        {
            EndDrag(commit: false);
        }
    }

    /// <summary>
    /// Tabs reorder live, the way a browser's do: the dragged tab takes the place of whichever one the
    /// pointer is over.
    /// </summary>
    /// <remarks>
    /// A tab only swaps in once the pointer is far enough across the other one that, after the swap, it is
    /// still over the dragged tab. Swapping on first contact makes a narrow tab dragged over a wide one land
    /// with the pointer back over the wide one, which swaps them again — the pair flickers back and forth.
    /// </remarks>
    private void ReorderTabUnderPointer(PointerEventArgs e, TerminalWorkspaceViewModel viewModel)
    {
        var session = _dragSession!;
        var dragged = TabFor(session);
        var draggedWidth = dragged?.Bounds.Width ?? 0;
        if (draggedWidth <= 0)
        {
            // The tab was rebuilt by the last move and has not been laid out yet.
            return;
        }

        foreach (var tab in Tabs())
        {
            if (tab.DataContext is not IWorkspaceSessionViewModel target || ReferenceEquals(target, session))
            {
                continue;
            }

            var x = e.GetPosition(tab).X;
            var width = tab.Bounds.Width;
            if (x < 0 || x > width)
            {
                continue;
            }

            var movingRight = viewModel.Sessions.IndexOf(target) > viewModel.Sessions.IndexOf(session);
            var settles = movingRight ? x >= width - draggedWidth : x <= draggedWidth;
            if (settles)
            {
                viewModel.MoveSession(session, target);
                Dispatcher.UIThread.Post(MarkDragSource, DispatcherPriority.Loaded);
            }

            return;
        }
    }

    private Control? TileUnderPointer(PointerEventArgs e)
    {
        if (SessionContent.ItemsPanelRoot is not WorkspaceSessionTilePanel panel)
        {
            return null;
        }

        foreach (var tile in panel.Children)
        {
            if (WorkspaceSessionTilePanel.IsTile(tile) &&
                new Rect(tile.Bounds.Size).Contains(e.GetPosition(tile)))
            {
                return tile;
            }
        }

        return null;
    }

    private void MarkDropTile(Control? tile)
    {
        // Dropping a tile on itself goes nowhere, so it is not marked as somewhere to go.
        if (tile is not null && ReferenceEquals(SessionContent.ItemFromContainer(tile), _dragSession))
        {
            tile = null;
        }

        if (ReferenceEquals(tile, _dropTile))
        {
            return;
        }

        _ = _dropTile?.Classes.Remove(_dropTargetClass);
        _dropTile = tile;
        _dropTile?.Classes.Add(_dropTargetClass);
    }

    /// <summary>Fades whatever stands for the session being dragged: its tab, or in a tile drag its tile.</summary>
    private void MarkDragSource()
    {
        if (_dragSession is null || !_isDragging)
        {
            return;
        }

        if (_dragSurface == DragSurface.Tab)
        {
            foreach (var tab in Tabs())
            {
                tab.Classes.Set(_draggingTabClass, ReferenceEquals(tab.DataContext, _dragSession));
            }
        }
        else if (SessionContent.ContainerFromItem(_dragSession) is { } tile)
        {
            tile.Classes.Add(_dragSourceClass);
        }
    }

    /// <summary>
    /// Finishes the gesture. A tile moves only now, on release: a grid that rearranged itself while the
    /// pointer crossed it would resize every terminal it passed over. A cancelled tab drag puts the tab back
    /// where it started, since its moves were made as it went.
    /// </summary>
    private void EndDrag(bool commit)
    {
        var session = _dragSession;
        var wasDragging = _isDragging;
        var dropTile = _dropTile;
        _dragSession = null;
        _isDragging = false;
        MarkDropTile(null);
        if (!wasDragging || session is null)
        {
            return;
        }

        ClearValue(CursorProperty);
        foreach (var tab in Tabs())
        {
            _ = tab.Classes.Remove(_draggingTabClass);
        }

        if (SessionContent.ContainerFromItem(session) is { } tile)
        {
            _ = tile.Classes.Remove(_dragSourceClass);
        }

        if (DataContext is not TerminalWorkspaceViewModel viewModel)
        {
            return;
        }

        if (_dragSurface == DragSurface.Tile && commit &&
            dropTile is not null &&
            SessionContent.ItemFromContainer(dropTile) is IWorkspaceSessionViewModel target)
        {
            viewModel.MoveSession(session, target);
        }
        else if (_dragSurface == DragSurface.Tab && !commit &&
            _dragOriginIndex >= 0 && _dragOriginIndex < viewModel.Sessions.Count)
        {
            viewModel.MoveSession(session, viewModel.Sessions[_dragOriginIndex]);
        }
    }

    private IEnumerable<Border> Tabs()
    {
        return TabScroller.GetVisualDescendants()
            .OfType<Border>()
            .Where(border => border.Classes.Contains("session-tab"));
    }

    private Border? TabFor(IWorkspaceSessionViewModel? session)
    {
        return Tabs().FirstOrDefault(tab => ReferenceEquals(tab.DataContext, session));
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
            index < 0 || index >= viewModel.HostedSessions.Count)
        {
            return;
        }

        var session = viewModel.HostedSessions[index];
        WorkspaceSessionTilePanel.SetTileOrder(container, viewModel.Sessions.IndexOf(session));
        _containerTiling[container] = container.Bind(
            WorkspaceSessionTilePanel.IsTileShownProperty,
            new Binding(nameof(IWorkspaceSessionViewModel.IsContentVisible))
            {
                Source = session,
            });
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        _observedWorkspace?.Sessions.CollectionChanged -= OnSessionOrderChanged;
        _observedWorkspace = DataContext as TerminalWorkspaceViewModel;
        _observedWorkspace?.Sessions.CollectionChanged += OnSessionOrderChanged;
    }

    /// <summary>Hands every tile its place in the user's order. Any change to the order can move any tile,
    /// and there are never more than a handful of them.</summary>
    private void OnSessionOrderChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (DataContext is not TerminalWorkspaceViewModel viewModel)
        {
            return;
        }

        foreach (var container in SessionContent.GetRealizedContainers())
        {
            if (SessionContent.ItemFromContainer(container) is IWorkspaceSessionViewModel session)
            {
                WorkspaceSessionTilePanel.SetTileOrder(container, viewModel.Sessions.IndexOf(session));
            }
        }
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

    /// <summary>Closing a session shifts the index of every container after it without clearing them.</summary>
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

        if (_isDragging)
        {
            // Escape abandons a drag; nothing else typed in the middle of one is meant for the terminal.
            if (e.Key == Key.Escape)
            {
                EndDrag(commit: false);
            }

            e.Handled = true;
            return;
        }

        if (viewModel.CommandLibrary.IsOpen)
        {
            // The library holds the keyboard while it is open, and its search box is a TextBox like the
            // find bar's: without this its Escape and Enter would be read as the find bar's.
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
        if (viewModel.CommandLibrary.IsOpen)
        {
            FocusCommandLibrary();
        }
        else if (viewModel.SelectedTerminalSession?.IsFindOpen == true)
        {
            FocusFindBox();
        }
        else
        {
            FocusTerminal();
        }
    }

    /// <summary>Types the chosen command at the prompt and gives the terminal the keyboard back, so the
    /// Enter that runs it is the user's own.</summary>
    private async void CommandLibrary_OnInsertRequested(object? sender, EventArgs e)
    {
        if (DataContext is TerminalsPageViewModel viewModel)
        {
            _ = await viewModel.InsertSelectedCommandAsync().ConfigureAwait(true);
            FocusTerminal();
        }
    }

    private void CommandLibrary_OnCloseRequested(object? sender, EventArgs e)
    {
        FocusTerminal();
    }

    private void CommandLibraryOverlay_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ReferenceEquals(e.Source, CommandLibraryOverlay) && DataContext is TerminalsPageViewModel viewModel)
        {
            viewModel.CommandLibrary.Close();
            FocusTerminal();
            e.Handled = true;
        }
    }

    private void FocusCommandLibrary()
    {
        // Posted rather than called: the overlay becomes visible with the property change that opened it,
        // and a control that is not yet visible cannot take focus.
        Dispatcher.UIThread.Post(CommandLibraryPanel.FocusSearch);
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
        FocusTab((DataContext as TerminalsPageViewModel)?.SelectedSession);
    }

    private void FocusTab(IWorkspaceSessionViewModel? session)
    {
        _ = TabFor(session)?.Focus(NavigationMethod.Tab) ?? Focus(NavigationMethod.Tab);
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
