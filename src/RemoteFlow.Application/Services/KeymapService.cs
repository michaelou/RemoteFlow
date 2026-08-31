using System.Collections.ObjectModel;
using System.Text;
using RemoteFlow.Application.Abstractions;

namespace RemoteFlow.Application.Services;

public enum KeymapPlatform
{
    WindowsLinux = 1,
    MacOs = 2,
}

[Flags]
public enum TerminalModifiers
{
    None = 0,
    Control = 1,
    Shift = 2,
    Alt = 4,
    Command = 8,
}

public enum TerminalKey
{
    None = 0,
    A,
    B,
    C,
    D,
    E,
    F,
    G,
    H,
    I,
    J,
    K,
    L,
    M,
    N,
    O,
    P,
    Q,
    R,
    S,
    T,
    U,
    V,
    W,
    X,
    Y,
    Z,
    D0,
    D1,
    D2,
    D3,
    D4,
    D5,
    D6,
    D7,
    D8,
    D9,
    Tab,
    Insert,
    Up,
    Down,
    Right,
    Left,
    Home,
    End,
    PageUp,
    PageDown,
    Delete,
    F1,
    F2,
    F3,
    F4,
    F5,
    F6,
    F7,
    F8,
    F9,
    F10,
    F11,
    F12,
}

public enum KeymapCommand
{
    Copy = 1,
    Paste,
    SelectAll,
    FindTerminal,
    NewTerminal,
    CloseTerminal,
    CycleTerminal,
    CycleTerminalBackward,
    SwitchToTerminal1,
    SwitchToTerminal2,
    SwitchToTerminal3,
    SwitchToTerminal4,
    SwitchToTerminal5,
    SwitchToTerminal6,
    SwitchToTerminal7,
    SwitchToTerminal8,
    SwitchToTerminal9,
    ToggleFullscreen,
    LeaveTerminal,
    CommandLibrary,
}

public enum KeymapResultKind
{
    Unhandled = 0,
    PtyBytes = 1,
    ApplicationCommand = 2,
}

public readonly record struct TerminalKeyStroke(
    TerminalKey Key,
    TerminalModifiers Modifiers = TerminalModifiers.None,
    string? Text = null);

public sealed record KeymapResult
{
    private KeymapResult(KeymapResultKind kind, ReadOnlyMemory<byte> bytes, KeymapCommand? command)
    {
        Kind = kind;
        Bytes = bytes;
        Command = command;
    }

    public KeymapResultKind Kind { get; }

    public ReadOnlyMemory<byte> Bytes { get; }

    public KeymapCommand? Command { get; }

    public static KeymapResult Unhandled { get; } = new(KeymapResultKind.Unhandled, ReadOnlyMemory<byte>.Empty, null);

    public static KeymapResult Pty(ReadOnlyMemory<byte> bytes)
    {
        return new KeymapResult(KeymapResultKind.PtyBytes, bytes, null);
    }

    public static KeymapResult App(KeymapCommand command)
    {
        return new KeymapResult(KeymapResultKind.ApplicationCommand, ReadOnlyMemory<byte>.Empty, command);
    }
}

public sealed record KeymapBinding(
    KeymapPlatform? Platform,
    TerminalKeyStroke Stroke,
    KeymapCommand? Command,
    byte[]? NormalBytes,
    byte[]? ApplicationCursorBytes,
    string Gesture,
    string Action);

public sealed class KeymapService
{
    private static readonly byte[] _escape = [0x1B];

    public IReadOnlyList<KeymapBinding> Bindings { get; } = CreateBindings();

    public KeymapResult Resolve(
        TerminalKeyStroke stroke,
        KeymapPlatform platform,
        bool applicationCursorKeys = false,
        CtrlCPolicy ctrlCPolicy = CtrlCPolicy.SigintAlways,
        bool hasSelection = false)
    {
        if (stroke.Key == TerminalKey.C && stroke.Modifiers == TerminalModifiers.Control &&
            ctrlCPolicy == CtrlCPolicy.CopyWhenSelected && hasSelection)
        {
            return KeymapResult.App(KeymapCommand.Copy);
        }

        var binding = Bindings.FirstOrDefault(candidate =>
            (candidate.Platform is null || candidate.Platform == platform) &&
            candidate.Stroke.Key == stroke.Key &&
            candidate.Stroke.Modifiers == stroke.Modifiers);
        if (binding is not null)
        {
            if (binding.Command is { } command)
            {
                return KeymapResult.App(command);
            }

            var bytes = applicationCursorKeys && binding.ApplicationCursorBytes is not null
                ? binding.ApplicationCursorBytes
                : binding.NormalBytes;
            return bytes is null ? KeymapResult.Unhandled : KeymapResult.Pty(bytes);
        }

        var isControlLetter = stroke.Modifiers is TerminalModifiers.Control or
            (TerminalModifiers.Control | TerminalModifiers.Shift) &&
            stroke.Key is >= TerminalKey.A and <= TerminalKey.Z;
        return isControlLetter
            ? KeymapResult.Pty(new byte[] { (byte)((int)stroke.Key - (int)TerminalKey.A + 1) })
            : stroke.Modifiers == TerminalModifiers.Alt && !string.IsNullOrEmpty(stroke.Text)
                ? KeymapResult.Pty(Concat(_escape, Encoding.UTF8.GetBytes(stroke.Text)))
                : stroke.Modifiers == TerminalModifiers.None && !string.IsNullOrEmpty(stroke.Text)
                    ? KeymapResult.Pty(Encoding.UTF8.GetBytes(stroke.Text))
                    : KeymapResult.Unhandled;
    }

    public string GenerateMarkdown()
    {
        var builder = new StringBuilder();
        _ = builder.AppendLine("# Terminal keybindings");
        _ = builder.AppendLine();
        _ = builder.AppendLine("This file is generated from `KeymapService.Bindings`. Changes must be made in the keymap data first.");
        _ = builder.AppendLine();
        _ = builder.AppendLine("| Platform | Binding | Action |");
        _ = builder.AppendLine("| --- | --- | --- |");
        foreach (var binding in Bindings)
        {
            var platform = binding.Platform switch
            {
                KeymapPlatform.WindowsLinux => "Windows/Linux",
                KeymapPlatform.MacOs => "macOS",
                null => "All",
                _ => throw new ArgumentOutOfRangeException(),
            };
            _ = builder.Append("| ").Append(platform)
                .Append(" | `").Append(binding.Gesture)
                .Append("` | ").Append(binding.Action).AppendLine(" |");
        }

        _ = builder.AppendLine();
        _ = builder.AppendLine("All other control-key combinations are sent to the PTY. `Alt` plus text is encoded as an ESC prefix. Ctrl+C sends byte `03` unless the optional, default-off CopyWhenSelected policy is enabled and a selection exists.");
        return builder.ToString().ReplaceLineEndings("\n");
    }

    private static ReadOnlyCollection<KeymapBinding> CreateBindings()
    {
        var bindings = new List<KeymapBinding>
        {
            Pty("Ctrl+C", TerminalKey.C, TerminalModifiers.Control, [0x03], "Send SIGINT (byte 03)"),
            App("Ctrl+Shift+T", TerminalKey.T, TerminalModifiers.Control | TerminalModifiers.Shift, KeymapCommand.NewTerminal, "Open a new terminal"),
            App("Ctrl+Shift+W", TerminalKey.W, TerminalModifiers.Control | TerminalModifiers.Shift, KeymapCommand.CloseTerminal, "Close the active terminal"),
            App("Ctrl+Shift+A", TerminalKey.A, TerminalModifiers.Control | TerminalModifiers.Shift, KeymapCommand.SelectAll, "Select all terminal text"),
            App("Ctrl+Shift+F", TerminalKey.F, TerminalModifiers.Control | TerminalModifiers.Shift, KeymapCommand.FindTerminal, "Find in terminal scrollback"),
            App("Ctrl+Shift+K", TerminalKey.K, TerminalModifiers.Control | TerminalModifiers.Shift, KeymapCommand.CommandLibrary, "Open the command library"),
            App("Ctrl+Tab", TerminalKey.Tab, TerminalModifiers.Control, KeymapCommand.CycleTerminal, "Select the next terminal"),
            App("Ctrl+Shift+Tab", TerminalKey.Tab, TerminalModifiers.Control | TerminalModifiers.Shift, KeymapCommand.CycleTerminalBackward, "Select the previous terminal"),
            App("F11", TerminalKey.F11, TerminalModifiers.None, KeymapCommand.ToggleFullscreen, "Toggle full screen"),
            // Without this the terminal is a keyboard trap: it consumes Tab as a byte, so once focus is
            // inside there is no way back out to the rest of the application. F6 is the platform
            // convention for moving between panes, and Shift+F6 still sends the terminal its own F6 —
            // the same arrangement F11 already uses.
            App("F6", TerminalKey.F6, TerminalModifiers.None, KeymapCommand.LeaveTerminal, "Move focus out of the terminal"),
            App("Ctrl+Shift+C", TerminalKey.C, TerminalModifiers.Control | TerminalModifiers.Shift, KeymapCommand.Copy, "Copy selection", KeymapPlatform.WindowsLinux),
            App("Ctrl+Insert", TerminalKey.Insert, TerminalModifiers.Control, KeymapCommand.Copy, "Copy selection", KeymapPlatform.WindowsLinux),
            App("Ctrl+Shift+V", TerminalKey.V, TerminalModifiers.Control | TerminalModifiers.Shift, KeymapCommand.Paste, "Paste", KeymapPlatform.WindowsLinux),
            App("Shift+Insert", TerminalKey.Insert, TerminalModifiers.Shift, KeymapCommand.Paste, "Paste", KeymapPlatform.WindowsLinux),
            App("Cmd+C", TerminalKey.C, TerminalModifiers.Command, KeymapCommand.Copy, "Copy selection", KeymapPlatform.MacOs),
            App("Cmd+V", TerminalKey.V, TerminalModifiers.Command, KeymapCommand.Paste, "Paste", KeymapPlatform.MacOs),
            Pty("Up", TerminalKey.Up, TerminalModifiers.None, Esc("[A"), "Cursor up", Esc("OA")),
            Pty("Down", TerminalKey.Down, TerminalModifiers.None, Esc("[B"), "Cursor down", Esc("OB")),
            Pty("Right", TerminalKey.Right, TerminalModifiers.None, Esc("[C"), "Cursor right", Esc("OC")),
            Pty("Left", TerminalKey.Left, TerminalModifiers.None, Esc("[D"), "Cursor left", Esc("OD")),
            Pty("Home", TerminalKey.Home, TerminalModifiers.None, Esc("[H"), "Home"),
            Pty("End", TerminalKey.End, TerminalModifiers.None, Esc("[F"), "End"),
            Pty("PageUp", TerminalKey.PageUp, TerminalModifiers.None, Esc("[5~"), "Page up"),
            Pty("PageDown", TerminalKey.PageDown, TerminalModifiers.None, Esc("[6~"), "Page down"),
            Pty("Delete", TerminalKey.Delete, TerminalModifiers.None, Esc("[3~"), "Delete"),
            Pty("F1", TerminalKey.F1, TerminalModifiers.None, Esc("OP"), "F1"),
            Pty("F2", TerminalKey.F2, TerminalModifiers.None, Esc("OQ"), "F2"),
            Pty("F3", TerminalKey.F3, TerminalModifiers.None, Esc("OR"), "F3"),
            Pty("F4", TerminalKey.F4, TerminalModifiers.None, Esc("OS"), "F4"),
            Pty("F5", TerminalKey.F5, TerminalModifiers.None, Esc("[15~"), "F5"),
            Pty("F6 (terminal)", TerminalKey.F6, TerminalModifiers.Shift, Esc("[17~"), "Send terminal F6 (Shift avoids the app shortcut)"),
            Pty("F7", TerminalKey.F7, TerminalModifiers.None, Esc("[18~"), "F7"),
            Pty("F8", TerminalKey.F8, TerminalModifiers.None, Esc("[19~"), "F8"),
            Pty("F9", TerminalKey.F9, TerminalModifiers.None, Esc("[20~"), "F9"),
            Pty("F10", TerminalKey.F10, TerminalModifiers.None, Esc("[21~"), "F10"),
            Pty("F11 (terminal)", TerminalKey.F11, TerminalModifiers.Shift, Esc("[23~"), "Send terminal F11 (Shift avoids the app shortcut)"),
            Pty("F12", TerminalKey.F12, TerminalModifiers.None, Esc("[24~"), "F12"),
        };
        // Anchored to the binding they read as a continuation of rather than to a count of the rows above
        // them, which a new binding anywhere earlier would silently shift.
        var afterCycling = bindings.FindIndex(binding => binding.Command == KeymapCommand.CycleTerminalBackward) + 1;
        for (var index = 1; index <= 9; index++)
        {
            bindings.Insert(afterCycling + index - 1, App(
                $"Alt+{index}",
                (TerminalKey)((int)TerminalKey.D0 + index),
                TerminalModifiers.Alt,
                (KeymapCommand)((int)KeymapCommand.SwitchToTerminal1 + index - 1),
                $"Select terminal {index}"));
        }

        return bindings.AsReadOnly();
    }

    private static KeymapBinding App(
        string gesture,
        TerminalKey key,
        TerminalModifiers modifiers,
        KeymapCommand command,
        string action,
        KeymapPlatform? platform = null)
    {
        return new KeymapBinding(platform, new TerminalKeyStroke(key, modifiers), command, null, null, gesture, action);
    }

    private static KeymapBinding Pty(
        string gesture,
        TerminalKey key,
        TerminalModifiers modifiers,
        byte[] bytes,
        string action,
        byte[]? applicationBytes = null)
    {
        return new KeymapBinding(null, new TerminalKeyStroke(key, modifiers), null, bytes, applicationBytes, gesture, action);
    }

    private static byte[] Esc(string suffix)
    {
        return Concat(_escape, Encoding.ASCII.GetBytes(suffix));
    }

    private static byte[] Concat(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        var result = new byte[first.Length + second.Length];
        first.CopyTo(result);
        second.CopyTo(result.AsSpan(first.Length));
        return result;
    }
}
