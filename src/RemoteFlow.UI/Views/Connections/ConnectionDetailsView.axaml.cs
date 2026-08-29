using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using RemoteFlow.UI.ViewModels.Connections;

namespace RemoteFlow.UI.Views.Connections;

public sealed partial class ConnectionDetailsView : UserControl
{
    public ConnectionDetailsView()
    {
        InitializeComponent();
    }

    /// <summary>Escape closes the pane here for the same reason it closes the editor: whichever of the two
    /// is showing, it is the thing in front of the list, and the key that dismisses it should not depend on
    /// which one it is.</summary>
    protected override async void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && FindOwner() is { } owner)
        {
            _ = await owner.CloseWorkspaceAsync().ConfigureAwait(true);
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    private async void Close_OnClick(object? sender, RoutedEventArgs e)
    {
        if (FindOwner() is { } owner)
        {
            _ = await owner.CloseWorkspaceAsync().ConfigureAwait(true);
        }
    }

    private ConnectionsPageViewModel? FindOwner()
    {
        return this.FindAncestorOfType<ConnectionsView>()?.DataContext as ConnectionsPageViewModel;
    }
}
