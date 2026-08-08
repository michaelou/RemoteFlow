using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
            SuggestedStartLocation = await GetKeyFolderAsync(topLevel, viewModel).ConfigureAwait(true),
            // Keys usually have no extension, so the default filter has to allow everything.
            FileTypeFilter =
            [
                new FilePickerFileType("SSH private keys") { Patterns = ["id_*", "*.pem", "*.key", "*"] },
                FilePickerFileTypes.All,
            ],
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
            SuggestedStartLocation = await GetKeyFolderAsync(topLevel, viewModel).ConfigureAwait(true),
        });
        if (file?.TryGetLocalPath() is { } path)
        {
            await viewModel.GenerateAsync(path).ConfigureAwait(true);
        }
    }

    private async void Refresh_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SshKeyPickerViewModel viewModel)
        {
            await viewModel.RefreshAvailableKeysAsync().ConfigureAwait(true);
        }
    }

    private void Paste_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SshKeyPickerViewModel viewModel)
        {
            viewModel.OpenImport();
            _ = ImportTextBox.Focus();
        }
    }

    private void CancelImport_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SshKeyPickerViewModel viewModel)
        {
            ClearImportText();
            viewModel.CancelImport();
        }
    }

    private async void Import_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SshKeyPickerViewModel viewModel)
        {
            return;
        }

        // The pasted text is private key material, so it is read straight from the box and wiped
        // afterwards rather than being held on the view model.
        var buffer = ImportTextBox.Text?.ToCharArray() ?? [];
        try
        {
            await viewModel.ImportAsync(new string(buffer)).ConfigureAwait(true);
            if (!viewModel.IsImportOpen)
            {
                ClearImportText();
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(buffer.AsSpan()));
        }
    }

    private async void Path_OnLostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SshKeyPickerViewModel viewModel)
        {
            await viewModel.InspectAsync().ConfigureAwait(true);
        }
    }

    private async void CopyPublicKey_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SshKeyPickerViewModel viewModel)
        {
            await viewModel.CopyPublicKeyAsync().ConfigureAwait(true);
        }
    }

    private void ClearImportText()
    {
        ImportTextBox.Clear();
    }

    private static async Task<IStorageFolder?> GetKeyFolderAsync(
        TopLevel topLevel,
        SshKeyPickerViewModel viewModel)
    {
        try
        {
            return Directory.Exists(viewModel.DefaultKeyDirectory)
                ? await topLevel.StorageProvider.TryGetFolderFromPathAsync(viewModel.DefaultKeyDirectory).ConfigureAwait(true)
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
