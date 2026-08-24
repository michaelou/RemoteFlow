using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Backup;
using RemoteFlow.Domain.Common;

namespace RemoteFlow.Application.Services.Backup;

public sealed class AutoBackupPassphraseStore(
    ICredentialProviderSelector selector,
    IEnumerable<ICredentialProvider> providers) : IAutoBackupPassphraseStore
{
    /// <summary>Deliberately outside <c>remoteflow/connection/...</c>. This is not a connection credential,
    /// and filing it under that prefix would present it as one to anything that later walks those keys.
    /// Changing this string orphans every stored passphrase, which is silent data loss — a test pins it.</summary>
    public const string StoreKey = "remoteflow/auto-backup/passphrase";

    private const string _displayName = "RemoteFlow automatic backup passphrase";

    private readonly ICredentialProviderSelector _selector = selector ?? throw new ArgumentNullException(nameof(selector));
    private readonly IReadOnlyList<ICredentialProvider> _providers = [.. providers ?? throw new ArgumentNullException(nameof(providers))];

    /// <summary>Whether any credential store exists at all. Note this is weaker than "usable": the file
    /// vault reports itself available and then refuses every read until it is unlocked, which is what
    /// <see cref="InspectAsync"/> is for.</summary>
    public bool IsAvailable => _providers.Any(IsUsable);

    public async Task<string> GetProviderNameAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var provider = await _selector.SelectAsync(cancellationToken).ConfigureAwait(false);
            return provider.Name;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return "your system credential store";
        }
    }

    public async Task<AutoBackupPassphraseState> InspectAsync(CancellationToken cancellationToken = default)
    {
        ICredentialProvider selected;
        try
        {
            selected = await _selector.SelectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new AutoBackupPassphraseState(false, Describe(exception));
        }

        // Whether the store is usable is read as a fact, not inferred from a failed lookup. A read can fail
        // for reasons that say nothing about the store's health, and reporting every one of those as
        // "unusable" is how a perfectly good Windows credential manager got blamed for a locked file vault
        // sitting unused further down the provider list.
        if (selected is ICredentialVault { IsUnlocked: false })
        {
            return new AutoBackupPassphraseState(false, "The credential vault is locked.");
        }

        using var handle = await LookUpAsync(cancellationToken).ConfigureAwait(false);
        return handle is not null ? AutoBackupPassphraseState.Present : AutoBackupPassphraseState.Missing;
    }

    public Task<SecretHandle?> GetAsync(CancellationToken cancellationToken = default)
    {
        return LookUpAsync(cancellationToken);
    }

    /// <summary>Finds the passphrase, or returns null. The selected provider is tried first, then anything
    /// else that could hold one: unlike a connection credential there is no stored provider name to look up
    /// — putting one in the settings row would leak a machine-local fact into every exported archive — and
    /// flipping ForceFileVault changes the selection without moving what is already stored.
    ///
    /// A provider that fails is skipped rather than reported. Only the selected provider's own state says
    /// anything about whether the credential store works, and that is read directly in
    /// <see cref="InspectAsync"/>.</summary>
    private async Task<SecretHandle?> LookUpAsync(CancellationToken cancellationToken)
    {
        ICredentialProvider selected;
        try
        {
            selected = await _selector.SelectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }

        var handle = await TryGetAsync(selected, cancellationToken).ConfigureAwait(false);
        if (handle is not null)
        {
            return handle;
        }

        foreach (var provider in _providers)
        {
            if (ReferenceEquals(provider, selected) || !CanHoldASecret(provider))
            {
                continue;
            }

            handle = await TryGetAsync(provider, cancellationToken).ConfigureAwait(false);
            if (handle is not null)
            {
                return handle;
            }
        }

        return null;
    }

    public async Task<Result<bool>> SetAsync(
        ReadOnlyMemory<char> passphrase,
        CancellationToken cancellationToken = default)
    {
        if (!PassphrasePolicy.IsStrong(passphrase.Span))
        {
            return Result<bool>.Failure(RemoteFlowError.Validation(
                "autobackup.weak_passphrase",
                PassphrasePolicy.Requirement));
        }

        // Broad on purpose. A credential provider is a platform integration — a locked vault, a refused
        // D-Bus call, a keychain denial — and it throws provider-specific types this layer cannot name.
        try
        {
            var provider = await _selector.SelectAsync(cancellationToken).ConfigureAwait(false);
            await provider.SetAsync(StoreKey, passphrase, _displayName, cancellationToken).ConfigureAwait(false);
            return Result<bool>.Success(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Result<bool>.Failure(RemoteFlowError.Unavailable(
                "autobackup.passphrase_store_unavailable",
                $"The passphrase could not be saved: {Describe(exception)}"));
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        // Every available provider, not just the selected one: a passphrase stored before the user flipped
        // to the file vault would otherwise be left behind, still decrypting old archives.
        foreach (var provider in _providers)
        {
            if (!IsUsable(provider))
            {
                continue;
            }

            try
            {
                await provider.DeleteAsync(StoreKey, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // One uncooperative provider must not stop the others being cleared.
            }
        }
    }

    private static async Task<SecretHandle?> TryGetAsync(
        ICredentialProvider provider,
        CancellationToken cancellationToken)
    {
        if (!CanHoldASecret(provider))
        {
            return null;
        }

        try
        {
            return await provider.GetAsync(StoreKey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Whether it is worth asking this provider anything. A locked vault answers every read with an
    /// exception, and <see cref="ICredentialProvider.IsAvailable"/> does not say so — the file vault reports
    /// itself available on every platform, including ones where nothing will ever open it.</summary>
    private static bool CanHoldASecret(ICredentialProvider provider)
    {
        return IsUsable(provider) && provider is not ICredentialVault { IsUnlocked: false };
    }

    private static bool IsUsable(ICredentialProvider provider)
    {
        try
        {
            return provider.IsAvailable;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Even the availability check reaches a platform API. One that answers by throwing is not one
            // to go on and ask for secrets.
            return false;
        }
    }

    /// <summary>The provider's own message. Credential providers throw types declared in the infrastructure
    /// layer, which this one cannot reference, so the message is all there is to go on — and it is written
    /// for a person ("The credential vault is locked.").</summary>
    private static string Describe(Exception exception)
    {
        return string.IsNullOrWhiteSpace(exception.Message)
            ? "The credential store could not be opened."
            : exception.Message;
    }
}
