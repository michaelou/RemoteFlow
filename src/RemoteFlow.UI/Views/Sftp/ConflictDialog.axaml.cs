using Avalonia.Controls;
using Avalonia.Interactivity;
using RemoteFlow.Application.Abstractions.Sftp;
using RemoteFlow.UI.ViewModels.Sftp;

namespace RemoteFlow.UI.Views.Sftp;

public sealed partial class ConflictDialog : Window
{
    public ConflictDialog()
    {
        InitializeComponent();
    }

    public ConflictDialog(RemoteEditConflictDialogViewModel viewModel) : this()
    {
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }

    public RemoteEditConflictResolution Resolution { get; private set; } =
        RemoteEditConflictResolution.Cancel;

    private void Cancel_OnClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void DiscardLocal_OnClick(object? sender, RoutedEventArgs e)
    {
        CloseWith(RemoteEditConflictResolution.DiscardLocal);
    }

    private void KeepBoth_OnClick(object? sender, RoutedEventArgs e)
    {
        CloseWith(RemoteEditConflictResolution.KeepBoth);
    }

    private void OverwriteRemote_OnClick(object? sender, RoutedEventArgs e)
    {
        CloseWith(RemoteEditConflictResolution.OverwriteRemote);
    }

    private void CloseWith(RemoteEditConflictResolution resolution)
    {
        Resolution = resolution;
        Close();
    }
}
