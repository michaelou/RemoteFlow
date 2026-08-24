using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RemoteFlow.Application.Abstractions;

namespace RemoteFlow.UI.Views.Security;

/// <summary>Asks for the credential vault's passphrase. Two shapes in one window, because they are the same
/// question at different moments: creating a vault wants a passphrase invented and confirmed, opening one
/// wants it recalled. Built in code rather than XAML, matching the other prompt windows.</summary>
public sealed class VaultUnlockWindow : Window
{
    private readonly bool _isNewVault;
    private readonly TextBox _passphrase;
    private readonly TextBox _confirmation;
    private readonly TextBlock _problem;

    public VaultUnlockWindow(VaultUnlockPromptRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _isNewVault = request.IsNewVault;
        Title = _isNewVault ? "Set up the credential vault" : "Unlock the credential vault";
        Width = 520;
        MinWidth = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _passphrase = new TextBox { PasswordChar = '●' };
        AutomationProperties.SetName(_passphrase, "Vault passphrase");
        _confirmation = new TextBox { PasswordChar = '●', IsVisible = _isNewVault };
        AutomationProperties.SetName(_confirmation, "Confirm vault passphrase");
        _problem = new TextBlock
        {
            Foreground = Brushes.OrangeRed,
            TextWrapping = TextWrapping.Wrap,
            Text = request.Problem ?? string.Empty,
            IsVisible = request.Problem is not null,
        };

        var cancel = new Button { Content = _isNewVault ? "Not now" : "Cancel", IsCancel = true };
        var submit = new Button { Content = _isNewVault ? "Create vault" : "Unlock", IsDefault = true };
        cancel.Click += (_, _) => Close();
        submit.Click += (_, _) => Submit();

        var body = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = Explanation(), TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = _isNewVault ? "Passphrase" : "Vault passphrase" },
                _passphrase,
            },
        };
        if (_isNewVault)
        {
            body.Children.Add(new TextBlock { Text = "Confirm passphrase" });
            body.Children.Add(_confirmation);
            body.Children.Add(new TextBlock
            {
                Text = PassphrasePolicy.Requirement +
                    " There is no way to recover the vault if this is forgotten — nothing else holds a copy.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.75,
            });
        }

        body.Children.Add(_problem);
        body.Children.Add(new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { cancel, submit },
        });
        Content = body;
        Opened += (_, _) => _ = _passphrase.Focus();
    }

    public VaultUnlockPromptResult? Result { get; private set; }

    private string Explanation()
    {
        return _isNewVault
            ? "This computer has no system keyring RemoteFlow can use, so it keeps saved passwords and keys " +
                "in an encrypted file of its own. Choose a passphrase to protect it. You will be asked for " +
                "this each time RemoteFlow starts."
            : "RemoteFlow keeps your saved passwords and keys in an encrypted file. Enter its passphrase to " +
                "make them available for this session.";
    }

    private void Submit()
    {
        var entered = _passphrase.Text ?? string.Empty;
        if (entered.Length == 0)
        {
            Fail("Enter the vault passphrase.");
            return;
        }

        if (_isNewVault)
        {
            // Checked here rather than after the fact: a mistyped confirmation on a vault being created
            // would otherwise become a passphrase nobody knows, protecting a vault nobody can open.
            if (!string.Equals(entered, _confirmation.Text ?? string.Empty, StringComparison.Ordinal))
            {
                Fail("The two passphrases do not match.");
                return;
            }

            if (!PassphrasePolicy.IsStrong(entered))
            {
                Fail(PassphrasePolicy.Requirement);
                return;
            }
        }

        Result = new VaultUnlockPromptResult(new SecretHandle(entered.AsSpan()));
        _passphrase.Text = string.Empty;
        _confirmation.Text = string.Empty;
        Close();
    }

    private void Fail(string message)
    {
        _problem.Text = message;
        _problem.IsVisible = true;
        _ = _passphrase.Focus();
    }
}
