using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace RemoteFlow.UI.Converters;

/// <summary>Resolves a resource key carried by a view model into the application resource it names, so
/// assets such as icon geometry stay declared in XAML instead of being built in the view model.</summary>
public sealed class ResourceKeyConverter : IValueConverter
{
    public static ResourceKeyConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is string key
            && Avalonia.Application.Current is { } application
            && application.TryFindResource(key, out var resource)
            ? resource
            : null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException("Resource keys are resolved one way only.");
    }
}
