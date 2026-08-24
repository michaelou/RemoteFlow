namespace RemoteFlow.Application.Abstractions;

public enum VaultUnlockOutcome
{
    Unlocked = 1,

    /// <summary>The passphrase did not open the vault. Indistinguishable, by design, from a vault file that
    /// has been tampered with or truncated — the cryptography cannot tell you which, and guessing would be
    /// worse than saying so.</summary>
    IncorrectPassphrase = 2,

    /// <summary>Something other than the passphrase went wrong: the file could not be read, or a new vault
    /// could not be written.</summary>
    Failed = 3,
}

/// <summary>A credential store that must be opened before it will answer. Ordinary providers — the Windows
/// credential manager, the macOS keychain, libsecret — are unlocked by the operating system as part of the
/// user's login, so they do not implement this. The encrypted file vault does, and until something opens it
/// every read and write it is asked for fails.
///
/// The type is the signal: code that needs a usable credential store asks whether the selected provider is
/// an <see cref="ICredentialVault"/>, rather than comparing provider names.</summary>
public interface ICredentialVault
{
    bool IsUnlocked { get; }

    /// <summary>Whether a vault has been created yet. False means the next unlock will make one, which is a
    /// materially different thing to ask the user for: a passphrase to invent rather than one to recall.</summary>
    bool Exists { get; }

    /// <summary>Opens the vault, creating it when it does not exist yet. Reports failure rather than
    /// throwing, because a wrong passphrase is an ordinary thing for a person to do.</summary>
    Task<VaultUnlockOutcome> TryUnlockAsync(
        ReadOnlyMemory<char> passphrase,
        CancellationToken cancellationToken = default);
}
