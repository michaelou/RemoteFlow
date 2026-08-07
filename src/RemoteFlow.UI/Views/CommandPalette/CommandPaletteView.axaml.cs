using Avalonia.Controls;
using Avalonia.Input;
using RemoteFlow.UI.ViewModels.CommandPalette;

namespace RemoteFlow.UI.Views.CommandPalette;

public sealed partial class CommandPaletteView : UserControl
{
    public CommandPaletteView()
    {
        InitializeComponent();
    }

    public event EventHandler? CloseRequested;

    public void FocusSearch()
    {
        _ = PaletteSearchBox.Focus();
        PaletteSearchBox.SelectAll();
    }

    private async void PaletteSearchBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not CommandPaletteViewModel viewModel)
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
            if (await viewModel.ConnectSelectedAsync().ConfigureAwait(true))
            {
                CloseRequested?.Invoke(this, EventArgs.Empty);
            }

            e.Handled = true;
        }
        else if (e.Key == Key.Down && PaletteResults.ItemCount > 0)
        {
            PaletteResults.SelectedIndex = Math.Min(PaletteResults.SelectedIndex + 1, PaletteResults.ItemCount - 1);
            e.Handled = true;
        }
        else if (e.Key == Key.Up && PaletteResults.ItemCount > 0)
        {
            PaletteResults.SelectedIndex = Math.Max(PaletteResults.SelectedIndex - 1, 0);
            e.Handled = true;
        }
    }

    private async void PaletteResults_OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is CommandPaletteViewModel viewModel &&
            await viewModel.ConnectSelectedAsync().ConfigureAwait(true))
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
