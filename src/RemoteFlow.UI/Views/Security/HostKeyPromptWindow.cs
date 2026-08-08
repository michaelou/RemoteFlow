using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using RemoteFlow.Application.Abstractions.Ssh;

namespace RemoteFlow.UI.Views.Security;

public sealed class HostKeyPromptWindow : Window
{
    private readonly HostKeyTrustPrompt _prompt;
    private readonly TextBlock _confirmationNotice;
    private HostKeyPromptDecision _pendingDecision;

    public HostKeyPromptWindow(HostKeyTrustPrompt prompt)
    {
        _prompt = prompt ?? throw new ArgumentNullException(nameof(prompt));
        Decision = HostKeyPromptDecision.Reject;
        Title = prompt.IsMismatch ? "Security warning: host key changed" : "Trust this SSH host?";
        Width = 680;
        MinWidth = 540;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var reject = CreateButton("Reject", "Reject this host key and close the connection");
        reject.IsDefault = prompt.IsMismatch;
        reject.IsCancel = true;
        reject.Click += (_, _) => Reject();
        var acceptOnce = CreateButton("Accept once", "Accept this host key for this connection only");
        acceptOnce.Click += (_, _) => RequestAcceptance(HostKeyPromptDecision.AcceptOnce);
        var acceptSave = CreateButton("Accept and save", "Accept and save this host key as trusted");
        acceptSave.Click += (_, _) => RequestAcceptance(HostKeyPromptDecision.AcceptAndSave);

        _confirmationNotice = new TextBlock
        {
            Foreground = Brushes.OrangeRed,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };

        var content = new StackPanel { Margin = new Avalonia.Thickness(24), Spacing = 16 };
        content.Children.Add(new TextBlock
        {
            Text = prompt.IsMismatch
                ? "The server presented a different identity key. This can indicate a man-in-the-middle attack, a rebuilt server, or a legitimate key rotation. Verify the new fingerprint through a separate trusted channel."
                : "This host has not been seen before. Verify its fingerprint through a separate trusted channel before accepting it.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = prompt.IsMismatch ? Brushes.OrangeRed : null,
            FontWeight = prompt.IsMismatch ? FontWeight.Bold : FontWeight.Normal,
        });
        content.Children.Add(LabelledValue("Host", $"{prompt.Host}:{prompt.Port}"));
        content.Children.Add(LabelledValue("Algorithm", prompt.KeyAlgorithm));
        if (prompt.StoredSha256Fingerprint is not null)
        {
            content.Children.Add(FingerprintPanel("Stored fingerprint", prompt.StoredSha256Fingerprint, Brushes.SteelBlue));
            content.Children.Add(FingerprintPanel("Offered fingerprint", prompt.Sha256Fingerprint, Brushes.OrangeRed));
        }
        else
        {
            content.Children.Add(FingerprintPanel("SHA256 fingerprint", prompt.Sha256Fingerprint, Brushes.SteelBlue));
        }
        if (!string.IsNullOrWhiteSpace(prompt.RandomArt))
        {
            content.Children.Add(LabelledValue("Randomart", prompt.RandomArt, "Cascadia Mono,Consolas,monospace"));
        }
        content.Children.Add(_confirmationNotice);
        content.Children.Add(new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { acceptOnce, acceptSave, reject },
        });
        Content = content;
        AutomationProperties.SetName(this, Title);
        KeyDown += OnKeyDown;
    }

    public HostKeyPromptDecision Decision { get; private set; }

    private static Button CreateButton(string content, string accessibleName)
    {
        var button = new Button { Content = content };
        AutomationProperties.SetName(button, accessibleName);
        return button;
    }

    private static StackPanel LabelledValue(string label, string value, string? fontFamily = null)
    {
        var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap };
        if (fontFamily is not null)
        {
            text.FontFamily = new FontFamily(fontFamily);
        }
        AutomationProperties.SetName(text, $"{label}: {value}");
        return new StackPanel
        {
            Spacing = 3,
            Children =
            {
                new TextBlock { Text = label, FontWeight = FontWeight.SemiBold },
                text,
            },
        };
    }

    private static Border FingerprintPanel(string label, string fingerprint, IBrush border)
    {
        var content = LabelledValue(label, fingerprint, "Cascadia Mono,Consolas,monospace");
        return new Border
        {
            BorderBrush = border,
            BorderThickness = new Avalonia.Thickness(3, 1, 1, 1),
            CornerRadius = new Avalonia.CornerRadius(4),
            Padding = new Avalonia.Thickness(10),
            Child = content,
        };
    }

    private void RequestAcceptance(HostKeyPromptDecision decision)
    {
        if (!_prompt.IsMismatch)
        {
            Decision = decision;
            Close();
            return;
        }

        if (_pendingDecision != decision)
        {
            _pendingDecision = decision;
            _confirmationNotice.Text = $"Security confirmation required: click '{(decision == HostKeyPromptDecision.AcceptOnce ? "Accept once" : "Accept and save")}' again to acknowledge the changed identity key.";
            _confirmationNotice.IsVisible = true;
            return;
        }

        Decision = decision;
        Close();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_prompt.IsMismatch && e.Key is Key.Enter or Key.Escape)
        {
            e.Handled = true;
            Reject();
        }
    }

    private void Reject()
    {
        Decision = HostKeyPromptDecision.Reject;
        Close();
    }
}
