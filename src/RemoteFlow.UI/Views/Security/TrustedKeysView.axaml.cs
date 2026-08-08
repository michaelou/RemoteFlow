using Avalonia.Controls;
using Avalonia.Interactivity;
using RemoteFlow.UI.ViewModels.Security;

namespace RemoteFlow.UI.Views.Security;

public sealed partial class TrustedKeysView : UserControl
{
    public TrustedKeysView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TrustedKeysViewModel viewModel)
        {
            await viewModel.LoadAsync().ConfigureAwait(true);
        }
    }
}
