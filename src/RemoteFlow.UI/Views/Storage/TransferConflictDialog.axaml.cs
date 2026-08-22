using Avalonia.Controls;
using Avalonia.Interactivity;
using RemoteFlow.Application.Abstractions.Sftp;
using RemoteFlow.UI.Services;
using RemoteFlow.UI.ViewModels.Storage;

namespace RemoteFlow.UI.Views.Storage;

public sealed partial class TransferConflictDialog : Window
{
    private readonly TransferConflictDialogViewModel? _viewModel;

    public TransferConflictDialog()
    {
        InitializeComponent();
    }

    public TransferConflictDialog(TransferConflictDialogViewModel viewModel) : this()
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = viewModel;
    }

    /// <summary>Cancel by default, including a dialog closed with the window chrome: an unanswered
    /// question must never be read as consent to overwrite.</summary>
    public TransferConflictChoice Choice { get; private set; } =
        new(TransferConflictDecision.Cancel, ApplyToAll: false);

    private void Cancel_OnClick(object? sender, RoutedEventArgs e)
    {
        CloseWith(TransferConflictDecision.Cancel);
    }

    private void Skip_OnClick(object? sender, RoutedEventArgs e)
    {
        CloseWith(TransferConflictDecision.Skip);
    }

    private void Overwrite_OnClick(object? sender, RoutedEventArgs e)
    {
        CloseWith(TransferConflictDecision.Overwrite);
    }

    private void CloseWith(TransferConflictDecision decision)
    {
        Choice = new TransferConflictChoice(decision, _viewModel?.ApplyToAll ?? false);
        Close();
    }
}
