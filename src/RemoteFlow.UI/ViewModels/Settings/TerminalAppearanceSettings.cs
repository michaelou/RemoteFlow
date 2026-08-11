using RemoteFlow.Application.Abstractions;
using XTerm.Options;

namespace RemoteFlow.UI.ViewModels.Settings;

public sealed record TerminalColorScheme(
    string Id,
    string DisplayName,
    string Background,
    string Foreground,
    string Black,
    string Red,
    string Green,
    string Yellow,
    string Blue,
    string Magenta,
    string Cyan,
    string White,
    string BrightBlack,
    string BrightRed,
    string BrightGreen,
    string BrightYellow,
    string BrightBlue,
    string BrightMagenta,
    string BrightCyan,
    string BrightWhite)
{
    public ThemeOptions ToThemeOptions()
    {
        return new ThemeOptions
        {
            Background = Background,
            Foreground = Foreground,
            Cursor = Foreground,
            CursorAccent = Background,
            Selection = "#406CB6FF",
            SelectionInactive = "#305B6573",
            Black = Black,
            Red = Red,
            Green = Green,
            Yellow = Yellow,
            Blue = Blue,
            Magenta = Magenta,
            Cyan = Cyan,
            White = White,
            BrightBlack = BrightBlack,
            BrightRed = BrightRed,
            BrightGreen = BrightGreen,
            BrightYellow = BrightYellow,
            BrightBlue = BrightBlue,
            BrightMagenta = BrightMagenta,
            BrightCyan = BrightCyan,
            BrightWhite = BrightWhite,
        };
    }
}

public static class TerminalColorSchemes
{
    public static TerminalColorScheme Dark { get; } = new(
        "dark", "RemoteFlow Dark", "#0B0F14", "#F2F5F7",
        "#0B0F14", "#FF7B72", "#5DE28C", "#FFCA58", "#6CB6FF", "#D2A8FF", "#76E3EA", "#D7E0EA",
        "#7E8998", "#FFA198", "#7EE2A8", "#FFD978", "#9CCAFF", "#E2C5FF", "#A5F3F6", "#FFFFFF");

    // Bright blue is darker than blue here, not lighter: on white, a lighter blue measured 3.39:1 and a
    // directory listing — which asks for bold blue — was the thing that read worst.
    public static TerminalColorScheme Light { get; } = new(
        "light", "Paper Light", "#FFFFFF", "#17202B",
        "#17202B", "#B42318", "#137333", "#8A5A00", "#0969DA", "#8250DF", "#0E7490", "#EAEFF5",
        "#475569", "#D92D20", "#16833A", "#A66D00", "#0B4FC0", "#A475F9", "#0891B2", "#FFFFFF");

    public static TerminalColorScheme HighContrast { get; } = new(
        "high-contrast", "High Contrast", "#000000", "#FFFFFF",
        "#000000", "#FF6B6B", "#72FF72", "#FFFF72", "#75B7FF", "#FF8CFF", "#72FFFF", "#FFFFFF",
        "#B3B3B3", "#FF9B9B", "#A5FFA5", "#FFFFA5", "#A8D1FF", "#FFBFFF", "#A5FFFF", "#FFFFFF");

    public static IReadOnlyList<TerminalColorScheme> All { get; } = [Dark, Light, HighContrast];

    public static TerminalColorScheme Resolve(string? id)
    {
        return All.FirstOrDefault(scheme => string.Equals(scheme.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Dark;
    }
}

public sealed record TerminalAppearanceSettings(
    string FontFamily,
    int FontSize,
    int Scrollback,
    TerminalColorScheme ColorScheme,
    TerminalCursorStyle CursorStyle,
    bool CursorBlink,
    TerminalBellMode BellMode);
