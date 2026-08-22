using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using RemoteFlow.UI.ViewModels.Storage;

namespace RemoteFlow.UI.Views.Storage;

public sealed partial class StorageWorkspace : UserControl
{
    public StorageWorkspace()
    {
        InitializeComponent();
    }

    private async void Workspace_OnLoaded(object? sender, RoutedEventArgs e)
    {
        // The connection picker is the first tab stop, so the keyboard lands on the decision the page
        // exists to make rather than in the middle of a file list.
        _ = ConnectionPicker.Focus();
        if (DataContext is StoragePageViewModel viewModel)
        {
            await viewModel.InitializeLocalAsync().ConfigureAwait(true);
            await viewModel.LoadConnectionsAsync().ConfigureAwait(true);
        }
    }

    /// <summary>The explicit pane jump.
    ///
    /// <c>Tab</c> is deliberately not bound: <c>docs/accessibility.md</c> gives it to "move between
    /// controls", and hijacking it would create exactly the keyboard trap <c>F6</c> exists to escape.
    /// Because the two panes are peer controls in declaration order, <c>Tab</c> already walks local to
    /// remote for free. <c>F6</c> is not reused either — ADR-0009 makes it mean "escape the keyboard
    /// trap" application-wide — and neither are <c>Ctrl+Tab</c> or <c>Alt+1</c>/<c>Alt+2</c>, which the
    /// terminal claims.</summary>
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
            _ = RemotePane.FocusList();
            e.Handled = true;
        }
        else if (e.Key == Key.L)
        {
            _ = FocusedPane().FocusPathBox();
            e.Handled = true;
        }
    }

    /// <summary>Whichever pane currently holds focus, and the local one when neither does.</summary>
    private FileBrowserPane FocusedPane()
    {
        return RemotePane.IsKeyboardFocusWithin ? RemotePane : LocalPane;
    }
}
