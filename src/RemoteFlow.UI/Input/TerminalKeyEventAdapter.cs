using Avalonia.Input;
using RemoteFlow.Application.Services;

namespace RemoteFlow.UI.Input;

public static class TerminalKeyEventAdapter
{
    private static readonly IReadOnlyDictionary<Key, TerminalKey> _specialKeys =
        new Dictionary<Key, TerminalKey>
        {
            [Key.Tab] = TerminalKey.Tab,
            [Key.Insert] = TerminalKey.Insert,
            [Key.Up] = TerminalKey.Up,
            [Key.Down] = TerminalKey.Down,
            [Key.Right] = TerminalKey.Right,
            [Key.Left] = TerminalKey.Left,
            [Key.Home] = TerminalKey.Home,
            [Key.End] = TerminalKey.End,
            [Key.PageUp] = TerminalKey.PageUp,
            [Key.PageDown] = TerminalKey.PageDown,
            [Key.Delete] = TerminalKey.Delete,
            [Key.F1] = TerminalKey.F1,
            [Key.F2] = TerminalKey.F2,
            [Key.F3] = TerminalKey.F3,
            [Key.F4] = TerminalKey.F4,
            [Key.F5] = TerminalKey.F5,
            [Key.F6] = TerminalKey.F6,
            [Key.F7] = TerminalKey.F7,
            [Key.F8] = TerminalKey.F8,
            [Key.F9] = TerminalKey.F9,
            [Key.F10] = TerminalKey.F10,
            [Key.F11] = TerminalKey.F11,
            [Key.F12] = TerminalKey.F12,
        };

    public static TerminalKeyStroke FromAvalonia(KeyEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var key = MapKey(args.Key);
        return new TerminalKeyStroke(key, MapModifiers(args.KeyModifiers), GetText(key, args.KeyModifiers));
    }

    private static TerminalKey MapKey(Key key)
    {
        return key is >= Key.A and <= Key.Z
            ? (TerminalKey)((int)TerminalKey.A + ((int)key - (int)Key.A))
            : key is >= Key.D0 and <= Key.D9
                ? (TerminalKey)((int)TerminalKey.D0 + ((int)key - (int)Key.D0))
                : _specialKeys.GetValueOrDefault(key, TerminalKey.None);
    }

    private static TerminalModifiers MapModifiers(KeyModifiers modifiers)
    {
        var result = TerminalModifiers.None;
        if (modifiers.HasFlag(KeyModifiers.Control))
        {
            result |= TerminalModifiers.Control;
        }

        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            result |= TerminalModifiers.Shift;
        }

        if (modifiers.HasFlag(KeyModifiers.Alt))
        {
            result |= TerminalModifiers.Alt;
        }

        if (modifiers.HasFlag(KeyModifiers.Meta))
        {
            result |= TerminalModifiers.Command;
        }

        return result;
    }

    private static string? GetText(TerminalKey key, KeyModifiers modifiers)
    {
        if (key is >= TerminalKey.A and <= TerminalKey.Z)
        {
            var character = (char)('a' + ((int)key - (int)TerminalKey.A));
            return modifiers.HasFlag(KeyModifiers.Shift)
                ? char.ToUpperInvariant(character).ToString()
                : character.ToString();
        }

        return key is >= TerminalKey.D0 and <= TerminalKey.D9
            ? ((int)key - (int)TerminalKey.D0).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;
    }
}
