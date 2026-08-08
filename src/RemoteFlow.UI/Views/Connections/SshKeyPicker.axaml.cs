using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using RemoteFlow.UI.ViewModels.Connections;

namespace RemoteFlow.UI.Views.Connections;

public sealed partial class SshKeyPicker : UserControl
{
    public SshKeyPicker()
    {
        InitializeComponent();
    }

    private async void Browse_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SshKeyPickerViewModel viewModel || TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            return;
        }
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose an SSH private key",
            AllowMultiple = false,
        });
        if (files.Count == 1)
        {
            viewModel.SelectedPath = files[0].TryGetLocalPath();
            await viewModel.InspectAsync().ConfigureAwait(true);
        }
    }

    private async void Generate_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SshKeyPickerViewModel viewModel || TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            return;
        }
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Generate an Ed25519 private key",
            SuggestedFileName = "id_ed25519",
        });
        if (file?.TryGetLocalPath() is { } path)
        {
            await viewModel.GenerateAsync(path).ConfigureAwait(true);
        }
    }

    private async void CopyPublicKey_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SshKeyPickerViewModel viewModel)
        {
            await viewModel.CopyPublicKeyAsync().ConfigureAwait(true);
        }
    }
}
