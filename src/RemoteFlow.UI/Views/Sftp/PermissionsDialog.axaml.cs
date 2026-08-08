using Avalonia.Controls;
using Avalonia.Interactivity;
using RemoteFlow.UI.ViewModels.Sftp;

namespace RemoteFlow.UI.Views.Sftp;

public sealed partial class PermissionsDialog : Window
{
    public PermissionsDialog()
    {
        InitializeComponent();
    }

    public PermissionsDialog(SftpPermissionsEditorViewModel viewModel) : this()
    {
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }

    private async void Apply_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SftpPermissionsEditorViewModel viewModel)
        {
            _ = await viewModel.ApplyAsync().ConfigureAwait(true);
        }
    }

    private void Close_OnClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
