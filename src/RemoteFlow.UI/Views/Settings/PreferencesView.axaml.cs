using Avalonia.Controls;
using Avalonia.Interactivity;
using RemoteFlow.UI.ViewModels.Settings;

namespace RemoteFlow.UI.Views.Settings;

public sealed partial class PreferencesView : UserControl
{
    public PreferencesView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PreferencesViewModel viewModel)
        {
            await viewModel.InitializeAsync().ConfigureAwait(true);
        }
    }
}
