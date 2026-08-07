using Avalonia.Controls;
using Avalonia.Interactivity;
using RemoteFlow.UI.ViewModels.Settings;

namespace RemoteFlow.UI.Views.Settings;

public sealed partial class TerminalSettings : UserControl
{
    public TerminalSettings()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TerminalSettingsViewModel viewModel)
        {
            await viewModel.InitializeAsync().ConfigureAwait(true);
        }
    }

    private void RemoveShellProfile_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ShellProfileEditorViewModel profile } &&
            DataContext is TerminalSettingsViewModel viewModel)
        {
            viewModel.RemoveShellProfileCommand.Execute(profile);
        }
    }
}
