using System.Text;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Services;
using Xunit;

namespace RemoteFlow.Application.Tests;

public sealed class KeymapServiceTests
{
    private readonly KeymapService _keymap = new();

    [Fact]
    public void EveryDocumentedBindingResolvesToItsExactCommandOrBytes()
    {
        foreach (var binding in _keymap.Bindings)
        {
            IEnumerable<KeymapPlatform> platforms = binding.Platform is { } platform
                ? [platform]
                : Enum.GetValues<KeymapPlatform>();
            foreach (var candidate in platforms)
            {
                var normal = _keymap.Resolve(binding.Stroke, candidate);
                if (binding.Command is { } command)
                {
                    Assert.Equal(KeymapResultKind.ApplicationCommand, normal.Kind);
                    Assert.Equal(command, normal.Command);
                    Assert.Empty(normal.Bytes.ToArray());
                }
                else
                {
                    Assert.Equal(KeymapResultKind.PtyBytes, normal.Kind);
                    Assert.Equal(binding.NormalBytes, normal.Bytes.ToArray());
                    var application = _keymap.Resolve(binding.Stroke, candidate, applicationCursorKeys: true);
                    Assert.Equal(binding.ApplicationCursorBytes ?? binding.NormalBytes, application.Bytes.ToArray());
                }
            }
        }
    }

    [Fact]
    public void CtrlCAlwaysSendsSigintUnlessTheOptionalSelectionPolicyAppliesOnce()
    {
        var stroke = new TerminalKeyStroke(TerminalKey.C, TerminalModifiers.Control);

        Assert.Equal(new byte[] { 0x03 }, _keymap.Resolve(stroke, KeymapPlatform.WindowsLinux).Bytes.ToArray());
        var copy = _keymap.Resolve(
            stroke,
            KeymapPlatform.WindowsLinux,
            ctrlCPolicy: CtrlCPolicy.CopyWhenSelected,
            hasSelection: true);
        Assert.Equal(KeymapCommand.Copy, copy.Command);
        Assert.Equal(
            new byte[] { 0x03 },
            _keymap.Resolve(
                stroke,
                KeymapPlatform.WindowsLinux,
                ctrlCPolicy: CtrlCPolicy.CopyWhenSelected,
                hasSelection: false).Bytes.ToArray());
    }

    [Fact]
    public void ClipboardAndTabShortcutsNeverProducePtyBytes()
    {
        var copy = _keymap.Resolve(
            new TerminalKeyStroke(TerminalKey.C, TerminalModifiers.Control | TerminalModifiers.Shift),
            KeymapPlatform.WindowsLinux);
        var altOne = _keymap.Resolve(
            new TerminalKeyStroke(TerminalKey.D1, TerminalModifiers.Alt, "1"),
            KeymapPlatform.WindowsLinux);

        Assert.Equal(KeymapCommand.Copy, copy.Command);
        Assert.Empty(copy.Bytes.ToArray());
        Assert.Equal(KeymapCommand.SwitchToTerminal1, altOne.Command);
        Assert.Empty(altOne.Bytes.ToArray());
    }

    [Fact]
    public void TuiCriticalControlKeysAreNotShadowedByApplicationCommands()
    {
        TerminalKey[] keys =
        [
            TerminalKey.A,
            TerminalKey.B,
            TerminalKey.D,
            TerminalKey.E,
            TerminalKey.F,
            TerminalKey.G,
            TerminalKey.H,
            TerminalKey.K,
            TerminalKey.L,
            TerminalKey.N,
            TerminalKey.O,
            TerminalKey.P,
            TerminalKey.Q,
            TerminalKey.R,
            TerminalKey.S,
            TerminalKey.U,
            TerminalKey.X,
            TerminalKey.Y,
            TerminalKey.Z,
        ];

        foreach (var key in keys)
        {
            var result = _keymap.Resolve(
                new TerminalKeyStroke(key, TerminalModifiers.Control),
                KeymapPlatform.WindowsLinux);
            Assert.Equal(KeymapResultKind.PtyBytes, result.Kind);
            Assert.Equal(new byte[] { (byte)((int)key - (int)TerminalKey.A + 1) }, result.Bytes.ToArray());
        }
    }

    [Fact]
    public void ArrowsUseCsiNormallyAndSs3InApplicationCursorMode()
    {
        var up = new TerminalKeyStroke(TerminalKey.Up);

        Assert.Equal(Escape("[A"), _keymap.Resolve(up, KeymapPlatform.WindowsLinux).Bytes.ToArray());
        Assert.Equal(
            Escape("OA"),
            _keymap.Resolve(up, KeymapPlatform.WindowsLinux, applicationCursorKeys: true).Bytes.ToArray());
    }

    [Fact]
    public void MacProfileUsesCommandForClipboardAndKeepsControlCAsSigint()
    {
        Assert.Equal(
            KeymapCommand.Copy,
            _keymap.Resolve(
                new TerminalKeyStroke(TerminalKey.C, TerminalModifiers.Command),
                KeymapPlatform.MacOs).Command);
        Assert.Equal(
            KeymapCommand.Paste,
            _keymap.Resolve(
                new TerminalKeyStroke(TerminalKey.V, TerminalModifiers.Command),
                KeymapPlatform.MacOs).Command);
        Assert.Equal(
            new byte[] { 0x03 },
            _keymap.Resolve(
                new TerminalKeyStroke(TerminalKey.C, TerminalModifiers.Control),
                KeymapPlatform.MacOs).Bytes.ToArray());
    }

    [Fact]
    public void AltTextUsesEscapePrefixAndUtf8()
    {
        var result = _keymap.Resolve(
            new TerminalKeyStroke(TerminalKey.None, TerminalModifiers.Alt, "\u00F8"),
            KeymapPlatform.WindowsLinux);

        Assert.Equal(new byte[] { 0x1B, 0xC3, 0xB8 }, result.Bytes.ToArray());
    }

    /// <summary>
    /// A terminal consumes Tab, so without an escape hatch focus that enters it can never leave — a
    /// keyboard trap. F6 is the way out, and Shift+F6 still reaches the remote program.
    /// </summary>
    [Fact]
    public void F6LeavesTheTerminalAndShiftF6StillReachesIt()
    {
        var leave = _keymap.Resolve(
            new TerminalKeyStroke(TerminalKey.F6),
            KeymapPlatform.WindowsLinux);
        var passthrough = _keymap.Resolve(
            new TerminalKeyStroke(TerminalKey.F6, TerminalModifiers.Shift),
            KeymapPlatform.WindowsLinux);

        Assert.Equal(KeymapCommand.LeaveTerminal, leave.Command);
        Assert.Equal(Escape("[17~"), passthrough.Bytes.ToArray());
    }

    [Fact]
    public void GeneratedDocumentationCannotDriftFromTheKeymapTable()
    {
        var root = FindRepositoryRoot();
        var documentation = File.ReadAllText(Path.Combine(root, "docs", "keybindings.md"))
            .ReplaceLineEndings("\n");

        Assert.StartsWith(_keymap.GenerateMarkdown(), documentation, StringComparison.Ordinal);
    }

    private static byte[] Escape(string suffix)
    {
        return [0x1B, .. Encoding.ASCII.GetBytes(suffix)];
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RemoteFlow.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not find the repository root.");
    }
}
