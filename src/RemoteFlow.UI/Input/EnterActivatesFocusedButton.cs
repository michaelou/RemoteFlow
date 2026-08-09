using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace RemoteFlow.UI.Input;

/// <summary>
/// Makes Enter press the button the keyboard is on.
/// </summary>
/// <remarks>
/// Avalonia activates a focused button with Space, and with Enter only when the button is the window's
/// default. Everyone expects Enter to work: it is what every other desktop toolkit does, and what a
/// person who has just tabbed to "New connection" will press. Registering one class handler for
/// <see cref="Button" /> fixes it everywhere at once, rather than per view.
/// </remarks>
public static class EnterActivatesFocusedButton
{
    private static bool _installed;

    public static void Install()
    {
        if (_installed)
        {
            return;
        }

        _installed = true;
        _ = InputElement.KeyDownEvent.AddClassHandler<Button>(OnKeyDown, RoutingStrategies.Bubble);
    }

    private static void OnKeyDown(Button button, KeyEventArgs e)
    {
        // IsFocused, not just "the event reached a button": a text box inside a button-shaped container
        // bubbles here too, and Enter belongs to whatever holds the caret. Handled events are left
        // alone so a default button, or a view that binds Enter itself, still wins.
        if (e.Handled || e.Key != Key.Enter || !button.IsFocused || !button.IsEffectivelyEnabled)
        {
            return;
        }

        // Both halves matter: Click handlers are wired in code-behind across the app, and Command is
        // bound in XAML. A button using one does nothing in response to the other.
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
        if (button.Command is { } command && command.CanExecute(button.CommandParameter))
        {
            command.Execute(button.CommandParameter);
        }

        e.Handled = true;
    }
}
