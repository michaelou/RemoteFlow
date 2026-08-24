using RemoteFlow.Application.Abstractions;

namespace RemoteFlow.Application.Services;

public sealed record VaultUnlockStatus
{
    /// <summary>True when the credential store is usable — including the ordinary case where it was never
    /// locked, because the operating system opens it as part of signing in.</summary>
    public required bool IsUsable { get; init; }

    /// <summary>True when a vault actually had to be opened. Lets a caller stay silent on Windows and macOS,
    /// where nothing was ever asked of the user.</summary>
    public bool WasPrompted { get; init; }

    /// <summary>Why the store is not usable, phrased for a person. Null when it is.</summary>
    public string? Problem { get; init; }

    public static VaultUnlockStatus Ready { get; } = new() { IsUsable = true };
}

public interface IVaultUnlockService
{
    /// <summary>Opens the credential vault if the selected provider is one and it is closed. Safe to call
    /// more than once; a vault that is already open costs nothing.</summary>
    Task<VaultUnlockStatus> EnsureUnlockedAsync(CancellationToken cancellationToken = default);
}

/// <summary>Gets the credential store open before anything needs it.
///
/// Only the encrypted file vault needs this, and it is reached only when the platform's own store is
/// missing — libsecret absent on Linux, or the file vault forced in settings. Everywhere else this returns
/// immediately having asked the user nothing, which is why the check is "is the selected provider an
/// <see cref="ICredentialVault"/>" rather than a platform test.</summary>
public sealed class VaultUnlockService(
    ICredentialProviderSelector selector,
    IVaultUnlockPrompt prompt) : IVaultUnlockService, IDisposable
{
    /// <summary>How many times a passphrase may be retyped before the prompt gives up for this session. The
    /// vault's own key derivation is the real brute-force defence; this only stops an endless dialog loop.</summary>
    public const int MaximumAttempts = 3;

    private readonly ICredentialProviderSelector _selector = selector ?? throw new ArgumentNullException(nameof(selector));
    private readonly IVaultUnlockPrompt _prompt = prompt ?? throw new ArgumentNullException(nameof(prompt));
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<VaultUnlockStatus> EnsureUnlockedAsync(CancellationToken cancellationToken = default)
    {
        // One prompt at a time. Two callers arriving together — startup and a page that wants credentials —
        // must not stack two dialogs asking the same question.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ICredentialProvider provider;
            try
            {
                provider = await _selector.SelectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return new VaultUnlockStatus { IsUsable = false, Problem = exception.Message };
            }

            // The platform store opens itself as part of signing in, so there is nothing to ask about it.
            return provider is not ICredentialVault vault || vault.IsUnlocked
                ? VaultUnlockStatus.Ready
                : await UnlockAsync(vault, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    private async Task<VaultUnlockStatus> UnlockAsync(ICredentialVault vault, CancellationToken cancellationToken)
    {
        // Read once, before the first prompt: creating the vault flips it, and the loop should keep asking
        // the question it started with rather than changing its wording underneath the user.
        var isNewVault = !vault.Exists;
        string? problem = null;
        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            using var answer = await _prompt.PromptAsync(
                new VaultUnlockPromptRequest { IsNewVault = isNewVault, Attempt = attempt, Problem = problem },
                cancellationToken).ConfigureAwait(false);
            if (answer is null)
            {
                // Declining is an answer. RemoteFlow still runs; it just cannot remember secrets this session.
                return new VaultUnlockStatus
                {
                    IsUsable = false,
                    WasPrompted = true,
                    Problem = isNewVault
                        ? "No credential vault has been set up, so RemoteFlow cannot save passwords or keys."
                        : "The credential vault was not unlocked, so saved passwords and keys are unavailable.",
                };
            }

            var outcome = await vault
                .TryUnlockAsync(answer.Secret.Secret, cancellationToken).ConfigureAwait(false);
            switch (outcome)
            {
                case VaultUnlockOutcome.Unlocked:
                    return new VaultUnlockStatus { IsUsable = true, WasPrompted = true };
                case VaultUnlockOutcome.IncorrectPassphrase:
                    problem = "That passphrase did not unlock the vault.";
                    break;
                case VaultUnlockOutcome.Failed:
                default:
                    // Not something retyping fixes — an unreadable file, a directory that cannot be written.
                    return new VaultUnlockStatus
                    {
                        IsUsable = false,
                        WasPrompted = true,
                        Problem = "The credential vault could not be opened.",
                    };
            }
        }

        return new VaultUnlockStatus
        {
            IsUsable = false,
            WasPrompted = true,
            Problem = $"The credential vault stayed locked after {MaximumAttempts} attempts.",
        };
    }

    public void Dispose()
    {
        _gate.Dispose();
    }
}
