using System.Globalization;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Styling;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.TestSupport;
using RemoteFlow.UI.Converters;
using RemoteFlow.UI.Navigation;
using RemoteFlow.UI.ViewModels.CommandPalette;
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
        var window = new MainWindow(
            new MainWindowViewModel(navigation),
            new WindowGeometryService(new InMemorySettingsStore()));
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

    /// <summary>
    /// The main window can be loaded and placed before it is shown, which is what lets the splash cover
    /// the whole start instead of a window appearing empty and then jumping to its remembered size. It is
    /// asserted because the ordering is not obvious: restoring geometry reads the screen list, and a
    /// window that has not been shown yet is exactly where that could have been unavailable.
    /// </summary>
    [AvaloniaFact]
    public async Task MainWindowLoadsAndTakesItsRememberedGeometryBeforeItIsShown()
    {
        var settings = new InMemorySettingsStore();
        var geometry = new WindowGeometryService(settings);
        await geometry.SaveAsync(
            new WindowGeometry(120, 96, 1024, 700, false),
            TestContext.Current.CancellationToken);
        var window = new MainWindow(
            new MainWindowViewModel(NavigationService.CreateDefault()),
            geometry);

        Assert.False(window.IsVisible);
        await window.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1024, window.Width);
        Assert.Equal(700, window.Height);
        Assert.Equal(WindowStartupLocation.Manual, window.WindowStartupLocation);

        // And showing it afterwards is what the splash flow then does, so it has to survive that too.
        window.Show();
        Assert.True(window.IsVisible);
        window.Close();
    }

    /// <summary>
    /// The sidebar collapses to a rail of icons and opens again, and the choice survives a restart. The
    /// rail is asserted through what a user would see — the width, the hidden label, the tooltip that
    /// replaces it — rather than only through the flag, because the whole rail is driven by one style
    /// class and a class that stopped matching would leave the flag right and the sidebar unchanged.
    /// </summary>
    [AvaloniaFact]
    public async Task SidebarCollapsesToARailOfIconsAndRemembersIt()
    {
        var settings = new InMemorySettingsStore();
        var viewModel = new MainWindowViewModel(
            NavigationService.CreateDefault(),
            new CommandPaletteViewModel(),
            null,
            null,
            settings);
        var window = new MainWindow(viewModel, new WindowGeometryService(settings));
        window.Show();
        var list = window.FindControl<ListBox>("NavigationList");
        var footer = window.FindControl<Border>("NavigationFooter");
        Assert.NotNull(list);
        Assert.NotNull(footer);

        Assert.True(viewModel.IsNavigationExpanded);
        Assert.Equal(220, viewModel.NavigationWidth);
        Assert.DoesNotContain("collapsed", list.Classes);
        Assert.Equal("Collapse sidebar", viewModel.NavigationToggleLabel);

        viewModel.RequestToggleNavigation();
        await viewModel.NavigationChangesSettled;
        window.UpdateLayout();

        Assert.False(viewModel.IsNavigationExpanded);
        Assert.Equal(56, viewModel.NavigationWidth);
        Assert.Contains("collapsed", list.Classes);
        Assert.Contains("collapsed", footer.Classes);
        Assert.Equal("Expand sidebar", viewModel.NavigationToggleLabel);
        Assert.False(await settings.Get(SettingKeys.NavigationExpanded, TestContext.Current.CancellationToken));

        // What is left of a row once its label is gone: the glyph, and a tooltip carrying the name the
        // label used to show. Without the tooltip the collapsed rail would be four unlabelled icons.
        var row = Assert.IsType<StackPanel>(
            list.GetRealizedContainers()
                .OfType<ListBoxItem>()
                .First()
                .GetVisualDescendants()
                .OfType<StackPanel>()
                .First());
        var label = Assert.IsType<TextBlock>(row.Children.OfType<TextBlock>().First());
        Assert.False(label.IsVisible);
        Assert.Equal("Connections", ToolTip.GetTip(row));

        // And a name of its own, because the tooltip is not one: a hidden label leaves the row with
        // nothing for a screen reader to read on keyboard focus.
        Assert.Equal("Connections", AutomationProperties.GetName(row));

        // And back: the label returns rather than the rail merely widening around a hidden one.
        viewModel.RequestToggleNavigation();
        await viewModel.NavigationChangesSettled;
        window.UpdateLayout();

        Assert.True(label.IsVisible);
        Assert.Equal(220, viewModel.NavigationWidth);
        Assert.True(await settings.Get(SettingKeys.NavigationExpanded, TestContext.Current.CancellationToken));
        window.Close();
    }

    /// <summary>
    /// A restart lands on whichever state the sidebar was left in, and the first frame is drawn expanded
    /// rather than flashing shut before the preference has been read.
    /// </summary>
    [AvaloniaFact]
    public async Task SidebarRestoresTheRailItWasLeftIn()
    {
        var settings = new InMemorySettingsStore();
        await settings.Set(SettingKeys.NavigationExpanded, false, TestContext.Current.CancellationToken);
        var viewModel = new MainWindowViewModel(
            NavigationService.CreateDefault(),
            new CommandPaletteViewModel(),
            null,
            null,
            settings);

        Assert.True(viewModel.IsNavigationExpanded);

        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.False(viewModel.IsNavigationExpanded);
    }

    [AvaloniaFact]
    public void SidebarHighlightFollowsNavigationStartedOutsideTheSidebar()
    {
        var navigation = NavigationService.CreateDefault();
        var window = new MainWindow(
            new MainWindowViewModel(navigation),
            new WindowGeometryService(new InMemorySettingsStore()));
        window.Show();
        var list = window.FindControl<ListBox>("NavigationList");
        Assert.NotNull(list);
        Assert.Equal("connections", Assert.IsType<NavigationItemViewModel>(list.SelectedItem).Key);

        navigation.Navigate("terminals");

        Assert.Equal("terminals", Assert.IsType<NavigationItemViewModel>(list.SelectedItem).Key);
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

    /// <summary>The sidebar carries a resource key rather than the glyph itself, so a typo or a missing
    /// entry in Icons.axaml would only show up as a blank sidebar at runtime.</summary>
    [AvaloniaFact]
    public void EverySidebarIconKeyResolvesToGeometry()
    {
        var app = Assert.IsType<UI.App>(global::Avalonia.Application.Current);

        foreach (var item in NavigationService.CreateDefault().Items)
        {
            _ = Assert.IsAssignableFrom<Geometry>(ResourceKeyConverter.Instance.Convert(
                item.IconKey,
                typeof(Geometry),
                parameter: null,
                CultureInfo.InvariantCulture));
        }

        // Registered only in DependencyInjection, and used by the connection details panel.
        foreach (var key in new[] { "Icon.Sftp", "Icon.Backup", "Icon.Edit", "Icon.Duplicate", "Icon.Delete" })
        {
            Assert.True(app.TryFindResource(key, out var geometry));
            _ = Assert.IsAssignableFrom<Geometry>(geometry);
        }
    }

    [AvaloniaFact]
    public void UnknownIconKeysResolveToNothingRatherThanThrowing()
    {
        Assert.Null(ResourceKeyConverter.Instance.Convert(
            "Icon.NoSuchThing",
            typeof(Geometry),
            parameter: null,
            CultureInfo.InvariantCulture));
    }

    /// <summary>The taskbar icon is set through the native window handle, which a window that was never
    /// shown does not have. Off Windows there is no handle to set at all.</summary>
    [AvaloniaFact]
    public void TaskbarIconIsANoOpWithoutANativeWindowHandle()
    {
        Assert.False(WindowsTaskbarIcon.Apply(new Window()));
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
