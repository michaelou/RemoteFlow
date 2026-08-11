using Avalonia.Headless.XUnit;
using Avalonia.Media;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.TestSupport;
using RemoteFlow.UI.Services;
using RemoteFlow.UI.ViewModels.Settings;
using Xunit;

namespace RemoteFlow.UI.Tests;

/// <summary>
/// The terminal renderer does not paint from the engine's theme: it looks each ANSI colour up in application
/// resources and, finding none, falls back to the VGA palette. That is how <c>ls</c> printed directory names
/// in navy (#000080) on a near-black background — 1.3:1, and unreadable — while the colour scheme said
/// #6CB6FF. These tests hold the resources to the scheme and the scheme to a contrast floor.
/// </summary>
public sealed class TerminalPaletteTests
{
    /// <summary>Blue is index 4 and bright blue index 12: what a directory listing uses.</summary>
    private const int _blueIndex = 4;

    [AvaloniaFact]
    public void EveryAnsiColourOfTheSchemeIsPublishedWhereTheRendererLooksForIt()
    {
        var application = global::Avalonia.Application.Current!;

        TerminalPaletteResources.Apply(application.Resources, TerminalColorSchemes.Dark);

        var expected = TerminalPaletteResources.AnsiColors(TerminalColorSchemes.Dark);
        Assert.Equal(16, expected.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            Assert.True(
                application.Resources.TryGetResource(
                    $"{TerminalPaletteResources.KeyPrefix}{index}",
                    application.ActualThemeVariant,
                    out var value),
                $"ANSI colour {index} is not published, so the renderer will use its own fallback.");
            // A bare Color is silently ignored by the renderer; only a brush is honoured.
            var brush = Assert.IsType<SolidColorBrush>(value);
            Assert.Equal(Color.Parse(expected[index]), brush.Color);
        }
    }

    /// <summary>The renderer resolves through these resources, so this is the palette a user actually reads.
    /// Blue is the one that was wrong, and it is also the one every directory listing depends on.</summary>
    [AvaloniaTheory]
    [InlineData("dark")]
    [InlineData("light")]
    [InlineData("high-contrast")]
    public void DirectoryBlueIsReadableAgainstItsOwnBackground(string schemeId)
    {
        var scheme = TerminalColorSchemes.Resolve(schemeId);
        var background = Color.Parse(scheme.Background);

        foreach (var index in new[] { _blueIndex, _blueIndex + 8 })
        {
            var colors = TerminalPaletteResources.AnsiColors(scheme);
            var ratio = Contrast(Color.Parse(colors[index]), background);
            Assert.True(
                ratio >= 4.5,
                $"{scheme.DisplayName} ANSI colour {index} is {ratio:F2}:1 against its background, " +
                "which is below the 4.5:1 floor for text.");
        }
    }

    /// <summary>Startup is the only moment guaranteed to happen before a terminal is on screen, and the
    /// palette has to be in place by then.</summary>
    [AvaloniaFact]
    public async Task StartupPublishesThePaletteOfTheStoredScheme()
    {
        var token = TestContext.Current.CancellationToken;
        var settings = new InMemorySettingsStore();
        await settings.Set(SettingKeys.TerminalColorScheme, TerminalColorSchemes.HighContrast.Id, token);
        var application = global::Avalonia.Application.Current!;
        _ = application.Resources.Remove($"{TerminalPaletteResources.KeyPrefix}{_blueIndex}");

        await new ThemeService(application, settings).InitializeAsync(token);

        Assert.True(application.Resources.TryGetResource(
            $"{TerminalPaletteResources.KeyPrefix}{_blueIndex}",
            application.ActualThemeVariant,
            out var value));
        Assert.Equal(
            Color.Parse(TerminalColorSchemes.HighContrast.Blue),
            Assert.IsType<SolidColorBrush>(value).Color);
    }

    private static double Contrast(Color first, Color second)
    {
        static double Luminance(Color color)
        {
            static double Channel(byte value)
            {
                var component = value / 255d;
                return component <= 0.03928
                    ? component / 12.92
                    : Math.Pow((component + 0.055) / 1.055, 2.4);
            }

            return (0.2126 * Channel(color.R)) + (0.7152 * Channel(color.G)) + (0.0722 * Channel(color.B));
        }

        var brighter = Math.Max(Luminance(first), Luminance(second));
        var darker = Math.Min(Luminance(first), Luminance(second));
        return (brighter + 0.05) / (darker + 0.05);
    }
}
