using Avalonia.Controls;
using Avalonia.Interactivity;
using RemoteFlow.UI.ViewModels.Connections;

namespace RemoteFlow.UI.Views.Connections;

public sealed partial class SearchBar : UserControl
{
    public SearchBar()
    {
        InitializeComponent();
    }

    private void ClearAll_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ConnectionsPageViewModel viewModel)
        {
            viewModel.ClearAllFilters();
            _ = SearchTextBox.Focus();
        }
    }
}
