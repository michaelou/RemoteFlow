using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RemoteFlow.Application.Abstractions;

namespace RemoteFlow.UI.Views.Security;

public sealed class SshCredentialPromptWindow : Window
{
    private readonly TextBox _secret;
    private readonly CheckBox _save;

    public SshCredentialPromptWindow(SshCredentialPromptRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Title = request.Title;
        Width = 520;
        MinWidth = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _secret = new TextBox { PasswordChar = '●' };
        _save = new CheckBox
        {
            Content = "Store securely for future connections",
            IsVisible = request.AllowSave,
        };
        AutomationProperties.SetName(_secret, request.Kind == Domain.Enums.CredentialKind.PrivateKeyPassphrase
            ? "Private key passphrase"
            : "SSH password");
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var submit = new Button { Content = "Continue", IsDefault = true };
        cancel.Click += (_, _) => Close();
        submit.Click += (_, _) =>
        {
            if (!string.IsNullOrEmpty(_secret.Text))
            {
                Result = new(new SecretHandle(_secret.Text.AsSpan()), _save.IsChecked == true);
                _secret.Text = string.Empty;
                Close();
            }
        };
        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(24),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = request.Message, TextWrapping = TextWrapping.Wrap },
                _secret,
                _save,
                new StackPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { cancel, submit },
                },
            },
        };
        Opened += (_, _) => _ = _secret.Focus();
    }

    public SshCredentialPromptResult? Result { get; private set; }
}
