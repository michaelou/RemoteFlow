using Avalonia.Controls;
using Avalonia.Interactivity;
using RemoteFlow.UI.ViewModels.Terminal;

namespace RemoteFlow.UI.Views.Terminal;

public sealed partial class TerminalShortcutsDialog : Window
{
    public TerminalShortcutsDialog()
    {
        InitializeComponent();
    }

    public TerminalShortcutsDialog(TerminalShortcutsViewModel shortcuts) : this()
    {
        DataContext = shortcuts ?? throw new ArgumentNullException(nameof(shortcuts));
    }

    private void Close_OnClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
