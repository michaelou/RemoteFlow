using System.Xml.Linq;
using Xunit;

namespace RemoteFlow.UI.Tests;

/// <summary>
/// A screen reader announces a control by its name. A button whose only content is a glyph — an arrow, a
/// multiplication sign, a chevron — announces that glyph or nothing at all, and a tooltip is not a name:
/// it is never read when the control takes focus by keyboard. This audit reads every view as XML and
/// fails on any actionable control that would reach a user unnamed, so a new icon button cannot be added
/// without one.
/// </summary>
public sealed class AccessibleNameAuditTests
{
    private const string _avalonia = "https://github.com/avaloniaui";

    /// <summary>Controls a user acts on, and which therefore have to announce what they do.</summary>
    private static readonly HashSet<string> _actionable = new(StringComparer.Ordinal)
    {
        "AutoCompleteBox",
        "Button",
        "CheckBox",
        "ComboBox",
        "NumericUpDown",
        "RadioButton",
        "RepeatButton",
        "Slider",
        "TextBox",
        "ToggleButton",
        "ToggleSwitch",
    };

    [Fact]
    public void EveryActionableControlInEveryViewHasAnAccessibleName()
    {
        var unnamed = new List<string>();
        foreach (var file in ViewFiles())
        {
            var document = XDocument.Load(file, LoadOptions.SetLineInfo);
            foreach (var element in document.Descendants().Where(element =>
                         element.Name.NamespaceName == _avalonia &&
                         _actionable.Contains(element.Name.LocalName)))
            {
                if (!HasAccessibleName(element))
                {
                    var line = ((System.Xml.IXmlLineInfo)element).LineNumber;
                    unnamed.Add($"{Path.GetFileName(file)}:{line} <{element.Name.LocalName}>");
                }
            }
        }

        Assert.True(
            unnamed.Count == 0,
            $"These controls would reach a screen reader unnamed. Give each one an " +
            $"AutomationProperties.Name, or a LabeledBy pointing at its visible label:{Environment.NewLine}" +
            string.Join(Environment.NewLine, unnamed));
    }

    private static bool HasAccessibleName(XElement element)
    {
        // An explicit name, or a label pointing at one, always wins.
        if (Attribute(element, "AutomationProperties.Name") is not null ||
            Attribute(element, "AutomationProperties.LabeledBy") is not null ||
            element.Element(XName.Get("AutomationProperties.Name", _avalonia)) is not null)
        {
            return true;
        }

        // Visible text is a name: Avalonia's automation peers fall back to the content. A binding is
        // assumed to produce real text, because the alternative is naming every bound label twice.
        var content = Attribute(element, "Content") ?? Attribute(element, "Header");
        return content is not null &&
            (content.StartsWith('{') || content.Any(char.IsLetterOrDigit));
    }

    private static string? Attribute(XElement element, string name)
    {
        return element.Attribute(name)?.Value;
    }

    private static IEnumerable<string> ViewFiles()
    {
        var views = Path.Combine(FindRepositoryRoot(), "src", "RemoteFlow.UI");
        return Directory.EnumerateFiles(views, "*.axaml", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);
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
