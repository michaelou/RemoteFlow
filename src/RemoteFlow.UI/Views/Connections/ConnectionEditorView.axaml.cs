using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RemoteFlow.UI.ViewModels.Connections;

namespace RemoteFlow.UI.Views.Connections;

public sealed partial class ConnectionEditorView : UserControl
{
    public ConnectionEditorView()
    {
        InitializeComponent();
        // The editor opens beside the button that opened it, so without this the keyboard is still on
        // that button and the first field is a dozen tab stops away. Opening a form means being in it.
        // Posted rather than called inline: at Loaded the window may not be active yet, and a focus
        // request to an inactive window is dropped.
        Loaded += (_, _) => Dispatcher.UIThread.Post(() => NameBox.Focus(NavigationMethod.Tab));
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
            ReadOnlyMemory<char> secretToStore = buffer.AsMemory();
            if (editor.AuthMethod == Domain.Enums.AuthMethod.PrivateKey && editor.KeyPicker is { IsEncrypted: true } keyPicker)
            {
                if (keyPicker.Sha256Fingerprint is null)
                {
                    await keyPicker.InspectAsync(buffer.AsMemory()).ConfigureAwait(true);
                }
                if (keyPicker.Sha256Fingerprint is null)
                {
                    return;
                }
                if (!keyPicker.StorePassphrase)
                {
                    secretToStore = ReadOnlyMemory<char>.Empty;
                }
            }
            _ = await owner.SaveEditorAsync(secretToStore).ConfigureAwait(true);
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

    /// <summary>Distinct from Cancel: cancelling abandons the edit and drops back to the details for the
    /// same connection, while this shuts the pane outright and gives the page back to the list. Unsaved
    /// work is still only discarded on the same prompt.</summary>
    private async void Close_OnClick(object? sender, RoutedEventArgs e)
    {
        if (FindOwner() is { } owner)
        {
            _ = await owner.CloseWorkspaceAsync().ConfigureAwait(true);
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
