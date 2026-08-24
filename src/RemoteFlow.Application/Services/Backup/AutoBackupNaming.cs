using System.Globalization;

namespace RemoteFlow.Application.Services.Backup;

/// <summary>Names automatic backup archives, and — far more importantly — decides which files are ours.
/// Retention deletes files from a folder the user chose, which may hold anything else they keep there, so
/// the parser below is the whole safety boundary: a name it rejects is never a candidate for deletion.
/// Every rule here is deliberately strict. Loosening one widens what retention is allowed to destroy.</summary>
public static class AutoBackupNaming
{
    public const string Prefix = "remoteflow-auto-";

    /// <summary>The <c>.zip</c> keeps every operating system able to open the archive by double-clicking.
    /// The <c>.rfbak</c> in front of it is the token retention matches on, so an unrelated <c>.zip</c> —
    /// including a manual export — is never eligible for pruning.</summary>
    public const string Suffix = ".rfbak.zip";

    /// <summary>Appended while an archive is still being written. Never parses as a finished archive, so a
    /// torn upload is neither counted toward retention nor pruned by it; the startup sweep clears it.</summary>
    public const string PartialSuffix = ".part";

    public const string SearchPattern = Prefix + "*" + Suffix;

    private const int _nonceLength = 8;

    /// <summary>Sorts lexicographically in the same order it sorts chronologically, which is why retention
    /// can order by name and never has to trust a timestamp from the destination. S3 exposes no
    /// modification time you control, and an SFTP server's clock can simply be wrong.</summary>
    private const string _timestampFormat = "yyyyMMdd'T'HHmmss'Z'";

    private const int _timestampLength = 16;

    public static string Create(DateTimeOffset createdUtc, Guid nonce)
    {
        var timestamp = createdUtc.ToUniversalTime().ToString(_timestampFormat, CultureInfo.InvariantCulture);
        // Eight hex characters keep two changes within the same second distinct, and keep two machines
        // writing to one shared destination from ever choosing the same name.
        var nonceText = nonce.ToString("N", CultureInfo.InvariantCulture)[.._nonceLength];
        return string.Concat(Prefix, timestamp, "-", nonceText, Suffix);
    }

    public static bool TryParse(string? name, out DateTimeOffset createdUtc)
    {
        createdUtc = default;
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        // Ordinal throughout: a culture-aware comparison can equate strings a byte comparison would not,
        // and "is this file ours" must not depend on the machine's locale.
        if (!name.StartsWith(Prefix, StringComparison.Ordinal) ||
            !name.EndsWith(Suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var middle = name.AsSpan(Prefix.Length, name.Length - Prefix.Length - Suffix.Length);
        return middle.Length == _timestampLength + 1 + _nonceLength &&
            middle[_timestampLength] == '-' &&
            IsLowercaseHex(middle[(_timestampLength + 1)..]) &&
            DateTimeOffset.TryParseExact(
            middle[.._timestampLength],
            _timestampFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out createdUtc);
    }

    public static bool IsAutoBackupName(string? name)
    {
        return TryParse(name, out _);
    }

    private static bool IsLowercaseHex(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            var isHex = character is (>= '0' and <= '9') or (>= 'a' and <= 'f');
            if (!isHex)
            {
                return false;
            }
        }

        return true;
    }
}
