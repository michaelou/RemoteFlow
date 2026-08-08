using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Services;
using RemoteFlow.UI.ViewModels.Terminal;
using Xunit;

namespace RemoteFlow.UI.Tests;

public sealed class TerminalShortcutsViewModelTests
{
    [Fact]
    public void ShortcutsComeFromTheKeymapAndAreScopedToThePlatform()
    {
        var windows = new TerminalShortcutsViewModel(
            new KeymapService(),
            platform: KeymapPlatform.WindowsLinux);
        var mac = new TerminalShortcutsViewModel(
            new KeymapService(),
            platform: KeymapPlatform.MacOs);

        var clipboard = windows.Groups.Single(group => group.Title == "Clipboard and selection");
        var copy = clipboard.Shortcuts.Single(shortcut => shortcut.Action == "Copy the selection");
        Assert.Contains("Ctrl+Shift+C", copy.Gesture, StringComparison.Ordinal);
        Assert.Contains("Ctrl+Insert", copy.Gesture, StringComparison.Ordinal);
        Assert.DoesNotContain("Cmd+", copy.Gesture, StringComparison.Ordinal);

        var macCopy = mac.Groups
            .Single(group => group.Title == "Clipboard and selection")
            .Shortcuts.Single(shortcut => shortcut.Action == "Copy the selection");
        Assert.Equal("Cmd+C", macCopy.Gesture);
    }

    [Fact]
    public void CtrlCIsExplainedAndTheNineTerminalSwitchesCollapseToOneRow()
    {
        var keymap = new KeymapService();

        var sigint = new TerminalShortcutsViewModel(keymap, CtrlCPolicy.SigintAlways, KeymapPlatform.WindowsLinux);
        var copyWhenSelected = new TerminalShortcutsViewModel(keymap, CtrlCPolicy.CopyWhenSelected, KeymapPlatform.WindowsLinux);

        Assert.Contains("interrupts", sigint.CtrlCNote, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("copies while text is selected", copyWhenSelected.CtrlCNote, StringComparison.OrdinalIgnoreCase);

        var terminals = sigint.Groups.Single(group => group.Title == "Terminals");
        var switching = terminals.Shortcuts.Single(shortcut => shortcut.Gesture.Contains('…', StringComparison.Ordinal));
        Assert.Equal("Alt+1 … Alt+9", switching.Gesture);
        Assert.DoesNotContain(terminals.Shortcuts, shortcut => shortcut.Gesture == "Alt+5");
    }

    [Fact]
    public void EveryListedGestureHasAnActionAndNoGroupIsEmpty()
    {
        var shortcuts = new TerminalShortcutsViewModel(new KeymapService(), platform: KeymapPlatform.WindowsLinux);

        Assert.NotEmpty(shortcuts.Groups);
        Assert.All(shortcuts.Groups, group =>
        {
            Assert.NotEmpty(group.Shortcuts);
            Assert.All(group.Shortcuts, shortcut =>
            {
                Assert.False(string.IsNullOrWhiteSpace(shortcut.Gesture));
                Assert.False(string.IsNullOrWhiteSpace(shortcut.Action));
            });
        });
    }
}
