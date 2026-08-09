using Avalonia.Controls;
using Avalonia.Interactivity;
using RemoteFlow.UI.ViewModels.Settings;

namespace RemoteFlow.UI.Views.Settings;

public sealed partial class AboutView : UserControl
{
    public AboutView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    // The view model is a singleton and initialises itself at startup, so this is normally a no-op. It is
    // here for the case where it is not: a host that builds the about box without the startup path, and
    // the first paint after a settings write, both need the toggle to show the stored value.
    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AboutViewModel viewModel)
        {
            await viewModel.InitializeAsync().ConfigureAwait(true);
        }
    }
}
