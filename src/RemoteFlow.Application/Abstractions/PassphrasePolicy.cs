namespace RemoteFlow.Application.Abstractions;

/// <summary>The strength rule every passphrase the user invents is held to: a manual export's, the
/// automatic backup passphrase, and the credential vault's. It lives here rather than privately inside
/// whichever service happened to need it first, because a rule enforced in four places drifts and a rule
/// read from one place cannot.</summary>
public static class PassphrasePolicy
{
    public const int MinimumLength = 12;

    public const int MinimumCategories = 3;

    /// <summary>The sentence shown when a passphrase is refused. Shared so the export dialog and the
    /// Backup page cannot describe the same rule differently.</summary>
    public const string Requirement =
        "Use a passphrase of at least 12 characters with upper, lower, number, and symbol characters.";

    public static bool IsStrong(ReadOnlySpan<char> passphrase)
    {
        if (passphrase.Length < MinimumLength)
        {
            return false;
        }

        var categories = 0;
        categories += passphrase.ContainsAnyInRange('a', 'z') ? 1 : 0;
        categories += passphrase.ContainsAnyInRange('A', 'Z') ? 1 : 0;
        categories += passphrase.ContainsAnyInRange('0', '9') ? 1 : 0;
        foreach (var character in passphrase)
        {
            if (!char.IsLetterOrDigit(character))
            {
                categories++;
                break;
            }
        }

        return categories >= MinimumCategories;
    }
}
