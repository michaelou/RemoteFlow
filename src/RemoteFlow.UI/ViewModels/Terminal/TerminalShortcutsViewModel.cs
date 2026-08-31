using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Services;

namespace RemoteFlow.UI.ViewModels.Terminal;

public sealed record TerminalShortcutViewModel(string Gesture, string Action);

public sealed record TerminalShortcutGroupViewModel(
    string Title,
    IReadOnlyList<TerminalShortcutViewModel> Shortcuts);

/// <summary>
/// The shortcuts worth knowing before typing into a terminal, read out of <see cref="KeymapService"/>
/// so the help can never drift from what the keymap actually does.
/// </summary>
public sealed class TerminalShortcutsViewModel
{
    public TerminalShortcutsViewModel(
        KeymapService keymap,
        CtrlCPolicy ctrlCPolicy = CtrlCPolicy.SigintAlways,
        KeymapPlatform? platform = null)
    {
        ArgumentNullException.ThrowIfNull(keymap);
        var target = platform ?? (OperatingSystem.IsMacOS() ? KeymapPlatform.MacOs : KeymapPlatform.WindowsLinux);
        var bindings = keymap.Bindings
            .Where(binding => binding.Platform is null || binding.Platform == target)
            .ToArray();

        CtrlCNote = ctrlCPolicy == CtrlCPolicy.CopyWhenSelected
            ? "Ctrl+C copies while text is selected and interrupts the running program otherwise."
            : "Ctrl+C always interrupts the running program. Copy with the clipboard shortcut above.";

        Groups =
        [
            new("Clipboard and selection",
            [
                .. Rows(bindings, KeymapCommand.Copy, "Copy the selection"),
                .. Rows(bindings, KeymapCommand.Paste, "Paste at the cursor"),
                .. Rows(bindings, KeymapCommand.SelectAll, "Select everything in the scrollback"),
                new("Ctrl+C", "Interrupt the running program (SIGINT)"),
                new("Drag / double-click", "Select text; there is no cut in a terminal — copy, then let the program delete"),
            ]),
            new("Find",
            [
                .. Rows(bindings, KeymapCommand.FindTerminal, "Search the scrollback"),
                new("Enter", "Jump to the next match"),
                new("Shift+Enter", "Jump to the previous match"),
                new("Esc", "Close the find bar"),
            ]),
            new("Command library",
            [
                .. Rows(bindings, KeymapCommand.CommandLibrary, "Open the library of commands"),
                new("Enter", "Type the highlighted command at the prompt — it is not run for you"),
                new("↑ / ↓", "Move through the matches"),
                new("Esc", "Close the library"),
            ]),
            new("Terminals",
            [
                .. Rows(bindings, KeymapCommand.NewTerminal),
                .. Rows(bindings, KeymapCommand.CloseTerminal),
                .. Rows(bindings, KeymapCommand.CycleTerminal),
                .. Rows(bindings, KeymapCommand.CycleTerminalBackward),
                .. SwitchRow(bindings),
                .. Rows(bindings, KeymapCommand.ToggleFullscreen),
            ]),
        ];
    }

    public IReadOnlyList<TerminalShortcutGroupViewModel> Groups { get; }

    public string CtrlCNote { get; }

    public string Footnote { get; } =
        "Every other control-key combination goes straight to the remote shell.";

    /// <summary>Collapses the nine per-terminal shortcuts into the single range they read as.</summary>
    private static TerminalShortcutViewModel[] SwitchRow(IReadOnlyList<KeymapBinding> bindings)
    {
        var switches = bindings
            .Where(binding => binding.Command is >= KeymapCommand.SwitchToTerminal1 and <= KeymapCommand.SwitchToTerminal9)
            .ToArray();
        return switches.Length == 0
            ? []
            : [new($"{switches[0].Gesture} … {switches[^1].Gesture}", "Jump straight to a terminal by position")];
    }

    /// <summary>One row per command, with every gesture that triggers it on this platform.</summary>
    private static TerminalShortcutViewModel[] Rows(
        IReadOnlyList<KeymapBinding> bindings,
        KeymapCommand command,
        string? action = null)
    {
        var matches = bindings.Where(binding => binding.Command == command).ToArray();
        return matches.Length == 0
            ? []
            : [new(string.Join("  or  ", matches.Select(binding => binding.Gesture)), action ?? matches[0].Action)];
    }
}
