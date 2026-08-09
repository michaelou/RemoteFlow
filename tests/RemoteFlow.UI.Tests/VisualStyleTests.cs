using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Xunit;

namespace RemoteFlow.UI.Tests;

/// <summary>
/// The look of the application is carried by tokens and style classes rather than by values repeated in
/// each view, which only works if the tokens actually reach the control that paints. A Fluent template
/// that stopped passing its corner radius through, or an icon whose path data no longer parses, would
/// leave the views unchanged and still be visibly wrong — so both are asserted here.
/// </summary>
public sealed class VisualStyleTests
{
    /// <summary>The radius every control that draws its own box is expected to settle on.</summary>
    private static readonly CornerRadius _control = new(10);

    [AvaloniaFact]
    public void ControlsTakeTheRoundedCornerToken()
    {
        var button = new Button { Content = "Connect" };
        var textBox = new TextBox();
        var comboBox = new ComboBox();
        var panel = new StackPanel { Children = { button, textBox, comboBox } };
        var window = Show(panel);

        Assert.Equal(_control, button.CornerRadius);
        Assert.Equal(_control, textBox.CornerRadius);
        Assert.Equal(_control, comboBox.CornerRadius);

        // The control property is only half of it: the template has to hand the radius to the part that
        // paints the box, or the corners stay square however the token is set.
        var painted = button.GetVisualDescendants().OfType<ContentPresenter>().First();
        Assert.Equal(_control, painted.CornerRadius);
        window.Close();
    }

    [AvaloniaFact]
    public void ThePanelClassPaintsACard()
    {
        var panel = new Border { Classes = { "panel" } };
        var window = Show(panel);

        Assert.Equal(new CornerRadius(14), panel.CornerRadius);
        Assert.True(global::Avalonia.Application.Current!.TryGetResource(
            "Brush.Surface.1",
            ThemeVariant.Dark,
            out var surface));
        Assert.Equal(
            Assert.IsAssignableFrom<ISolidColorBrush>(surface).Color,
            Assert.IsAssignableFrom<ISolidColorBrush>(panel.Background).Color);
        window.Close();
    }

    /// <summary>
    /// Icon geometry is parsed from path data the first time it is resolved, so a typo in a glyph reaches a
    /// user as an empty button rather than as a build failure. This resolves every one of them.
    /// </summary>
    [AvaloniaFact]
    public void EveryIconResolvesToGeometry()
    {
        var application = global::Avalonia.Application.Current!;
        var keys = IconKeys();

        Assert.NotEmpty(keys);
        foreach (var key in keys)
        {
            Assert.True(application.TryFindResource(key, out var value), $"{key} is missing.");
            var geometry = Assert.IsAssignableFrom<Geometry>(value);
            Assert.True(
                geometry.Bounds.Width > 0 && geometry.Bounds.Height > 0,
                $"{key} parsed to nothing, so it would draw as an empty square.");
        }
    }

    /// <summary>
    /// View models name glyphs by resource key — the sidebar's pages, the explorer's rows — and a key that
    /// matches nothing resolves to null, which draws as an empty space rather than throwing. This resolves
    /// every key the code hands to the converter.
    /// </summary>
    [AvaloniaFact]
    public void EveryIconKeyNamedInCodeResolves()
    {
        var application = global::Avalonia.Application.Current!;
        var keys = IconKeysNamedInCode();

        Assert.NotEmpty(keys);
        foreach (var key in keys)
        {
            Assert.True(
                application.TryFindResource(key, out var value) && value is Geometry,
                $"{key} is named in code but is not a glyph in Icons.axaml.");
        }
    }

    private static Window Show(Control content)
    {
        var window = new Window { Content = content, Width = 400, Height = 300 };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    private static IReadOnlyList<string> IconKeys()
    {
        var icons = Path.Combine(FindRepositoryRoot(), "src", "RemoteFlow.UI", "Styles", "Icons.axaml");
        return [.. XDocument.Load(icons).Root!
            .Elements()
            .Select(element => element.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value)
            .Where(key => key is not null)
            .Cast<string>()];
    }

    private static IReadOnlyList<string> IconKeysNamedInCode()
    {
        var source = Path.Combine(FindRepositoryRoot(), "src", "RemoteFlow.UI");
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
                     .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            foreach (var match in Regex.Matches(File.ReadAllText(file), "\"(Icon\\.[A-Za-z]+)\""))
            {
                _ = keys.Add(((Match)match).Groups[1].Value);
            }
        }

        return [.. keys];
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
