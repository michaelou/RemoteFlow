using Avalonia.Controls;
using Avalonia.Input;
using RemoteFlow.UI.ViewModels.Terminal;

namespace RemoteFlow.UI.Views.Terminal;

/// <summary>
/// The command library as it appears over the terminal workspace.
/// </summary>
/// <remarks>
/// The view decides nothing about what a command means: it moves the highlight, and raises
/// <see cref="InsertRequested" /> or <see cref="CloseRequested" /> for the workspace to act on. The
/// workspace owns both the session being typed into and the focus that has to come back to it.
/// </remarks>
public sealed partial class CommandLibraryView : UserControl
{
    public CommandLibraryView()
    {
        InitializeComponent();
    }

    /// <summary>Raised when the user chose a command to type at the prompt.</summary>
    public event EventHandler? InsertRequested;

    /// <summary>Raised when the user left the library without choosing anything.</summary>
    public event EventHandler? CloseRequested;

    public void FocusSearch()
    {
        _ = LibrarySearchBox.Focus();
        LibrarySearchBox.SelectAll();
    }

    private void LibrarySearchBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not CommandSnippetPaletteViewModel viewModel)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            viewModel.Close();
            CloseRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && viewModel.SelectedResult is not null)
        {
            InsertRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
        // The search box keeps the keyboard the whole time, so the arrow keys have to reach the list from
        // here; a highlight the user cannot move is a list they have to use the mouse on.
        else if (e.Key is Key.Down or Key.Up)
        {
            viewModel.MoveSelection(e.Key == Key.Down ? 1 : -1);
            ScrollToSelection(viewModel);
            e.Handled = true;
        }
    }

    private void ScrollToSelection(CommandSnippetPaletteViewModel viewModel)
    {
        if (viewModel.SelectedResult is { } selected)
        {
            LibraryResults.ScrollIntoView(selected);
        }
    }

    private void LibraryResults_OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is CommandSnippetPaletteViewModel { SelectedResult: not null })
        {
            InsertRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }
}
