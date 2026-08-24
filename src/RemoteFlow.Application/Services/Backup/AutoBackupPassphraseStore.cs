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
        var (handle, problem) = await LookUpAsync(cancellationToken).ConfigureAwait(false);
        using (handle)
        {
            return problem is not null
                ? new AutoBackupPassphraseState(false, problem)
                : handle is not null
                    ? AutoBackupPassphraseState.Present
                    : AutoBackupPassphraseState.Missing;
        }
    }

    public async Task<SecretHandle?> GetAsync(CancellationToken cancellationToken = default)
    {
        var (handle, _) = await LookUpAsync(cancellationToken).ConfigureAwait(false);
        return handle;
    }

    /// <summary>Finds the passphrase, or explains why it could not look. The selected provider is tried
    /// first, then anything else available: unlike a connection credential there is no stored provider name
    /// to look up — putting one in the settings row would leak a machine-local fact into every exported
    /// archive — and flipping ForceFileVault changes the selection without moving what is already stored.</summary>
    private async Task<(SecretHandle? Handle, string? Problem)> LookUpAsync(CancellationToken cancellationToken)
    {
        ICredentialProvider selected;
        try
        {
            selected = await _selector.SelectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return (null, Describe(exception));
        }

        var (handle, problem) = await TryGetAsync(selected, cancellationToken).ConfigureAwait(false);
        if (handle is not null)
        {
            return (handle, null);
        }

        foreach (var provider in _providers)
        {
            if (!IsUsable(provider) || ReferenceEquals(provider, selected))
            {
                continue;
            }

            var (fallback, fallbackProblem) = await TryGetAsync(provider, cancellationToken).ConfigureAwait(false);
            if (fallback is not null)
            {
                return (fallback, null);
            }

            problem ??= fallbackProblem;
        }

        return (null, problem);
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

    private static async Task<(SecretHandle? Handle, string? Problem)> TryGetAsync(
        ICredentialProvider provider,
        CancellationToken cancellationToken)
    {
        if (!IsUsable(provider))
        {
            return (null, null);
        }

        try
        {
            return (await provider.GetAsync(StoreKey, cancellationToken).ConfigureAwait(false), null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Reported rather than swallowed: a store that will not open is a different problem from one
            // that simply holds no passphrase, and only one of the two is fixed by typing a new one.
            return (null, Describe(exception));
        }
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
