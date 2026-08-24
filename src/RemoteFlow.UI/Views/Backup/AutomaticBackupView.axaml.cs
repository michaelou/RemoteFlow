using Avalonia.Controls;
using RemoteFlow.UI.ViewModels.Backup;

namespace RemoteFlow.UI.Views.Backup;

public partial class AutomaticBackupView : UserControl
{
    public AutomaticBackupView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is AutomaticBackupSettingsViewModel viewModel)
        {
            await viewModel.InitializeAsync().ConfigureAwait(true);
        }
    }
}
