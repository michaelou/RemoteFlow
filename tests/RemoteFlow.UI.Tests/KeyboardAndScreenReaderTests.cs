using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using RemoteFlow.Domain.Enums;
using RemoteFlow.TestSupport;
using RemoteFlow.UI.Navigation;
using RemoteFlow.UI.Services;
using RemoteFlow.UI.ViewModels;
using RemoteFlow.UI.ViewModels.Connections;
using RemoteFlow.UI.Views;
using Xunit;

namespace RemoteFlow.UI.Tests;

public sealed class KeyboardAndScreenReaderTests
{
    private const string _avalonia = "https://github.com/avaloniaui";

    /// <summary>
    /// Arrow keys walk the sidebar and take the page with them; Enter is the commit, and the commit hands
    /// the keyboard to the page. Without this a keyboard-only user reaches a page and is still in the
    /// sidebar, and a screen reader never says which page opened.
    /// </summary>
    [AvaloniaFact]
    public void CommittingANavigationMovesFocusIntoTheNamedPageRegion()
    {
        var navigation = NavigationService.CreateDefault();
        var window = new MainWindow(
            new MainWindowViewModel(navigation),
            new WindowGeometryService(new InMemorySettingsStore()));
        window.Show();
        var list = window.FindControl<ListBox>("NavigationList");
        var host = window.FindControl<ContentControl>("PageHost");
        Assert.NotNull(list);
        Assert.NotNull(host);
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
        Assert.True(host.IsFocused, "Enter did not hand the keyboard to the page.");
        Assert.Equal("Terminals", global::Avalonia.Automation.AutomationProperties.GetName(host));
        window.Close();
    }

    /// <summary>
    /// Avalonia presses a focused button with Space, and with Enter only when it is the window default.
    /// Someone who has tabbed to "New connection" will press Enter, and did: the button did nothing and
    /// the next keystroke moved focus off it.
    /// </summary>
    [AvaloniaFact]
    public void EnterPressesTheButtonTheKeyboardIsOn()
    {
        var clicks = 0;
        var button = new Button { Content = "Connect" };
        button.Click += (_, _) => clicks++;
        var window = new Window { Content = button };
        window.Show();
        _ = button.Focus(NavigationMethod.Tab);

        button.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Enter,
            PhysicalKey = PhysicalKey.Enter,
        });

        Assert.Equal(1, clicks);
        window.Close();
    }

    /// <summary>
    /// The tab strip is an ItemsControl of borders rather than a real tab control, so nothing makes its
    /// tabs focusable or named by default. They are the only way to see which sessions exist.
    /// </summary>
    [Fact]
    public void EveryTerminalTabTakesFocusAndSaysWhichSessionItIs()
    {
        var tab = TerminalWorkspaceElement(
            element => element.Name.NamespaceName == _avalonia &&
                element.Name.LocalName == "Border" &&
                element.Attribute("KeyDown")?.Value == "Tab_OnKeyDown");

        Assert.Equal("True", tab.Attribute("Focusable")?.Value);
        Assert.Equal("{Binding TabAccessibleName}", tab.Attribute("AutomationProperties.Name")?.Value);
    }

    /// <summary>
    /// A terminal is a custom control drawing its own text. Left alone it reaches a screen reader as an
    /// unnamed element of no particular kind, which is the one thing the accessibility pass has to stop.
    /// </summary>
    [Fact]
    public void TheTerminalSurfaceAnnouncesItselfAsTextAndNamesItsSession()
    {
        var terminal = TerminalWorkspaceElement(element => element.Name.LocalName == "TerminalControl");

        Assert.Equal("Text", terminal.Attribute("AutomationProperties.ControlTypeOverride")?.Value);
        Assert.Equal("{Binding TerminalAccessibleName}", terminal.Attribute("AutomationProperties.Name")?.Value);
    }

    /// <summary>
    /// Environment is the one thing on screen a mistake about is expensive — typing into production
    /// believing it is staging. It is shown in colour, so every environment also has to carry words.
    /// </summary>
    [Fact]
    public void EveryEnvironmentCarriesTextAndNotJustAColour()
    {
        // Unspecified only earns a badge when the user picked a colour for it — which is exactly the case
        // where the colour would otherwise be the whole message.
        var badges = Enum.GetValues<EnvironmentKind>()
            .Select(environment => ExplorerNodeViewModel.CreateBadge(environment, "#6CB6FF"))
            .ToArray();

        Assert.All(badges, badge =>
        {
            Assert.NotNull(badge);
            Assert.False(string.IsNullOrWhiteSpace(badge.Text));
            Assert.False(string.IsNullOrWhiteSpace(badge.Icon));
        });
        Assert.Equal(
            badges.Length,
            badges.Select(badge => badge!.Text).Distinct(StringComparer.Ordinal).Count());
    }

    private static XElement TerminalWorkspaceElement(Func<XElement, bool> predicate)
    {
        var path = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "RemoteFlow.UI",
            "Views",
            "Terminal",
            "TerminalWorkspace.axaml");
        return XDocument.Load(path).Descendants().Single(predicate);
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
