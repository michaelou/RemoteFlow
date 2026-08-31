using System.Text;
using Avalonia.Headless.XUnit;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Infrastructure.Pty;
using RemoteFlow.UI.Services;
using RemoteFlow.UI.ViewModels.Terminal;
using SvcSystems.UI.Terminal;
using Xunit;

namespace RemoteFlow.UI.Tests;

/// <summary>
/// A real shell, edited in the middle of a line, shown correctly.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TerminalInsertCharacterTests" /> replays the sequences readline emits; this drives readline
/// itself, over a pseudoterminal, through the same view model the workspace uses. It is the end of the
/// chain that the replay cannot prove: that the sequence a live bash chooses for the edit is the one being
/// corrected, and that it is still the one after an emulator upgrade.
/// </para>
/// <para>
/// The keystrokes are sent a step at a time and each step is allowed to land. That is not politeness: given
/// the whole edit in a single read, readline redraws the line once at the end and never opens a gap, so a
/// test that sent everything at once would pass whatever the emulator did with the sequence. For the same
/// reason the line has to be short enough not to wrap — readline reprints a wrapped line instead of
/// shifting cells within a row, which is why this was only ever seen on lines that fit.
/// </para>
/// </remarks>
public sealed class TerminalLiveShellEditingTests
{
    private const int _columns = 80;
    private const int _rows = 24;
    private const string _cursorLeft = "";
    private const string _backspace = "";

    [AvaloniaFact]
    public async Task TypingIntoTheMiddleOfALineShowsWhatTheShellRuns()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The test drives bash over a Linux pseudoterminal.");
        Assert.SkipUnless(File.Exists("/bin/bash"), "The test drives bash.");
        var token = TestContext.Current.CancellationToken;
        await using var channel = await new PortaPtyService().SpawnAsync(
            new PtySpawnOptions
            {
                ShellPath = "/bin/bash",
                // The user's own configuration would decide the prompt, the width left for the line and
                // whether readline is even in charge of it.
                Arguments = ["--norc", "--noprofile", "-i"],
                WorkingDirectory = Path.GetTempPath(),
                EnvironmentVariables = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["TERM"] = "xterm-256color",
                },
                Columns = _columns,
                Rows = _rows,
            },
            token);
        await using var session = new TerminalSessionViewModel(
            channel,
            new UiDispatcher(),
            new TerminalControlModel(new TerminalOptions
            {
                Cols = _columns,
                Rows = _rows,
                Scrollback = 200,
                ReflowOnResize = false,
                TermName = "xterm-256color",
            }));

        const string head = "aaaaaaaaaa";
        const string tail = "bbbbbbbbbb";
        await TypeAsync(session, $"echo {head}MIDDLE{tail}", token);
        // Eleven columns from the end is the E of MIDDLE: the ten characters of the tail, and then one more.
        await TypeAsync(session, string.Concat(Enumerable.Repeat(_cursorLeft, tail.Length + 1)), token);
        await TypeAsync(session, _backspace, token);
        await TypeAsync(session, "XY", token);

        const string edited = $"{head}MIDDXYE{tail}";
        await UntilAsync(session, screen => CommandLine(screen) == $"echo {edited}", token);
        Assert.Equal($"echo {edited}", CommandLine(Screen(session)));

        // And the shell agrees: echo writes its argument back, on a row of its own.
        await TypeAsync(session, "\n", token);
        await UntilAsync(session, screen => screen.Any(row => row.Contains(edited, StringComparison.Ordinal)), token);
    }

    /// <summary>The line being edited, from the command onwards, so that the prompt does not matter.</summary>
    private static string CommandLine(List<string> screen)
    {
        var row = screen.LastOrDefault(candidate => candidate.Contains("echo ", StringComparison.Ordinal));
        return row is null ? string.Empty : row[row.IndexOf("echo ", StringComparison.Ordinal)..];
    }

    private static List<string> Screen(TerminalSessionViewModel session)
    {
        var buffer = session.Model.Terminal.Buffer;
        var rows = new List<string>(_rows);
        for (var row = 0; row < _rows; row++)
        {
            rows.Add(buffer.Lines[buffer.YDisp + row]?.TranslateToString(true) ?? string.Empty);
        }

        return rows;
    }

    /// <summary>
    /// Sends one step of the edit and waits for the shell to answer it.
    /// </summary>
    /// <remarks>
    /// Cursor movement produces no visible change, so there is nothing to wait for but quiet: two frame
    /// budgets without output means readline has finished redrawing.
    /// </remarks>
    private static async Task TypeAsync(
        TerminalSessionViewModel session,
        string keystrokes,
        CancellationToken cancellationToken)
    {
        await session.SendInputAsync(Encoding.UTF8.GetBytes(keystrokes), cancellationToken);
        var frames = session.OutputFramesApplied;
        var quiet = 0;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (quiet < 6)
        {
            Assert.True(DateTime.UtcNow < deadline, "The shell never stopped writing.");
            await Task.Delay(25, cancellationToken);
            if (session.OutputFramesApplied == frames)
            {
                quiet++;
                continue;
            }

            frames = session.OutputFramesApplied;
            quiet = 0;
        }
    }

    private static async Task UntilAsync(
        TerminalSessionViewModel session,
        Func<List<string>, bool> condition,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition(Screen(session)))
        {
            Assert.True(
                DateTime.UtcNow < deadline,
                $"The screen never showed what was expected. It showed:{System.Environment.NewLine}" +
                    string.Join(System.Environment.NewLine, Screen(session)));
            await Task.Delay(25, cancellationToken);
        }
    }
}
