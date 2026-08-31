using XTerm.Buffer;

namespace RemoteFlow.UI.ViewModels.Terminal;

/// <summary>
/// Keeps the insert-characters control sequence away from the terminal emulator, and applies it to the
/// buffer instead.
/// </summary>
/// <remarks>
/// <para>
/// XTerm.NET 1.0.15 shifts a line's cells to the right with a forward copy over itself
/// (<c>XTerm.InputHandler.InsertChars</c> calls <c>BufferLine.CopyCellsFrom(..., applyInReverse: false)</c>),
/// so each cell is read after the copy has already overwritten it: <c>CSI 1 @</c> in the middle of
/// <c>abcdefgh</c> leaves <c>abcde</c> followed by <c>f</c> repeated to the end of the row. A shift to the
/// right of an overlapping range has to run backwards.
/// </para>
/// <para>
/// That sequence is not exotic — it is how readline edits a line. With <c>TERM=xterm-256color</c> terminfo
/// carries <c>ich</c>, so inserting a character anywhere but at the end of the line makes bash emit
/// <c>CSI 1 @</c> and then print the one character, rather than reprinting the tail. Every mid-line edit
/// therefore smeared the row: typing into a recalled command showed one character repeated across it, and
/// where the cell at the cursor was a space — after a backspace, or in pasted text — the row was smeared
/// with spaces, which reads as the rest of the line being wiped. The shell's own line buffer was never
/// touched, so Enter ran the right command: the damage was only ever on screen.
/// </para>
/// <para>
/// The defect is upstream, filed as <see href="https://github.com/tomlm/XTerm.NET/issues/121" />, and is present in every release up to 1.2.0 and in
/// 2.0.0-rc005. Until one carries the fix, <see cref="Split" /> cuts each sequence out of the output and
/// <see cref="Apply" /> performs the shift on
/// the same public buffer the emulator would have used. <c>CSI 4 h</c> (insert mode) reaches the same
/// defective copy in <c>InputHandler.Print</c> and is left alone: nothing in a shell session uses it, and
/// emulating it would mean taking over printing entirely.
/// </para>
/// </remarks>
internal sealed class InsertCharacterCorrection
{
    /// <summary>A sequence longer than this cannot be the one being looked for, so it is passed through.</summary>
    private const int _maximumSequenceLength = 24;

    /// <summary>
    /// A sequence that begins in one output frame and ends in the next. Held back rather than fed, so that
    /// the emulator is never handed the half of it that would put its parser inside the sequence.
    /// </summary>
    private string _partialSequence = string.Empty;

    /// <summary>
    /// Splits an output frame around every insert-characters sequence in it.
    /// </summary>
    /// <remarks>
    /// The segments have to be applied in order: a sequence acts at the cursor position the text before it
    /// left behind.
    /// </remarks>
    public IReadOnlyList<TerminalOutputSegment> Split(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var segments = new List<TerminalOutputSegment>();
        var pending = _partialSequence;
        _partialSequence = string.Empty;
        var remaining = pending.Length == 0 ? text : pending + text;
        var start = 0;
        var index = 0;
        while (index < remaining.Length)
        {
            var escape = remaining.IndexOf('\u001b', index);
            if (escape < 0)
            {
                break;
            }

            var outcome = Scan(remaining, escape, out var end, out var count);
            if (outcome == ScanOutcome.Incomplete)
            {
                // The rest of the frame is the beginning of a sequence that may yet turn out to be this one.
                _partialSequence = remaining[escape..];
                AddText(segments, remaining[start..escape]);
                return segments;
            }

            if (outcome == ScanOutcome.NotInsertCharacters)
            {
                index = escape + 1;
                continue;
            }

            AddText(segments, remaining[start..escape]);
            segments.Add(TerminalOutputSegment.Insert(count));
            start = end;
            index = end;
        }

        AddText(segments, remaining[start..]);
        return segments;
    }

    /// <summary>
    /// Inserts <paramref name="count" /> blanks at the cursor, pushing the rest of the row to the right.
    /// </summary>
    /// <remarks>
    /// The blanks take the attributes of the cell they displace rather than the emulator's current
    /// attributes, which are private to it. Both agree wherever the run being edited is one colour, and a
    /// caller of the sequence overwrites the blanks in the same breath anyway.
    /// </remarks>
    public static void Apply(SvcSystems.UI.Terminal.Terminal terminal, int count)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        var buffer = terminal.Buffer;
        var line = buffer.Lines[buffer.YBase + buffer.Y];
        if (line is null)
        {
            return;
        }

        // The cursor sits one past the last column while a wrap is pending, and the emulator's own handler
        // reads it unclamped; a shift from there moves nothing, which is what a terminal does.
        var column = buffer.X;
        var columns = terminal.Cols;
        var inserted = Math.Min(count, columns - column);
        if (inserted <= 0)
        {
            return;
        }

        var blank = BufferCell.Space;
        blank.Attributes = line[column].Attributes;
        line.CopyCellsFrom(line, column, column + inserted, columns - column - inserted, applyInReverse: true);
        line.Fill(blank, column, column + inserted);
    }

    private static void AddText(List<TerminalOutputSegment> segments, string text)
    {
        if (text.Length > 0)
        {
            segments.Add(TerminalOutputSegment.Output(text));
        }
    }

    /// <summary>
    /// Reads the sequence that starts at <paramref name="escape" />.
    /// </summary>
    /// <remarks>
    /// Only the plain form is claimed: <c>CSI</c>, decimal parameters, then <c>@</c>. A private marker or an
    /// intermediate byte makes it some other sequence, and passing those through unchanged keeps this from
    /// deciding what the emulator does with them. String sequences are not tracked, so a title or a device
    /// payload whose text happened to spell this one out would be cut from it — which needs an escape
    /// character inside a string sequence that has no business carrying one.
    /// </remarks>
    private static ScanOutcome Scan(string text, int escape, out int end, out int count)
    {
        end = escape;
        count = 1;
        var index = escape + 1;
        if (index >= text.Length)
        {
            return ScanOutcome.Incomplete;
        }

        if (text[index] != '[')
        {
            return ScanOutcome.NotInsertCharacters;
        }

        var parameterStart = ++index;
        while (index < text.Length && (char.IsAsciiDigit(text[index]) || text[index] == ';'))
        {
            index++;
        }

        if (index - escape > _maximumSequenceLength)
        {
            return ScanOutcome.NotInsertCharacters;
        }

        if (index >= text.Length)
        {
            return ScanOutcome.Incomplete;
        }

        if (text[index] != '@')
        {
            return ScanOutcome.NotInsertCharacters;
        }

        end = index + 1;
        var parameters = text[parameterStart..index];
        var separator = parameters.IndexOf(';');
        var first = separator < 0 ? parameters : parameters[..separator];
        // An omitted or zero parameter means one, the same default the emulator applies.
        count = int.TryParse(first, out var parsed) ? Math.Max(parsed, 1) : 1;
        return ScanOutcome.InsertCharacters;
    }

    private enum ScanOutcome
    {
        InsertCharacters,
        NotInsertCharacters,
        Incomplete,
    }
}
