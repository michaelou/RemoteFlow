using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RemoteFlow.UI.ViewModels.Storage;
using RemoteFlow.UI.Views.Storage;
using Xunit;

namespace RemoteFlow.UI.Tests;

/// <summary>The keyboard path across the Storage page. <c>Tab</c> is deliberately not bound — hijacking it
/// would create exactly the keyboard trap <c>F6</c> exists to escape — so what has to be true is that the
/// panes are peer controls in declaration order and <c>Tab</c> already walks between them.</summary>
public sealed class StorageKeyboardTests
{
    [AvaloniaFact]
    public void ThePageOpensOnTheConnectionPickerAndTabWalksFromTheLocalListToTheRemoteOne()
    {
        var fixture = StorageTestDoubles.CreateFixture();
        var window = Show(fixture.Page);
        try
        {
            var picker = window.GetVisualDescendants().OfType<ComboBox>()
                .First(box => box.Name == "ConnectionPicker");
            var panes = window.GetVisualDescendants().OfType<FileBrowserPane>().ToArray();

            // The first tab stop is the decision the page exists to make, not the middle of a file list.
            Assert.True(picker.IsFocused, "The page did not open on the account picker.");
            Assert.Equal(2, panes.Length);

            // Tab is deliberately unbound: the panes are peer controls in declaration order, so Tab walks
            // local to remote for free, and nothing here can become the keyboard trap F6 exists to escape.
            var order = TabOrder(window, panes[0], panes[1]);
            Assert.Equal(["local", "remote"], order);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void TheExplicitPaneJumpMovesFocusBetweenTheTwoLists()
    {
        var fixture = StorageTestDoubles.CreateFixture();
        var window = Show(fixture.Page);
        try
        {
            var workspace = window.GetVisualDescendants().OfType<StorageWorkspace>().Single();
            var panes = window.GetVisualDescendants().OfType<FileBrowserPane>().ToArray();

            workspace.RaiseEvent(PaneJump(Key.Right));
            Dispatcher.UIThread.RunJobs();
            Assert.True(panes[1].IsKeyboardFocusWithin, "Ctrl+Shift+Right did not focus the remote pane.");

            workspace.RaiseEvent(PaneJump(Key.Left));
            Dispatcher.UIThread.RunJobs();
            Assert.True(panes[0].IsKeyboardFocusWithin, "Ctrl+Shift+Left did not focus the local pane.");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task RefreshNewFolderAndDeleteReachTheFocusedPane()
    {
        var token = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "remoteflow-keys-" + Path.GetRandomFileName());
        _ = Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "one.txt"), "x", token);
            var fixture = StorageTestDoubles.CreateFixture(confirmationResult: false);
            _ = await fixture.Page.Local.NavigateAsync(root, token);
            var window = Show(fixture.Page);
            try
            {
                var local = window.GetVisualDescendants().OfType<ListBox>()
                    .First(list => list.Name == "EntryList");
                _ = local.Focus();
                Dispatcher.UIThread.RunJobs();

                local.RaiseEvent(KeyPress(Key.F7));
                Assert.True(fixture.Page.Local.IsCreatingFolder, "F7 did not start a new folder.");
                fixture.Page.Local.CancelCreateFolder();

                local.SelectedIndex = 0;
                fixture.Page.Local.SetSelection([fixture.Page.Local.Items[0]]);
                local.RaiseEvent(KeyPress(Key.Delete));
                Dispatcher.UIThread.RunJobs();

                // Confirmation-gated: the file is still there because the stub said no.
                Assert.NotEmpty(fixture.Confirmation.Messages);
                Assert.True(File.Exists(Path.Combine(root, "one.txt")));

                local.RaiseEvent(KeyPress(Key.F5));
                Dispatcher.UIThread.RunJobs();
                Assert.NotEmpty(fixture.Page.Local.Items);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static KeyEventArgs KeyPress(Key key)
    {
        return new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key };
    }

    private static KeyEventArgs PaneJump(Key key)
    {
        return new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = KeyModifiers.Control | KeyModifiers.Shift,
        };
    }

    /// <summary>Presses Tab and records which pane the keyboard lands in, which is the only honest way to
    /// assert a tab order: it drives the real keyboard-navigation handler rather than an internal helper.
    /// </summary>
    private static List<string> TabOrder(Window window, Control local, Control remote)
    {
        var visited = new List<string>();
        for (var step = 0; step < 80; step++)
        {
            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            if (local.IsKeyboardFocusWithin && visited.LastOrDefault() != "local")
            {
                visited.Add("local");
            }
            else if (remote.IsKeyboardFocusWithin && visited.LastOrDefault() != "remote")
            {
                visited.Add("remote");
            }

            if (visited.Count == 2)
            {
                break;
            }
        }

        return visited;
    }

    /// <summary>The routing fact both file pages' drags depend on, and the one that made them silently
    /// impossible: a press on a row is already handled by the time it reaches the list.
    ///
    /// <c>ListBoxItem</c> is a child of the <c>ListBox</c>, so it sees the bubbling press first, and
    /// <c>SelectingItemsControl.UpdateSelectionFromEvent</c> sets <c>Handled</c> as soon as the press
    /// triggers selection. A <c>PointerPressed</c> handler declared in XAML asks only for unhandled
    /// events, so it never ran on a row at all — no drag out of either pane could start, in any
    /// direction. Both pages now attach that handler in code with <c>handledEventsToo</c>.
    ///
    /// This is behaviour of Avalonia 12.1.1, which Directory.Packages.props already says to upgrade by
    /// hand: if an upgrade stops marking the press handled, this test says so before the drags do.
    /// </summary>
    [AvaloniaFact]
    public async Task APressOnARowIsHandledByTheRowSoADragHandlerHasToAskForHandledEvents()
    {
        var token = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "remoteflow-drag-" + Path.GetRandomFileName());
        _ = Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "one.txt"), "x", token);
            var fixture = StorageTestDoubles.CreateFixture();
            _ = await fixture.Page.Local.NavigateAsync(root, token);
            var window = Show(fixture.Page);
            try
            {
                var list = window.GetVisualDescendants().OfType<ListBox>()
                    .First(box => box.Name == "EntryList");
                window.UpdateLayout();
                var container = Assert.IsAssignableFrom<Control>(list.ContainerFromIndex(0));

                var plain = 0;
                var handledToo = 0;
                list.AddHandler(InputElement.PointerPressedEvent, (_, _) => plain++);
                list.AddHandler(
                    InputElement.PointerPressedEvent,
                    (_, _) => handledToo++,
                    handledEventsToo: true);

                container.RaiseEvent(PressOn(container, window));

                Assert.Equal(0, plain);
                Assert.Equal(1, handledToo);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A left press in the middle of <paramref name="target"/>. The position matters: the row
    /// only claims the press — and only marks it handled — while the pointer is inside its bounds.
    /// Raised on the container rather than sent through the window, because headless hit-testing is not
    /// reliable enough to pin a routing fact on.</summary>
    private static PointerPressedEventArgs PressOn(Control target, Window window)
    {
        var middle = new Point(target.Bounds.Width / 2, target.Bounds.Height / 2);
        var centre = target.TranslatePoint(middle, window) ?? default;
        return new PointerPressedEventArgs(
            target,
            new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true),
            window,
            centre,
            0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None);
    }

    private static Window Show(StoragePageViewModel page)
    {
        var window = new Window
        {
            Width = 1200,
            Height = 800,
            Content = new StorageWorkspace { DataContext = page },
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return window;
    }
}
