using Avalonia.Controls;

namespace RemoteFlow.UI.Views.Storage;

/// <summary>The application-wide transfer queue, embedded at the foot of the Storage page. It binds the
/// same <c>TransfersPageViewModel</c> singleton the Transfers page does, so there is exactly one queue and
/// exactly one three-slot concurrency gate.</summary>
public sealed partial class TransferQueuePane : UserControl
{
    public TransferQueuePane()
    {
        InitializeComponent();
    }
}
