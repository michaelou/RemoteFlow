using Avalonia.Controls;
using Avalonia.Media;
using RemoteFlow.UI.ViewModels.Settings;

namespace RemoteFlow.UI.Services;

/// <summary>
/// Publishes a colour scheme's sixteen ANSI colours where the terminal renderer looks for them.
/// </summary>
/// <remarks>
/// The renderer resolves each ANSI colour from a resource named <c>SvcSystems.UI.TerminalColor&lt;n&gt;</c>
/// and, when the resource is missing, falls back to the VGA palette — whose blue is <c>#000080</c>. That is
/// how <c>ls</c> came to print directory names in navy on a near-black background: the scheme's own colours
/// were being handed to the terminal engine, which is not what paints them. The resource has to be a
/// <see cref="SolidColorBrush" />; a bare <see cref="Color" /> is ignored and the fallback wins.
/// </remarks>
public static class TerminalPaletteResources
{
    internal const string KeyPrefix = "SvcSystems.UI.TerminalColor";

    /// <summary>The ANSI order the renderer indexes: eight normal colours, then their bright forms.</summary>
    public static IReadOnlyList<string> AnsiColors(TerminalColorScheme scheme)
    {
        ArgumentNullException.ThrowIfNull(scheme);
        return
        [
            scheme.Black, scheme.Red, scheme.Green, scheme.Yellow,
            scheme.Blue, scheme.Magenta, scheme.Cyan, scheme.White,
            scheme.BrightBlack, scheme.BrightRed, scheme.BrightGreen, scheme.BrightYellow,
            scheme.BrightBlue, scheme.BrightMagenta, scheme.BrightCyan, scheme.BrightWhite,
        ];
    }

    public static void Apply(IResourceDictionary resources, TerminalColorScheme scheme)
    {
        ArgumentNullException.ThrowIfNull(resources);
        var colors = AnsiColors(scheme);
        for (var index = 0; index < colors.Count; index++)
        {
            resources[$"{KeyPrefix}{index}"] = new SolidColorBrush(Color.Parse(colors[index]));
        }
    }

    /// <summary>Applies the scheme to the running application, if there is one. Called from view models that
    /// own the scheme but not the application.</summary>
    public static void ApplyToApplication(TerminalColorScheme scheme)
    {
        if (global::Avalonia.Application.Current is { } application)
        {
            Apply(application.Resources, scheme);
        }
    }
}
