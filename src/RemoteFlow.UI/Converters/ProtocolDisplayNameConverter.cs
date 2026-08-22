using System.Globalization;
using Avalonia.Data.Converters;
using RemoteFlow.Domain.Enums;

namespace RemoteFlow.UI.Converters;

/// <summary>Puts a readable protocol name in front of a person. Binding an enum straight into a list
/// shows its member name, and "AzureBlob" is not what anybody calls the product.</summary>
public sealed class ProtocolDisplayNameConverter : IValueConverter
{
    public static ProtocolDisplayNameConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is ProtocolType protocol ? protocol.GetDisplayName() : null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException("Protocol names are resolved one way only.");
    }
}
