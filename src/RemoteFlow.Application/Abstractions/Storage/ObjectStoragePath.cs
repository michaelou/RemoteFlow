namespace RemoteFlow.Application.Abstractions.Storage;

/// <summary>Paths in the account-rooted object-storage model, kept separate from <c>SftpPath</c> on
/// purpose. <c>SftpPath.Normalize</c> strips a trailing slash, and here a trailing slash is
/// load-bearing: <c>a/</c> is a prefix marker and <c>a</c> is an object. The algorithm is reused; the
/// type is not.</summary>
public static class ObjectStoragePath
{
    public const string Root = "/";

    public const char Separator = '/';

    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var canonical = path.Replace('\\', Separator);
        var trailing = canonical.Length > 1 && canonical[^1] == Separator;
        var segments = new List<string>();

        foreach (var segment in canonical.Split(Separator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }

                continue;
            }

            segments.Add(segment);
        }

        return segments.Count == 0
            ? Root
            : Separator + string.Join(Separator, segments) + (trailing ? Separator.ToString() : string.Empty);
    }

    public static string Combine(string parent, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parent);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Normalize(parent.TrimEnd(Separator, '\\') + Separator + name);
    }

    public static bool IsRoot(string path)
    {
        return Normalize(path) == Root;
    }

    public static string GetName(string path)
    {
        var normalized = Normalize(path).TrimEnd(Separator);
        if (normalized.Length == 0)
        {
            return Root;
        }

        var index = normalized.LastIndexOf(Separator);
        return index < 0 ? normalized : normalized[(index + 1)..];
    }

    /// <summary>The parent of a path, or null at the account root.</summary>
    public static string? GetParent(string path)
    {
        var normalized = Normalize(path).TrimEnd(Separator);
        if (normalized.Length == 0)
        {
            return null;
        }

        var index = normalized.LastIndexOf(Separator);
        return index <= 0 ? Root : normalized[..index];
    }

    /// <summary>Splits an account-rooted path into the container it names and the key inside it. The
    /// account root gives (null, ""), a container root gives (name, ""), and the key never starts with a
    /// separator.</summary>
    public static (string? Container, string Key) Split(string path)
    {
        var normalized = Normalize(path);
        if (normalized == Root)
        {
            return (null, string.Empty);
        }

        var body = normalized[1..];
        var index = body.IndexOf(Separator, StringComparison.Ordinal);
        return index < 0
            ? (body, string.Empty)
            : (body[..index], body[(index + 1)..]);
    }

    /// <summary>The key a folder marker lives at: the key with exactly one trailing separator. An empty
    /// key is the container root, which has no marker.</summary>
    public static string AsPrefix(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var trimmed = key.TrimEnd(Separator);
        return trimmed.Length == 0 ? string.Empty : trimmed + Separator;
    }
}
