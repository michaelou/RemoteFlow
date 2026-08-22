using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Xunit;

namespace RemoteFlow.UI.Tests;

/// <summary>
/// Dark is the default and the only theme most people will ever see, so the palette is audited rather
/// than eyeballed. Text is held to 4.5:1 and anything a user has to find — a focus ring, a control
/// boundary — to 3:1, per WCAG 2.2 1.4.3 and 1.4.11.
/// </summary>
public sealed class DarkPaletteContrastTests
{
    private const double _textMinimum = 4.5;
    private const double _componentMinimum = 3.0;

    /// <summary>Every surface a foreground can legitimately land on.</summary>
    private static readonly string[] _surfaces = ["Color.Surface.0", "Color.Surface.1", "Color.Surface.2"];

    public static TheoryData<string> TextColors =>
    [
        "Color.Text.Primary",
        "Color.Text.Secondary",
        "Color.Text.Disabled",
        "Color.Text.Muted",
        "Color.Accent",
        "Color.Success",
        "Color.Warning",
        "Color.Danger",
        "Color.Environment.Dev",
        "Color.Environment.Staging",
        "Color.Environment.Production",
    ];

    [AvaloniaTheory]
    [MemberData(nameof(TextColors))]
    public void TextReadsAgainstEverySurfaceItCanSitOn(string key)
    {
        var app = global::Avalonia.Application.Current!;
        var foreground = GetColor(app, key);

        foreach (var surface in _surfaces)
        {
            var ratio = Contrast(foreground, GetColor(app, surface));
            Assert.True(
                ratio >= _textMinimum,
                $"{key} on {surface} is {ratio:F2}:1, below the {_textMinimum}:1 floor for text.");
        }
    }

    /// <summary>
    /// The ring has to be found against everything it can land on — which includes the primary button,
    /// whose fill is the system accent. That case is why the ring is not itself the accent: blue on blue
    /// measured 2.1:1 and read as no ring at all.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void TheFocusRingIsFindableOnEverythingItCanLandOn(string variantName)
    {
        var app = global::Avalonia.Application.Current!;
        var variant = variantName == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var focus = GetColor(app, "Color.Focus", variant);
        Assert.True(app.TryGetResource("SystemAccentColor", variant, out var accent));

        foreach (var behind in _surfaces.Select(key => GetColor(app, key, variant))
                     .Append(Assert.IsType<Color>(accent)))
        {
            var ratio = Contrast(focus, behind);
            Assert.True(
                ratio >= _componentMinimum,
                $"The {variantName} focus ring on {behind} is {ratio:F2}:1, below {_componentMinimum}:1.");
        }
    }

    [AvaloniaFact]
    public void BadgeTextReadsOnItsOwnBadge()
    {
        var app = global::Avalonia.Application.Current!;

        var ratio = Contrast(GetColor(app, "Color.Badge.Text"), GetColor(app, "Color.Badge.Background"));

        Assert.True(ratio >= _textMinimum, $"Badge text is {ratio:F2}:1 on its badge.");
    }

    /// <summary>
    /// The outline that tells a text box apart from the page comes from the Fluent theme, not from
    /// RemoteFlow's own tokens, and it is a translucent white. What matters is the colour it composites
    /// to over each of our surfaces, which is what this measures — a theme upgrade that dimmed it would
    /// fail here rather than quietly making every input harder to find.
    /// </summary>
    [AvaloniaFact]
    public void ControlOutlinesStandOutFromTheSurfacesBehindThem()
    {
        var app = global::Avalonia.Application.Current!;
        Assert.True(app.TryGetResource("TextControlBorderBrush", ThemeVariant.Dark, out var value));
        var stroke = Assert.IsAssignableFrom<ISolidColorBrush>(value).Color;

        foreach (var surface in _surfaces)
        {
            var background = GetColor(app, surface);
            var ratio = Contrast(Composite(stroke, background), background);
            Assert.True(
                ratio >= _componentMinimum,
                $"The control outline over {surface} is {ratio:F2}:1, below the {_componentMinimum}:1 floor.");
        }
    }

    /// <summary>
    /// The inline error banner's fill. It is a token rather than the hard-coded translucent red the SFTP
    /// workspace still carries precisely so that it can be measured: an opaque colour has a contrast
    /// ratio, and a translucent one only has whatever it happens to composite onto that day.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void TheErrorBannerReadsAgainstItsOwnFill(string variantName)
    {
        var app = global::Avalonia.Application.Current!;
        var variant = variantName == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var fill = GetColor(app, "Color.Danger.Surface", variant);

        foreach (var key in new[] { "Color.Text.Primary", "Color.Danger" })
        {
            var ratio = Contrast(GetColor(app, key, variant), fill);
            Assert.True(
                ratio >= _textMinimum,
                $"{key} on the error banner is {ratio:F2}:1, below the {_textMinimum}:1 floor for text.");
        }

        // The banner's own outline has to be findable against the fill it surrounds.
        var border = Contrast(GetColor(app, "Color.Danger", variant), fill);
        Assert.True(border >= _componentMinimum, $"The banner outline is {border:F2}:1.");
    }

    /// <summary>Flattens a translucent foreground onto an opaque background, the way the compositor does.</summary>
    private static Color Composite(Color foreground, Color background)
    {
        var alpha = foreground.A / 255d;
        static byte Blend(byte over, byte under, double alpha)
        {
            return (byte)Math.Round((alpha * over) + ((1 - alpha) * under));
        }

        return Color.FromRgb(
            Blend(foreground.R, background.R, alpha),
            Blend(foreground.G, background.G, alpha),
            Blend(foreground.B, background.B, alpha));
    }

    private static Color GetColor(global::Avalonia.Application app, string key, ThemeVariant? variant = null)
    {
        Assert.True(app.TryGetResource(key, variant ?? ThemeVariant.Dark, out var value), $"{key} is missing.");
        return Assert.IsType<Color>(value);
    }

    private static double Contrast(Color first, Color second)
    {
        var lighter = Math.Max(Luminance(first), Luminance(second));
        var darker = Math.Min(Luminance(first), Luminance(second));
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double Luminance(Color color)
    {
        static double Channel(byte value)
        {
            var normalized = value / 255d;
            return normalized <= 0.04045
                ? normalized / 12.92
                : Math.Pow((normalized + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(color.R)) + (0.7152 * Channel(color.G)) + (0.0722 * Channel(color.B));
    }
}
