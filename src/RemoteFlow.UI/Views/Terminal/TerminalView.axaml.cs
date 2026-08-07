using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using RemoteFlow.UI.ViewModels;

namespace RemoteFlow.UI.Views.Terminal;

public sealed partial class TerminalView : UserControl
{
    public TerminalView()
    {
        InitializeComponent();
    }

    private async void TerminalView_OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TerminalsPageViewModel viewModel)
        {
            await viewModel.InitializeAsync().ConfigureAwait(true);
            _ = TerminalHost.Focus();
        }
    }

    private async void Restart_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TerminalsPageViewModel viewModel)
        {
            await viewModel.StartLocalShellAsync().ConfigureAwait(true);
            _ = TerminalHost.Focus();
        }
    }

    private void TerminalBorder_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _ = TerminalHost.Focus();
    }
}
