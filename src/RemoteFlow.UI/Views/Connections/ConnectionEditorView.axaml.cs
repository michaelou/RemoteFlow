using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using RemoteFlow.UI.ViewModels.Connections;

namespace RemoteFlow.UI.Views.Connections;

public sealed partial class ConnectionEditorView : UserControl
{
    public ConnectionEditorView()
    {
        InitializeComponent();
    }

    private async void Save_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ConnectionEditorViewModel editor || FindOwner() is not { } owner)
        {
            return;
        }

        var capture = editor.AuthMethod == Domain.Enums.AuthMethod.PrivateKey
            ? PassphraseCapture
            : PasswordCapture;
        var buffer = capture.Text?.ToCharArray() ?? [];
        capture.Text = string.Empty;
        try
        {
            _ = await owner.SaveEditorAsync(buffer.AsMemory()).ConfigureAwait(true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(buffer.AsSpan()));
        }
    }

    private async void Cancel_OnClick(object? sender, RoutedEventArgs e)
    {
        if (FindOwner() is { } owner)
        {
            _ = await owner.CloseEditorAsync().ConfigureAwait(true);
        }
    }

    private async void ClearCredential_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ConnectionEditorViewModel editor)
        {
            _ = await editor.ClearCredentialAsync().ConfigureAwait(true);
        }
    }

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && FindOwner() is { } owner)
        {
            _ = await owner.CloseEditorAsync().ConfigureAwait(true);
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    private ConnectionsPageViewModel? FindOwner()
    {
        return this.FindAncestorOfType<ConnectionsView>()?.DataContext as ConnectionsPageViewModel;
    }
}
