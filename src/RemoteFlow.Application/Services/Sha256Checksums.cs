using System.Diagnostics.CodeAnalysis;

namespace RemoteFlow.Application.Services;

/// <summary>Reads the <c>checksums.txt</c> published beside every release, so a downloaded installer can be
/// proved to be the file the release page names before anything runs it.
///
/// The format is whatever <c>sha256sum</c> writes, because that is what produces the file: one line per
/// artefact, a 64-character hex digest, two spaces, then a bare filename. That shape is now an interface
/// rather than something only a human reads, which is why the release workflow asserts it and why this
/// parser is deliberately strict about the digest and forgiving about everything else — a release must not
/// become uninstallable over a trailing blank line.</summary>
public static class Sha256Checksums
{
    /// <summary>The most a <c>checksums.txt</c> is allowed to be. It lists a handful of files and is read
    /// into memory, so a response larger than this is not the file that was asked for.</summary>
    public const int MaximumSizeInBytes = 64 * 1024;

    private const int _digestLength = 64;

    /// <summary>Finds the digest recorded for one filename, or returns false when the file lists no such
    /// name. The digest is returned lower-case whatever case it was written in, and the filename is matched
    /// without regard to case, because the comparison that matters happens against a hash this application
    /// computed rather than against the text.</summary>
    public static bool TryFind(
        string content,
        string fileName,
        [NotNullWhen(true)] out string? sha256)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(fileName);
        sha256 = null;

        // Written on a Linux runner, so the line endings are LF — but a file that has been through an
        // editor or a checkout with autocrlf on arrives with CRLF, and neither is worth failing over.
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.AsSpan().Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            // GNU sha256sum prefixes a line with a backslash and escapes the filename when it contains a
            // newline or a backslash. RemoteFlow's artefact names never do, so this is a line we do not
            // understand rather than one to write an unescaper for.
            if (line[0] == '\\')
            {
                continue;
            }

            if (!TryReadDigest(line, out var digest, out var remainder))
            {
                continue;
            }

            // Text mode separates with two spaces and binary mode with " *". Splitting on whitespace and
            // then dropping a leading asterisk accepts both without caring which was used.
            var name = remainder.Trim();
            if (name.Length > 0 && name[0] == '*')
            {
                name = name[1..];
            }

            if (name.Length > 0 && name.Equals(fileName, StringComparison.OrdinalIgnoreCase))
            {
                sha256 = digest;
                return true;
            }
        }

        return false;
    }

    private static bool TryReadDigest(
        ReadOnlySpan<char> line,
        [NotNullWhen(true)] out string? digest,
        out ReadOnlySpan<char> remainder)
    {
        digest = null;
        remainder = default;
        if (line.Length <= _digestLength)
        {
            return false;
        }

        var candidate = line[.._digestLength];
        foreach (var character in candidate)
        {
            if (!char.IsAsciiHexDigit(character))
            {
                return false;
            }
        }

        // The digest has to be the whole first token: a 65-character one would otherwise pass by having its
        // first 64 characters read and the rest treated as the start of a filename.
        if (!char.IsWhiteSpace(line[_digestLength]))
        {
            return false;
        }

        digest = candidate.ToString().ToLowerInvariant();
        remainder = line[_digestLength..];
        return true;
    }
}
