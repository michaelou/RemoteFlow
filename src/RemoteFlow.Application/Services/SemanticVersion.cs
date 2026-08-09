using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace RemoteFlow.Application.Services;

/// <summary>A SemVer 2.0 version, parsed far enough to answer one question: is the release on the project
/// page newer than the build that is running?
///
/// Comparing version strings by hand gets this wrong in exactly the case that matters. <c>0.10.0</c> sorts
/// before <c>0.9.0</c> as text, and a release candidate has to lose to the release it precedes —
/// <c>0.1.0-rc.1</c> is older than <c>0.1.0</c>, not newer, even though the string is longer. Build
/// metadata after <c>+</c> is dropped, because SemVer says it carries no precedence, and RemoteFlow's own
/// informational version puts the commit hash there.</summary>
public sealed record SemanticVersion : IComparable<SemanticVersion>, IComparable
{
    private SemanticVersion(int major, int minor, int patch, string prerelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    /// <summary>The part after the first <c>-</c>, or empty for a stable release. A version that has one
    /// ranks below the same version without one.</summary>
    public string Prerelease { get; }

    public bool IsPrerelease => Prerelease.Length > 0;

    /// <summary>Parses <c>1.2.3</c>, <c>v1.2.3</c>, <c>1.2.3-rc.1</c>, or any of those with <c>+metadata</c>
    /// appended. A leading <c>v</c> is accepted because that is how the tags are written, and the tag name
    /// is what the release API returns.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out SemanticVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var remaining = text.Trim().AsSpan();
        if (remaining.Length > 0 && (remaining[0] == 'v' || remaining[0] == 'V'))
        {
            remaining = remaining[1..];
        }

        var buildSeparator = remaining.IndexOf('+');
        if (buildSeparator >= 0)
        {
            remaining = remaining[..buildSeparator];
        }

        var prerelease = string.Empty;
        var prereleaseSeparator = remaining.IndexOf('-');
        if (prereleaseSeparator >= 0)
        {
            prerelease = remaining[(prereleaseSeparator + 1)..].ToString();
            remaining = remaining[..prereleaseSeparator];
            if (prerelease.Length == 0)
            {
                return false;
            }
        }

        Span<Range> parts = stackalloc Range[4];
        var partCount = remaining.Split(parts, '.');
        if (partCount != 3)
        {
            return false;
        }

        if (!TryParseNumber(remaining[parts[0]], out var major) ||
            !TryParseNumber(remaining[parts[1]], out var minor) ||
            !TryParseNumber(remaining[parts[2]], out var patch))
        {
            return false;
        }

        version = new SemanticVersion(major, minor, patch, prerelease);
        return true;
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var core = Major.CompareTo(other.Major);
        if (core != 0)
        {
            return core;
        }

        core = Minor.CompareTo(other.Minor);
        if (core != 0)
        {
            return core;
        }

        core = Patch.CompareTo(other.Patch);
        return core != 0 ? core : ComparePrerelease(Prerelease, other.Prerelease);
    }

    public int CompareTo(object? obj)
    {
        return obj switch
        {
            null => 1,
            SemanticVersion other => CompareTo(other),
            _ => throw new ArgumentException($"Expected a {nameof(SemanticVersion)}.", nameof(obj)),
        };
    }

    public override string ToString()
    {
        var core = string.Create(
            CultureInfo.InvariantCulture,
            $"{Major}.{Minor}.{Patch}");
        return IsPrerelease ? $"{core}-{Prerelease}" : core;
    }

    public static bool operator <(SemanticVersion? left, SemanticVersion? right)
    {
        return left is null ? right is not null : left.CompareTo(right) < 0;
    }

    public static bool operator <=(SemanticVersion? left, SemanticVersion? right)
    {
        return left is null || left.CompareTo(right) <= 0;
    }

    public static bool operator >(SemanticVersion? left, SemanticVersion? right)
    {
        return left is not null && left.CompareTo(right) > 0;
    }

    public static bool operator >=(SemanticVersion? left, SemanticVersion? right)
    {
        return left is null ? right is null : left.CompareTo(right) >= 0;
    }

    private static bool TryParseNumber(ReadOnlySpan<char> text, out int value)
    {
        value = 0;
        if (text.Length == 0)
        {
            return false;
        }

        foreach (var character in text)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>SemVer's precedence rules for the prerelease part: absent beats present, numeric
    /// identifiers compare as numbers and rank below alphanumeric ones, and when everything so far
    /// matches, the version with more identifiers wins.</summary>
    private static int ComparePrerelease(string left, string right)
    {
        if (left.Length == 0 && right.Length == 0)
        {
            return 0;
        }

        if (left.Length == 0)
        {
            return 1;
        }

        if (right.Length == 0)
        {
            return -1;
        }

        var leftParts = left.Split('.');
        var rightParts = right.Split('.');
        var shared = Math.Min(leftParts.Length, rightParts.Length);
        for (var index = 0; index < shared; index++)
        {
            var comparison = CompareIdentifier(leftParts[index], rightParts[index]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return leftParts.Length.CompareTo(rightParts.Length);
    }

    private static int CompareIdentifier(string left, string right)
    {
        var leftIsNumeric = TryParseNumber(left, out var leftNumber);
        var rightIsNumeric = TryParseNumber(right, out var rightNumber);

        return (leftIsNumeric, rightIsNumeric) switch
        {
            (true, true) => leftNumber.CompareTo(rightNumber),
            // A numeric identifier always ranks below an alphanumeric one, so 1.0.0-1 precedes 1.0.0-alpha.
            (true, false) => -1,
            (false, true) => 1,
            _ => string.CompareOrdinal(left, right),
        };
    }
}
