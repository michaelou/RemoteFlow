using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RemoteFlow.Application.Abstractions.Ssh;

namespace RemoteFlow.UI.Views.Security;

public sealed class KeyboardInteractivePromptWindow : Window
{
    private readonly List<TextBox> _inputs;

    public KeyboardInteractivePromptWindow(IReadOnlyList<SshAuthenticationPrompt> prompts)
    {
        ArgumentNullException.ThrowIfNull(prompts);
        Title = "SSH authentication challenge";
        Width = 560;
        MinWidth = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var inputs = new List<TextBox>(prompts.Count);
        var content = new StackPanel { Margin = new Avalonia.Thickness(24), Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = "The SSH server requested the following information:",
            TextWrapping = TextWrapping.Wrap,
        });
        foreach (var prompt in prompts)
        {
            var input = new TextBox { PasswordChar = prompt.IsSecret ? '●' : default };
            AutomationProperties.SetName(input, prompt.Prompt);
            inputs.Add(input);
            content.Children.Add(new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = prompt.Prompt, TextWrapping = TextWrapping.Wrap },
                    input,
                },
            });
        }

        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var submit = new Button { Content = "Continue", IsDefault = true };
        cancel.Click += (_, _) => Close();
        submit.Click += (_, _) =>
        {
            Responses = [.. inputs.Select(input => input.Text ?? string.Empty)];
            Close();
        };
        AutomationProperties.SetName(cancel, "Cancel SSH authentication");
        AutomationProperties.SetName(submit, "Submit SSH authentication responses");
        content.Children.Add(new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { cancel, submit },
        });
        Content = content;
        _inputs = inputs;
        Opened += (_, _) =>
        {
            if (_inputs.Count > 0)
            {
                _ = _inputs[0].Focus();
            }
        };
    }

    public IReadOnlyList<string> Responses { get; private set; } = [];
}
