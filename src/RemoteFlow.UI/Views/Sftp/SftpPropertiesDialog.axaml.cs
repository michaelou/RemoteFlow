using Avalonia.Controls;
using Avalonia.Interactivity;
using RemoteFlow.UI.ViewModels.Sftp;

namespace RemoteFlow.UI.Views.Sftp;

public sealed partial class SftpPropertiesDialog : Window
{
    public SftpPropertiesDialog()
    {
        InitializeComponent();
    }

    public SftpPropertiesDialog(SftpPropertiesViewModel properties) : this()
    {
        DataContext = properties ?? throw new ArgumentNullException(nameof(properties));
    }

    private void Close_OnClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
