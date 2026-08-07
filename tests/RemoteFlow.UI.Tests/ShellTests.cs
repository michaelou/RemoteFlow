using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.TestSupport;
using RemoteFlow.UI.Navigation;
using RemoteFlow.UI.Services;
using RemoteFlow.UI.ViewModels;
using RemoteFlow.UI.Views;
using Xunit;

namespace RemoteFlow.UI.Tests;

public sealed class ShellTests
{
    [AvaloniaFact]
    public void AppStartsDarkAndThemeResourcesResolveForBothVariants()
    {
        var app = Assert.IsType<UI.App>(global::Avalonia.Application.Current);

        Assert.Equal(ThemeVariant.Dark, app.RequestedThemeVariant);
        Assert.Equal(ThemeVariant.Dark, app.ActualThemeVariant);
        Assert.True(app.TryGetResource("Color.Surface.0", ThemeVariant.Dark, out var darkSurface));
        Assert.True(app.TryGetResource("Color.Surface.0", ThemeVariant.Light, out var lightSurface));
        Assert.NotEqual(darkSurface, lightSurface);
    }

    [AvaloniaFact]
    public async Task ThemeCanSwitchAtRuntimeAndPersists()
    {
        var app = Assert.IsType<UI.App>(global::Avalonia.Application.Current);
        var settings = new InMemorySettingsStore();
        var service = new ThemeService(app, settings);

        await service.SetThemeAsync(AppTheme.Light, TestContext.Current.CancellationToken);

        Assert.Equal(ThemeVariant.Light, app.RequestedThemeVariant);
        Assert.Equal(ThemeVariant.Light, app.ActualThemeVariant);
        Assert.Equal(AppTheme.Light, await settings.Get(SettingKeys.Theme, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void NavigationReturnsTheSamePageInstanceAndPreservesState()
    {
        var navigation = NavigationService.CreateDefault();
        var connections = navigation.CurrentPage;
        connections.StateText = "preserve me";

        navigation.Navigate("settings");
        navigation.Navigate("connections");

        Assert.Same(connections, navigation.CurrentPage);
        Assert.Equal("preserve me", navigation.CurrentPage.StateText);
    }

    [AvaloniaFact]
    public void SidebarSupportsArrowKeysAndEnter()
    {
        var navigation = NavigationService.CreateDefault();
        var window = new MainWindow(new MainWindowViewModel(navigation), new InMemorySettingsStore());
        window.Show();
        var list = window.FindControl<ListBox>("NavigationList");
        Assert.NotNull(list);
        _ = list.Focus();

        list.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Down,
            PhysicalKey = PhysicalKey.ArrowDown,
        });
        list.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Enter,
            PhysicalKey = PhysicalKey.Enter,
        });

        Assert.Equal("Terminals", navigation.CurrentPage.Title);
        window.Close();
    }

    [Fact]
    public void OffScreenGeometryIsClampedToThePrimaryMonitor()
    {
        var geometry = new WindowGeometry(9000, 9000, 1200, 760, true);
        MonitorWorkArea[] monitors =
        [
            new(0, 0, 1920, 1040, 1, true),
            new(1920, 0, 2560, 1400, 1.25, false),
        ];

        var clamped = geometry.ClampToVisibleMonitor(monitors);

        Assert.Equal(720, clamped.X);
        Assert.Equal(280, clamped.Y);
        Assert.True(clamped.IsMaximized);
    }

    [Fact]
    public async Task WindowGeometryRoundTripsThroughSettings()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var settings = new InMemorySettingsStore();
        var service = new WindowGeometryService(settings);
        var expected = new WindowGeometry(80, 60, 1100, 700, true);
        MonitorWorkArea[] monitors = [new(0, 0, 1920, 1040, 1, true)];

        await service.SaveAsync(expected, cancellationToken);
        var actual = await service.RestoreAsync(monitors, cancellationToken);

        Assert.Equal(expected, actual);
    }

    [AvaloniaFact]
    public void EnvironmentColorsMeetContrastOnDarkSurface()
    {
        var app = Assert.IsType<UI.App>(global::Avalonia.Application.Current);
        var surface = GetColor(app, "Color.Surface.0");

        Assert.True(Contrast(GetColor(app, "Color.Environment.Dev"), surface) >= 4.5);
        Assert.True(Contrast(GetColor(app, "Color.Environment.Staging"), surface) >= 4.5);
        Assert.True(Contrast(GetColor(app, "Color.Environment.Production"), surface) >= 4.5);
    }

    private static Color GetColor(global::Avalonia.Application app, string key)
    {
        Assert.True(app.TryGetResource(key, ThemeVariant.Dark, out var value));
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
