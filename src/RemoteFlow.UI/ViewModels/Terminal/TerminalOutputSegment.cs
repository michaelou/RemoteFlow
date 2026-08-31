namespace RemoteFlow.UI.ViewModels.Terminal;

/// <summary>
/// One piece of an output frame: either text for the emulator, or an insert-characters sequence that was
/// taken out of it.
/// </summary>
internal readonly record struct TerminalOutputSegment(string Text, int InsertCount)
{
    public static TerminalOutputSegment Output(string text)
    {
        return new TerminalOutputSegment(text, 0);
    }

    public static TerminalOutputSegment Insert(int count)
    {
        return new TerminalOutputSegment(string.Empty, count);
    }
}
