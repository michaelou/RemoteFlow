using System.Globalization;
using RemoteFlow.Domain.Abstractions;

namespace RemoteFlow.Domain.Common;

internal static class DomainValidation
{
    internal static string? Required(string? value, int maximumLength, string code, out RemoteFlowError? error)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            error = RemoteFlowError.Validation(code, "A value is required.");
            return null;
        }

        if (normalized.Length > maximumLength)
        {
            error = RemoteFlowError.Validation(code, $"The value cannot exceed {maximumLength.ToString(CultureInfo.InvariantCulture)} characters.");
            return null;
        }

        error = null;
        return normalized;
    }

    internal static string? Optional(string? value, int maximumLength, string code, out RemoteFlowError? error)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (normalized?.Length > maximumLength)
        {
            error = RemoteFlowError.Validation(code, $"The value cannot exceed {maximumLength.ToString(CultureInfo.InvariantCulture)} characters.");
            return null;
        }

        error = null;
        return normalized;
    }

    internal static string? ColorHex(string? value, string code, out RemoteFlowError? error)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();
        if (normalized is null)
        {
            error = null;
            return null;
        }

        if (normalized.Length is not (7 or 9) || normalized[0] != '#' || !IsHex(normalized.AsSpan(1)))
        {
            error = RemoteFlowError.Validation(code, "The color must be #RRGGBB or #RRGGBBAA hexadecimal notation.");
            return null;
        }

        error = null;
        return normalized;
    }

    internal static DateTimeOffset Utc(DateTimeOffset value)
    {
        return value.ToUniversalTime();
    }

    internal static Guid NewRequiredGuid(IGuidProvider guidProvider)
    {
        ArgumentNullException.ThrowIfNull(guidProvider);
        var value = guidProvider.NewGuid();
        return value == Guid.Empty
            ? throw new InvalidOperationException("The GUID provider returned an empty GUID.")
            : value;
    }

    private static bool IsHex(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }
}
